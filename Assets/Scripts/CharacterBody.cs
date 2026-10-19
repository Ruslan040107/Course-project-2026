using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Character models with animations instead of the capsules (Quaternius Ultimate Modular Men, CC0):
// Terrorists (team 0) are Resources/Characters/T_Adventurer, Counter-Terrorists (team 1) CT_Swat.
// - The capsule and the other own renderers of the bot or player are hidden (forceRenderingOff); colliders stay.
// - The model is a child with its feet on the floor, facing the character's forward.
// - Legacy animation by state: Idle_Gun standing, Run / Run_Back / Run_Left / Run_Right by the direction of
//   movement, Walk while sneaking, playback speed follows the real speed. While a bot has a target (the player
//   always) the right arm takes the Idle_Gun_Pointing pose (a layer on the arm only) and the chest turns up/down
//   and sideways so the gun points where the head looks. A hit plays HitRecieve on top, a knife swing Sword_Slash.
// - WeaponMount: an empty object in the right fist where BotWeaponModels hangs the gun (grip in the fist,
//   barrel forward in the pointing pose); while aiming it is turned exactly along the aim.
// - The model of the character the camera is on (the living player, a watched bot) only casts a shadow.
// It adds itself to every bot and to the player of a scene with a RoundManager, never in simulations.
public class CharacterBody : MonoBehaviour
{
    [Header("Aim")]
    [Tooltip("the chest turns up/down at most this much, degrees")] public float maxAimPitch = 40f;
    [Tooltip("the chest turns sideways at most this much, degrees")] public float maxAimYaw = 45f;
    [Tooltip("how fast the arm goes up to aim and down again, 1/s")] public float aimBlendSpeed = 6f;
    [Tooltip("the gun kicks up by this angle on a shot, degrees")] public float recoilKick = 6f;

    [Header("Animation")]
    [Tooltip("m/s at which the Run clips look right (measured on the model)")] public float runAnimSpeed = 3f;
    [Tooltip("m/s at which the Walk clip looks right")] public float walkAnimSpeed = 1.35f;
    public float crossFade = 0.2f;
    [Range(0f, 1f)] public float hitWeight = 0.7f;

    public const string TModel = "Characters/T_Adventurer";
    public const string CTModel = "Characters/CT_Swat";
    public const float ModelHeight = 1.81f;

    // the model, its animation and the gun mount (null until Start, or when there is no model)
    public Transform Model => model;
    public Animation Anim => anim;
    public Transform WeaponMount => mount;
    public GameObject Prefab => prefab;
    public bool HasModel => model != null && model.gameObject.activeInHierarchy;

    Health health;
    BotController bot;
    PlayerController player;
    PlayerShoot shoot;
    NavMeshAgent agent;
    CharacterController cc;
    float startBaseOffset;

    GameObject prefab;
    Transform model;
    Animation anim;
    Transform torso, chest, mount;
    float modelY;        // local y of the model root with the feet at the bottom of the collider
    float bottom;        // local y of the bottom of the collider (the bot transform is the capsule centre)
    readonly List<Renderer> hidden = new List<Renderer>();

    // animation states
    string idle, idleKnife, run, runBack, runLeft, runRight, walk, current;
    AnimationState aimArm, slash, hit;
    bool moving;

    // aim and movement
    Vector3 velocity;
    Vector3 lastPos;
    bool hasLastPos;
    Vector3 aimDir = Vector3.forward;
    float aimWeight;
    float kick;
    float lastShot = -10f;

    // the chest bones are turned after the animation; if a clip does not write them, the turn is undone first
    Quaternion torsoBase, torsoSet, chestBase, chestSet;
    bool torsoTurned, chestTurned;
    Vector3 mountPos;
    Quaternion mountRot;

    // shadow only while the camera is on this character
    bool ownView;
    bool viewSet;
    float nextRendererCheck;

    static bool warned;
    static readonly List<Renderer> rendererBuffer = new List<Renderer>();
    static readonly RaycastHit[] hits = new RaycastHit[8];
    static readonly FieldInfo TargetField =
        typeof(BotController).GetField("target", BindingFlags.Instance | BindingFlags.NonPublic);

    // ---------------- bootstrap ----------------

    // one CharacterBody without an owner scans for new bots and the player
    static CharacterBody manager;
    static bool creatingManager;
    bool isManager;
    float nextScan;

    // Play mode starts without a domain reload: forget the last play (a model reimported in between is measured again)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        manager = null;
        creatingManager = false;
        warned = false;
        fits.Clear();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryCreate(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryCreate(scene);
    }

    static bool SimulationFlag()
    {
        return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-sim") >= 0;
    }

    // visual only: never in simulations, batch runs or without graphics
    static bool Allowed()
    {
        if (Application.isBatchMode || SimulationFlag()) return false;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return false;
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        return rm == null || !rm.simulation;
    }

    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || !Allowed()) return;
        // one manager per scene (the one of the previous scene may still exist while the next one loads)
        if (manager != null && manager.gameObject.scene == scene) return;

        RoundManager rm = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            rm = root.GetComponentInChildren<RoundManager>(true);
            if (rm != null) break;
        }
        if (rm == null || rm.simulation) return;

        GameObject go = new GameObject("Character Bodies");
        SceneManager.MoveGameObjectToScene(go, scene);
        creatingManager = true;
        go.AddComponent<CharacterBody>();
        creatingManager = false;
    }

    void Awake()
    {
        isManager = creatingManager;
        if (isManager) manager = this;

        // background runs pass -sim, RoundManager reads it only in its Start
        if (!Allowed()) enabled = false;
    }

    void Scan()
    {
        foreach (Health h in Health.All)
        {
            if (h == null || h.GetComponent<CharacterBody>() != null) continue;
            if (h.GetComponent<BotController>() == null && h.GetComponent<PlayerController>() == null) continue;
            h.gameObject.AddComponent<CharacterBody>();
        }
    }

    // ---------------- models (also used by Corpses) ----------------

    public static GameObject PrefabFor(int team)
    {
        return Resources.Load<GameObject>(team == 1 ? CTModel : TModel);
    }

    // where an instance sits under a parent of scale 1: feet at y 0, facing +Z, about 1.81 m tall
    struct Fit
    {
        public Quaternion rotation;
        public Vector3 scale;
        public float feet;
    }
    static readonly Dictionary<GameObject, Fit> fits = new Dictionary<GameObject, Fit>();

    // a copy of a character model under the parent: feet at the parent's origin, facing its forward,
    // opaque materials, without colliders
    public static GameObject SpawnModel(GameObject prefab, Transform parent)
    {
        GameObject go = Instantiate(prefab, parent, false);
        go.name = prefab.name;
        Transform t = go.transform;

        foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        FixMaterials(go);

        if (!fits.TryGetValue(prefab, out Fit fit))
        {
            fit = Measure(t, parent);
            fits[prefab] = fit;
        }
        float parentScale = parent != null ? Mathf.Max(parent.lossyScale.y, 1e-4f) : 1f;
        t.localRotation = fit.rotation;
        t.localScale = fit.scale / parentScale;
        t.localPosition = new Vector3(0f, fit.feet / parentScale, 0f);
        return go;
    }

    // measures the first instance: up and forward from the bones (a wrong axis is turned by 90 degree steps),
    // height and feet from the meshes in the import pose
    static Fit Measure(Transform t, Transform parent)
    {
        Quaternion baseRot = t.localRotation;
        Vector3 baseScale = t.localScale;
        t.localPosition = Vector3.zero;

        // directions in the parent's space; the turn is added in front of the root's own rotation
        Quaternion fix = Quaternion.identity;
        Transform head = FindBone(t, "Head");
        Transform footL = FindBone(t, "Foot.L");
        Transform footR = FindBone(t, "Foot.R");
        Transform toe = FindBone(t, "Foot.L_end");
        if (head != null && footL != null && footR != null)
        {
            Vector3 up = ToParent(parent, head.position - (footL.position + footR.position) * 0.5f);
            if (up.sqrMagnitude > 1e-6f && Vector3.Angle(up, Vector3.up) > 45f)
                fix = Snap(Quaternion.FromToRotation(up, Vector3.up));

            if (toe != null)
            {
                Vector3 toes = fix * ToParent(parent, toe.position - footL.position);
                toes.y = 0f;
                if (toes.sqrMagnitude > 1e-6f)
                {
                    float yaw = Vector3.SignedAngle(toes, Vector3.forward, Vector3.up);
                    if (Mathf.Abs(yaw) > 45f) fix = Quaternion.AngleAxis(Mathf.Round(yaw / 90f) * 90f, Vector3.up) * fix;
                }
            }
        }
        t.localRotation = fix * baseRot;

        // mesh corners in the parent's space
        bool any = false;
        float minY = 0f, maxY = 0f;
        foreach (SkinnedMeshRenderer r in t.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r.sharedMesh == null) continue;
            Bounds b = r.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                Vector3 w = r.transform.TransformPoint(b.center + Vector3.Scale(b.extents, corner));
                float y = parent != null ? parent.InverseTransformPoint(w).y : w.y;
                if (!any || y < minY) minY = y;
                if (!any || y > maxY) maxY = y;
                any = true;
            }
        }

        float k = 1f;
        float height = maxY - minY;
        if (any && height > 0.01f && (height < ModelHeight * 0.85f || height > ModelHeight * 1.15f)) k = ModelHeight / height;

        float parentScale = parent != null ? Mathf.Max(parent.lossyScale.y, 1e-4f) : 1f;
        return new Fit
        {
            rotation = fix * baseRot,
            scale = baseScale * k * parentScale,
            feet = any ? -minY * k * parentScale : 0f
        };
    }

    static Vector3 ToParent(Transform parent, Vector3 worldDir)
    {
        return parent != null ? parent.InverseTransformDirection(worldDir) : worldDir;
    }

    // the nearest rotation made of 90 degree turns
    static Quaternion Snap(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return Quaternion.Euler(Mathf.Round(e.x / 90f) * 90f, Mathf.Round(e.y / 90f) * 90f, Mathf.Round(e.z / 90f) * 90f);
    }

    public static Transform FindBone(Transform root, string name)
    {
        return root != null ? ViewModel.FindChild(root, name) : null;
    }

    // the state of a clip by its short name ("Run" also finds "CharacterArmature|Run"), null if there is none
    public static string FindState(Animation a, string shortName)
    {
        if (a == null) return null;
        if (a[shortName] != null) return shortName;
        string suffix = "|" + shortName;
        foreach (AnimationState s in a)
            if (s.name.EndsWith(suffix, System.StringComparison.Ordinal)) return s.name;
        return null;
    }

    // shows one moment of a clip (at = 0..1 of its length) and stops
    public static void SamplePose(Animation a, string state, float at)
    {
        if (a == null || state == null || a[state] == null) return;
        a.Stop();
        AnimationState s = a[state];
        s.enabled = true;
        s.weight = 1f;
        s.time = s.length * at;
        a.Sample();
        s.enabled = false;
    }

    // An empty object in the right fist where a gun is held: measured on the Idle_Gun_Pointing pose
    // (arm straight forward). The grip point is the middle of the fist: a bit past the knuckles
    // (Middle2.R), a little down and to the thumb side; the barrel looks along the model's forward.
    // Its world scale is 1, so a gun hung under it has its real size.
    public static Transform MakeWeaponMount(Transform modelRoot, Animation a)
    {
        Transform wrist = FindBone(modelRoot, "Wrist.R");
        Transform knuckle = FindBone(modelRoot, "Middle2.R");
        if (knuckle == null) knuckle = FindBone(modelRoot, "Index2.R");
        string pointing = FindState(a, "Idle_Gun_Pointing");
        if (wrist == null || knuckle == null || pointing == null) return null;

        SamplePose(a, pointing, 0.5f);

        Vector3 w = wrist.position;
        Vector3 d = knuckle.position - w;
        float length = d.magnitude;
        if (length < 1e-4f) return null;

        Vector3 up = modelRoot.up;
        Vector3 right = Vector3.Cross(up, d / length).normalized;
        Vector3 grip = w + d * 1.07f - up * (0.1f * length) - right * (0.165f * length);

        Transform m = new GameObject("WeaponMount").transform;
        m.SetParent(wrist, false);
        m.SetPositionAndRotation(grip, modelRoot.rotation);
        Vector3 s = wrist.lossyScale;
        m.localScale = new Vector3(1f / Mathf.Max(Mathf.Abs(s.x), 1e-4f), 1f / Mathf.Max(Mathf.Abs(s.y), 1e-4f), 1f / Mathf.Max(Mathf.Abs(s.z), 1e-4f));
        return m;
    }

    static readonly Dictionary<Material, Material> opaqueCopies = new Dictionary<Material, Material>();

    // the FBX materials have alpha 0; CharacterImport makes them opaque, this is the safety net at runtime
    public static void FixMaterials(GameObject go)
    {
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null) continue;
                bool transparent = (m.HasProperty("_Surface") && m.GetFloat("_Surface") > 0.5f) ||
                                   (m.HasProperty("_BaseColor") && m.GetColor("_BaseColor").a < 0.99f);
                if (!transparent) continue;

                if (!opaqueCopies.TryGetValue(m, out Material copy) || copy == null)
                {
                    Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                    copy = new Material(lit != null ? lit : m.shader) { name = m.name + " (opaque)" };
                    Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
                    c.a = 1f;
                    copy.SetColor("_BaseColor", c);
                    if (m.HasProperty("_BaseMap")) copy.SetTexture("_BaseMap", m.GetTexture("_BaseMap"));
                    copy.SetFloat("_Smoothness", 0.25f);
                    opaqueCopies[m] = copy;
                }
                mats[i] = copy;
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    // ---------------- one character ----------------

    void Start()
    {
        if (isManager) return;

        health = GetComponent<Health>();
        bot = GetComponent<BotController>();
        player = GetComponent<PlayerController>();
        agent = GetComponent<NavMeshAgent>();
        cc = GetComponent<CharacterController>();
        if (agent != null) startBaseOffset = agent.baseOffset;

        prefab = health != null ? PrefabFor(health.team) : null;
        if (prefab == null)
        {
            enabled = false;
            return;
        }

        GameObject go = SpawnModel(prefab, transform);
        anim = go.GetComponent<Animation>();
        if (anim == null || FindState(anim, "Idle_Gun") == null)
        {
            // a T-posing model sliding around is worse than the capsule: the import must be Legacy (CharacterImport)
            if (!warned)
            {
                warned = true;
                Debug.LogWarning("CharacterBody: " + prefab.name + " has no Legacy animation clips; reimport Assets/Resources/Characters");
            }
            Destroy(go);
            anim = null;
            enabled = false;
            return;
        }

        HideOwnRenderers(go.transform);

        model = go.transform;
        bottom = FeetY();
        modelY = model.localPosition.y + bottom;
        model.localPosition = new Vector3(0f, modelY, 0f);
        SetLayer(go, gameObject.layer);

        foreach (SkinnedMeshRenderer r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;

        anim.playAutomatically = false;
        anim.cullingType = player != null ? AnimationCullingType.AlwaysAnimate : AnimationCullingType.BasedOnRenderers;

        torso = FindBone(model, "Torso");
        chest = FindBone(model, "Chest");
        mount = MakeWeaponMount(model, anim);
        if (mount != null)
        {
            mountPos = mount.localPosition;
            mountRot = mount.localRotation;
        }

        idle = FirstState("Idle_Gun", "Idle");
        idleKnife = FirstState("Idle_Sword", "Idle_Gun", "Idle");
        run = FirstState("Run");
        runBack = FirstState("Run_Back", "Run");
        runLeft = FirstState("Run_Left", "Run");
        runRight = FirstState("Run_Right", "Run");
        walk = FirstState("Walk", "Run");
        foreach (string s in new[] { idle, idleKnife, run, runBack, runLeft, runRight, walk })
            if (s != null) anim[s].wrapMode = WrapMode.Loop;

        Transform shoulder = FindBone(model, "Shoulder.R");
        Transform abdomen = FindBone(model, "Abdomen");
        aimArm = AddLayer("Idle_Gun_Pointing", "AimArm", 1, shoulder, WrapMode.Loop, AnimationBlendMode.Blend);
        slash = AddLayer("Sword_Slash", "Slash", 2, abdomen, WrapMode.Once, AnimationBlendMode.Blend);
        hit = AddLayer("HitRecieve", "Hit", 3, abdomen, WrapMode.Once, AnimationBlendMode.Additive);

        anim.Stop();
        current = null;

        lastPos = transform.position;
        hasLastPos = true;
        RefreshRenderers();
    }

    string FirstState(params string[] names)
    {
        foreach (string n in names)
        {
            string s = FindState(anim, n);
            if (s != null) return s;
        }
        return null;
    }

    // a copy of a clip on a higher layer that moves only the bones under mix
    AnimationState AddLayer(string clipName, string name, int layer, Transform mix, WrapMode wrap, AnimationBlendMode mode)
    {
        string src = FindState(anim, clipName);
        if (src == null || mix == null) return null;
        if (anim[name] == null) anim.AddClip(anim[src].clip, name);
        AnimationState s = anim[name];
        if (s == null) return null;
        s.layer = layer;
        s.wrapMode = wrap;
        s.blendMode = mode;
        s.AddMixingTransform(mix, true);
        s.weight = 0f;
        s.enabled = false;
        return s;
    }

    // the local height of the bottom of the character's collider (the bot transform is the capsule centre)
    float FeetY()
    {
        if (cc != null) return cc.center.y - cc.height * 0.5f - cc.skinWidth;
        CapsuleCollider cap = GetComponent<CapsuleCollider>();
        if (cap != null && cap.direction == 1) return cap.center.y - cap.height * 0.5f;
        if (agent != null) return -agent.baseOffset;
        Collider c = GetComponent<Collider>();
        if (c != null) return transform.InverseTransformPoint(c.bounds.min).y;
        return -1f;
    }

    static void SetLayer(GameObject go, int layer)
    {
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    // the capsule and other shapes of the character; not the new model itself, not the first person weapon
    // (on the camera) and not the gun of BotWeaponModels
    void HideOwnRenderers(Transform newModel)
    {
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (r.transform.IsChildOf(newModel)) continue;
            if (r.GetComponentInParent<Camera>(true) != null || UnderWeaponModel(r.transform)) continue;
            r.forceRenderingOff = true;
            hidden.Add(r);
        }
    }

    bool UnderWeaponModel(Transform t)
    {
        for (; t != null && t != transform; t = t.parent)
            if (t.name == BotWeaponModels.HolderName) return true;
        return false;
    }

    void OnEnable()
    {
        if (isManager)
        {
            RoundManager.RoundStarted += Scan;
            return;
        }
        if (health == null) health = GetComponent<Health>();
        if (health != null) health.DamageTaken += OnDamageTaken;
        Weapon.AnyShot += OnShot;

        // after a respawn: no jump of the speed, the animation starts again
        hasLastPos = false;
        velocity = Vector3.zero;
        current = null;
        aimWeight = 0f;
        kick = 0f;
        torsoTurned = false;
        chestTurned = false;
    }

    void OnDisable()
    {
        if (isManager)
        {
            RoundManager.RoundStarted -= Scan;
            return;
        }
        if (health != null) health.DamageTaken -= OnDamageTaken;
        Weapon.AnyShot -= OnShot;
    }

    void OnDestroy()
    {
        if (isManager)
        {
            if (manager == this) manager = null;
            return;
        }
        foreach (Renderer r in hidden)
            if (r != null) r.forceRenderingOff = false;
        if (model != null) Destroy(model.gameObject);
    }

    void OnDamageTaken(float amount, Health attacker, bool headshot)
    {
        if (hit == null || amount <= 0f || health == null || health.IsDead) return;
        hit.enabled = true;
        hit.time = 0f;
        hit.weight = hitWeight;
    }

    void OnShot(ShotInfo s)
    {
        if (health == null || s.shooter != health) return;
        lastShot = Time.time;
        if (s.melee)
        {
            if (slash != null)
            {
                slash.enabled = true;
                slash.time = 0f;
                slash.weight = 1f;
            }
        }
        else
        {
            kick = Mathf.Min(kick + recoilKick, recoilKick * 2.5f);
        }
    }

    Weapon CurrentWeapon()
    {
        if (bot != null) return bot.weapon;
        if (shoot == null) shoot = GetComponent<PlayerShoot>();
        if (shoot != null && shoot.weapon != null) return shoot.weapon;
        return null;
    }

    // where the character aims: the player along his camera, a bot at its target (its head looks there)
    bool FindAim(out Vector3 dir)
    {
        dir = transform.forward;
        if (player != null)
        {
            if (player.cameraTransform != null) dir = player.cameraTransform.forward;
            return true;
        }
        if (bot == null || bot.head == null) return false;

        Health target = TargetField != null ? TargetField.GetValue(bot) as Health : null;
        if (target != null && !target.IsDead && target.gameObject.activeInHierarchy)
        {
            Collider c = target.GetComponent<Collider>();
            Vector3 point = c != null ? c.bounds.center : target.transform.position;
            Vector3 to = point - bot.head.position;
            // once the bot has reacted its head looks exactly at the point it shoots at
            dir = Vector3.Angle(bot.head.forward, to) < 25f || to.sqrMagnitude < 0.01f ? bot.head.forward : to.normalized;
            return true;
        }
        if (Time.time - lastShot < 0.8f)
        {
            dir = bot.head.forward;
            return true;
        }
        return false;
    }

    void Update()
    {
        if (isManager)
        {
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 1f;
                Scan();
            }
            return;
        }
        if (model == null) return;

        // the player is not deactivated when he dies: his model goes away here (the body is made by Corpses)
        bool dead = health != null && health.IsDead;
        if (model.gameObject.activeSelf == dead)
        {
            model.gameObject.SetActive(!dead);
            current = null;
        }
        if (dead || anim == null) return;

        Weapon w = CurrentWeapon();
        bool knife = w != null && w.IsMelee;

        // paused (time scale 0): the clip is still chosen, nothing moves
        float dt = Time.deltaTime;
        if (dt > 0f)
        {
            UpdateVelocity(dt);
            bool aiming = FindAim(out Vector3 dir);
            if (aiming && dir.sqrMagnitude > 1e-6f) aimDir = dir.normalized;
            aimWeight = Mathf.MoveTowards(aimWeight, aiming && !knife ? 1f : 0f, aimBlendSpeed * dt);
            kick *= Mathf.Exp(-12f * dt);
        }

        ChooseClip(knife);

        if (aimArm != null)
        {
            aimArm.enabled = aimWeight > 0.001f;
            aimArm.weight = aimWeight;
        }
    }

    void UpdateVelocity(float dt)
    {
        Vector3 v;
        if (agent != null && agent.enabled) v = agent.velocity;
        else if (cc != null && cc.enabled) v = cc.velocity;
        else v = hasLastPos ? (transform.position - lastPos) / dt : Vector3.zero;
        lastPos = transform.position;
        hasLastPos = true;

        v.y = 0f;
        if (v.sqrMagnitude > 400f) v = Vector3.zero;   // a teleport (respawn)
        velocity = Vector3.Lerp(velocity, v, 1f - Mathf.Exp(-10f * dt));
    }

    void ChooseClip(bool knife)
    {
        float speed = velocity.magnitude;
        // a gap between the thresholds, so the clip does not flicker at one speed
        moving = moving ? speed > 0.25f : speed > 0.45f;

        string next;
        float clipSpeed = 1f;
        bool sneaking = (bot != null && bot.IsSneaking) || (player != null && (player.IsSneaking || player.IsCrouching));

        if (!moving)
        {
            next = knife ? idleKnife : idle;
        }
        else if (sneaking)
        {
            next = walk;
            clipSpeed = speed / walkAnimSpeed;
        }
        else
        {
            Vector3 local = transform.InverseTransformDirection(velocity);
            float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            if (Mathf.Abs(angle) <= 55f) next = run;
            else if (angle > 0f && angle < 125f) next = runRight;
            else if (angle < 0f && angle > -125f) next = runLeft;
            else next = runBack;
            clipSpeed = speed / runAnimSpeed;
        }
        if (next == null) return;

        if (next != current || !anim.IsPlaying(next))
        {
            if (current == null) anim.Play(next);
            else anim.CrossFade(next, crossFade);
            current = next;
        }
        anim[current].speed = Mathf.Clamp(clipSpeed, 0.5f, 2f);
    }

    void LateUpdate()
    {
        if (isManager || model == null || !model.gameObject.activeInHierarchy) return;

        PlaceOnFloor();
        UpdateView();
        if (anim == null) return;

        AimChest();
        AimMount();
    }

    // feet exactly on the floor under the character (the NavMesh is not exactly at the floor), not in a jump
    void PlaceOnFloor()
    {
        float target = modelY;
        bool grounded = cc != null ? cc.isGrounded : agent == null || agent.baseOffset <= startBaseOffset + 0.01f;
        if (grounded && bottom < 0f)
        {
            // from the centre down to a bit below the feet; the own collider is not hit from inside
            int n = Physics.RaycastNonAlloc(transform.position, Vector3.down, hits, -bottom + 0.35f,
                                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (hits[i].distance >= best || hits[i].collider.GetComponentInParent<Health>() != null) continue;
                best = hits[i].distance;
            }
            // the floor a little above or below the bottom of the collider: the feet go onto it
            if (best < float.MaxValue && Mathf.Abs(-best - bottom) < 0.3f) target = modelY - best - bottom;
        }

        Vector3 p = model.localPosition;
        p.y = Mathf.MoveTowards(p.y, target, 2f * Time.deltaTime);
        if (Mathf.Abs(p.y - target) > 0.5f) p.y = target;
        model.localPosition = p;
    }

    // the character the camera is on (the living player, a watched bot) only casts a shadow
    void UpdateView()
    {
        Camera cam = Camera.main;
        bool own = cam != null && cam.transform.IsChildOf(transform);
        if (viewSet && own == ownView && Time.time < nextRendererCheck) return;
        ApplyView(own);
    }

    void ApplyView(bool own)
    {
        ownView = own;
        viewSet = true;
        nextRendererCheck = Time.time + 0.5f;
        model.GetComponentsInChildren(true, rendererBuffer);
        ShadowCastingMode mode = own ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
        foreach (Renderer r in rendererBuffer)
            if (r.shadowCastingMode != mode) r.shadowCastingMode = mode;
        rendererBuffer.Clear();
    }

    // call after adding renderers to the model (a gun), so they get the right shadow mode at once
    public void RefreshRenderers()
    {
        if (model == null) return;
        Camera cam = Camera.main;
        ApplyView(cam != null && cam.transform.IsChildOf(transform));
    }

    // the chest (half in Torso, half in Chest) turns so the arm points along the aim; the shot kicks it up
    void AimChest()
    {
        Restore(torso, ref torsoBase, torsoSet, torsoTurned);
        Restore(chest, ref chestBase, chestSet, chestTurned);

        float yaw = 0f, pitch = 0f;
        if (aimWeight > 0.001f)
        {
            Vector3 flatAim = aimDir;
            flatAim.y = 0f;
            Vector3 flatFwd = model.forward;
            flatFwd.y = 0f;
            if (flatAim.sqrMagnitude > 1e-4f && flatFwd.sqrMagnitude > 1e-4f)
                yaw = Mathf.Clamp(Vector3.SignedAngle(flatFwd, flatAim, Vector3.up), -maxAimYaw, maxAimYaw);
            pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg, -maxAimPitch, maxAimPitch);
            yaw *= aimWeight;
            pitch *= aimWeight;
        }
        pitch -= kick * 0.3f;
        if (Mathf.Abs(yaw) < 0.01f && Mathf.Abs(pitch) < 0.01f)
        {
            torsoTurned = false;
            chestTurned = false;
            return;
        }

        Quaternion half = Quaternion.AngleAxis(yaw * 0.5f, Vector3.up) * Quaternion.AngleAxis(pitch * 0.5f, model.right);
        Turn(torso, half, ref torsoSet, ref torsoTurned);
        Turn(chest, half, ref chestSet, ref chestTurned);
    }

    static void Restore(Transform bone, ref Quaternion baseRot, Quaternion setRot, bool turned)
    {
        if (bone == null) return;
        // the animation did not write this bone this frame: undo last frame's turn, or it would add up
        if (turned && bone.localRotation == setRot) bone.localRotation = baseRot;
        baseRot = bone.localRotation;
    }

    static void Turn(Transform bone, Quaternion worldTurn, ref Quaternion setRot, ref bool turned)
    {
        if (bone == null) return;
        bone.rotation = worldTurn * bone.rotation;
        setRot = bone.localRotation;
        turned = true;
    }

    // the gun follows the fist; while aiming its barrel is turned exactly along the aim, a shot kicks it up
    void AimMount()
    {
        if (mount == null) return;
        mount.localPosition = mountPos;
        mount.localRotation = mountRot;

        if (aimWeight > 0.001f)
        {
            Quaternion want = Quaternion.LookRotation(aimDir, Vector3.up);
            mount.rotation = Quaternion.Slerp(mount.rotation, want, aimWeight);
        }
        if (kick > 0.01f) mount.rotation = Quaternion.AngleAxis(-kick, mount.right) * mount.rotation;
    }
}
