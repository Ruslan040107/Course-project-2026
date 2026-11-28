using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Segmented T and CT models share the animated Quaternius skeleton.
// - Every model gets "Boost player contrast" (CharacterContrast, GameSettings.BoostPlayerContrast).
// - The capsule and the other own renderers of the bot or player are hidden (forceRenderingOff).
// - The model is a child with its feet on the floor. It faces where the character aims (the player: his view),
//   and a bot without a target faces where it runs, so it never runs sideways by mistake.
// - Legacy animation by state: Idle_Gun standing, Run (Run_Back / Run_Left / Run_Right only when it moves sideways
//   to where it aims), Walk while sneaking or crouching (played backwards when walking back), playback speed follows
//   the real speed. A hit plays HitRecieve on top, a knife swing Sword_Slash.
// - Facing (after the clip, in LateUpdate): the clips stand turned (Idle_Gun: hips 27 degrees to the left; Run_Left /
//   Run_Right: hips 90 degrees and chest 34 degrees to the side; measured in Blender), so the pose is corrected every
//   frame: the hips (Body, with the feet that are the clip's IK targets under Root) turn to face the aim; running
//   diagonally or strafing they turn to the run direction, but at most maxLegYaw (60 degrees) from the aim, so the
//   body still faces where the character looks (the legs step a little sideways); the spine turns the chest
//   exactly to the aim and the neck the head exactly along the aim (yaw and pitch).
// - In the air (the player off the ground, a bot hopping) the idle clip with a procedural jump pose plays instead of
//   Run: the feet come up and the knees bend (leg IK); a short dip on landing.
// - Guns are held with both hands (after the animation, in LateUpdate): the right hand on the grip, the left hand on
//   the fore-end (rifles, SMGs, shotguns, machine guns, snipers; the chest stays square to the aim, the left shoulder
//   comes forward, the stock rests at the right shoulder) or also on the grip (pistols, arms forward). Both arms use an
//   analytic two-bone IK (UpperArm/LowerArm/Wrist); the gun slides back along the aim when a hand cannot reach. While
//   reloading the left hand goes to the magazine. The knife stays in the right fist of the animation.
// - Halftime (Health.team changed): the model of the new team is built again, with hitboxes, gun mount and layers;
//   the character keeps its agent number (the first agent of one side becomes the first agent of the other).
// - WeaponMount: an empty object in the right fist where BotWeaponModels hangs the gun (grip in the fist, barrel
//   forward). The IK puts the right wrist so that the mount is exactly where the gun should be.
// - Crouch (bots: BotController.CrouchAmount, the player: his crouch key): a procedural pose on top of the clip,
//   hips 0.45 m down, spine forward, knees bent by a leg IK to the feet of the clip (Walk when moving).
// - Hitboxes: colliders with HitZone on the bones (head sphere, chest/upper body/stomach/pelvis boxes, arm, hand and
//   leg capsules, feet boxes), one table for every model, sized to the models with their gear (like CS2: a helmet is
//   head, a plate carrier chest and stomach), so they follow the animation and the crouch.
//   The capsule / CharacterController goes to the Ignore Raycast layer: bullets and vision hit only the hitboxes.
//   A bot's eyes (BotController.head: vision and shots) follow the head of its model.
// - The model of the character the main camera is in (the living player, a watched bot) is on layer 9 "LocalBody"
//   that the main camera does not draw; other cameras see the whole model. Shadow-only copies keep its shadow.
// It adds itself to every bot and to the player of a scene with a RoundManager, never in simulations
// (there everything stays capsule based).
public class CharacterBody : MonoBehaviour
{
    [Header("Aim")]
    [Tooltip("the chest turns up/down at most this much, degrees")] public float maxAimPitch = 40f;
    [Tooltip("the chest turns sideways at most this much, degrees")] public float maxAimYaw = 45f;
    [Tooltip("how fast the arm goes up to aim and down again, 1/s")] public float aimBlendSpeed = 6f;
    [Tooltip("the gun kicks up by this angle on a shot, degrees")] public float recoilKick = 6f;
    [Tooltip("degrees per second the model turns to where it aims or runs")] public float turnSpeed = 540f;
    [Tooltip("the head looks up/down at most this much, degrees")] public float maxHeadPitch = 70f;

    [Header("Facing")]
    [Tooltip("degrees per second the legs turn to a new run direction")] public float legTurnSpeed = 720f;
    [Tooltip("the hips turn at most this much from the aim to the run direction while strafing, degrees " +
             "(more: the legs slide less, less: the body faces the aim more)")]
    public float maxLegYaw = 60f;
    [Tooltip("a run clip changes this many degrees past its border, so it does not flicker")] public float clipHysteresis = 8f;

    [Header("Two-handed hold")]
    [Tooltip("with a long gun the left shoulder comes forward this much (degrees), so the left hand reaches the fore-end; the chest stays square to the aim")]
    public float supportShoulder = 30f;
    [Tooltip("and the right shoulder goes back this much, degrees")] public float rightShoulderBack = 10f;
    [Tooltip("a gun without a target points this much down, degrees")] public float lowReadyPitch = 10f;
    [Tooltip("where the stock rests, from the right shoulder joint: right, up, forward (m, in the chest's frame)")]
    public Vector3 stockPocket = new Vector3(-0.075f, -0.05f, 0.05f);
    [Tooltip("pistols: the grip this far in front of the middle of the shoulders")] public float pistolReach = 0.38f;
    [Tooltip("pistols: and this much lower")] public float pistolDrop = 0.08f;
    [Tooltip("the gun slides back along the aim at most this much when a hand cannot reach")] public float maxPullBack = 0.25f;
    [Tooltip("the left fingers bend this much at each joint around the fore-end, degrees")] public float fingerCurl = 38f;
    [Tooltip("1/s, how fast the hands go to the gun and away")] public float holdBlendSpeed = 5f;

    [Header("Crouch pose")]
    [Tooltip("the hips go down this much at full crouch, m (the player's camera goes down 0.55)")] public float crouchDrop = 0.5f;
    [Tooltip("and back this much, so the thighs go forward")] public float crouchBack = 0.08f;
    [Tooltip("the spine leans forward this much, degrees")] public float crouchLean = 16f;

    [Header("Jump pose")]
    [Tooltip("in the air the feet come up this much and the knees bend, m")] public float airTuck = 0.28f;
    [Tooltip("the player counts as in the air after this long off the ground, s (steps and slopes do not count)")]
    public float airDelay = 0.1f;
    [Tooltip("1/s, how fast the jump pose comes and goes")] public float airBlendSpeed = 8f;
    [Tooltip("a short crouch when landing, 0..1 of the crouch pose")] [Range(0f, 1f)] public float landDip = 0.3f;
    [Tooltip("how long the landing dip lasts, s")] public float landTime = 0.25f;

    [Header("Animation")]
    [Tooltip("m/s at which the Run clips look right (measured on the model)")] public float runAnimSpeed = 3f;
    [Tooltip("m/s at which the Walk clip looks right")] public float walkAnimSpeed = 1.35f;
    public float crossFade = 0.2f;
    [Range(0f, 1f)] public float hitWeight = 0.7f;

    // the agents of each side (Resources paths), the index is the agent number of a character
    public static readonly string[] TAgents = { "Characters/T_Character" };
    public static readonly string[] CTAgents = { "Characters/CT_Character" };
    // the first models: what a missing agent falls back to
    public const string TFallback = "Characters/T_Adventurer";
    public const string CTFallback = "Characters/CT_Swat";
    const float ImportHeight = 1.81f;
    public const float BodyScale = 1.2f;
    public const float ModelHeight = ImportHeight * BodyScale;

    // layer 9 "LocalBody" (used by number, it needs no name in the tag manager): the model of the character the
    // main camera is in; the main camera does not draw it, every other camera does
    public const int LocalBodyLayer = 9;
    public const int IgnoreRaycastLayer = 2;
    const int DefaultLayer = 0;

    // the model, its animation and the gun mount (null until Start, or when there is no model)
    public Transform Model => model;
    public Animation Anim => anim;
    public Transform WeaponMount => mount;
    public GameObject Prefab => prefab;
    public bool HasModel => model != null && model.gameObject.activeInHierarchy;
    // true while bullets and vision use the bone hitboxes of the model instead of the capsule
    public bool HasHitboxes => hitboxes.Count > 0 && HasModel;
    // where the model faces (flat)
    public Vector3 Forward
    {
        get
        {
            Vector3 f = model != null ? Frame * Vector3.forward : transform.forward;
            f.y = 0f;
            return f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
        }
    }

    Health health;
    BotController bot;
    PlayerController player;
    PlayerShoot shoot;
    NavMeshAgent agent;
    CharacterController cc;
    bool ccWasOn;
    float startBaseOffset;

    GameObject prefab;
    Transform model;
    Animation anim;
    Transform mount;
    float modelY;        // local y of the model root with the feet at the bottom of the collider
    float bottom;        // local y of the bottom of the collider (the bot transform is the capsule centre)
    readonly List<Renderer> hidden = new List<Renderer>();

    // the character's own frame (feet on the floor, facing +Z) relative to the model root; the model turns with yaw
    Quaternion frameRel = Quaternion.identity;
    Quaternion baseLocalRot = Quaternion.identity;
    float modelYaw;
    bool yawSet;
    Quaternion Frame => model.rotation * frameRel;

    // bones
    Transform body, hipsBone, abdomen, torso, chest, neck, headBone;
    Transform shoulderL, upperArmL, lowerArmL, wristL, shoulderR, upperArmR, lowerArmR, wristR;
    Transform upperLegL, lowerLegL, footL, upperLegR, lowerLegR, footR;
    Vector3 shinTipL, shinTipR;                  // the ankle in the lower leg's space
    Quaternion handRestL = Quaternion.identity;  // wrist rotation = LookRotation(fingers, palm normal) * handRest
    bool hasHandL;
    readonly List<Transform[]> fingersL = new List<Transform[]>();
    // rotations relative to the frame in the rest pose: bone.rotation * Inverse(rest) is where the bone faces
    Quaternion chestRest = Quaternion.identity, hipsRest = Quaternion.identity, headRest = Quaternion.identity;
    float scaleK = 1f;                           // the model's size compared with the Blender measurements
    int builtTeam = -1;                          // the team the model was made for
    int agentNumber = -1;                        // the agent number (TAgents / CTAgents), kept when the team changes
    int builtAgent = -1;                         // the agent the model was made for

    // animation states
    string idle, idleKnife, run, runBack, runLeft, runRight, walk, current;
    AnimationState aimArm, slash, hit;
    bool moving;
    bool walkBack;       // the walk clip plays backwards

    // aim and movement
    Vector3 velocity;
    Vector3 lastPos;
    bool hasLastPos;
    Vector3 aimDir = Vector3.forward;
    bool aiming;
    float aimWeight;     // the arm points the gun (not with the knife)
    float lookWeight;    // the chest and head look along the aim (also with the knife)
    float kick;
    float lastShot = -10f;
    float crouchWeight;
    float poseCrouch;    // the crouch of the pose: crouching, or the dip after a landing

    // the legs: where the hips face from the frame (degrees, + = right), turned smoothly to the wanted direction
    float legYaw, legTarget;
    bool legYawSet;

    // in the air
    float airTime;       // the player: seconds off the ground
    float airWeight;     // 0..1 of the jump pose
    bool airClip;        // the idle clip plays because the character is in the air
    bool wasInAir;
    float landTimer;

    // the gun held with both hands
    float holdWeight;
    float longWeight;      // 1 = a long gun (stock at the shoulder), 0 = a pistol
    float drawBlend;
    float reloadBlend;
    Transform gunHolder;   // the gun model under the mount, measured when it changes
    Vector3 gunButt, gunFore, gunMag;
    Vector3 mountPos;
    Quaternion mountRot;

    // bones changed after the animation: if a clip does not write one, the change of the last frame is undone first
    class BoneState
    {
        public Transform t;
        public Vector3 basePos, setPos;
        public Quaternion baseRot, setRot;
        public bool set;
    }
    readonly List<BoneState> changed = new List<BoneState>();

    // hitboxes
    readonly List<Collider> hitboxes = new List<Collider>();
    Collider hbHead, hbChest, hbStomach, hbArmL, hbArmR, hbThighL, hbThighR;
    int rootLayer = DefaultLayer;
    bool movedRootLayer;

    // the camera is in this character: its model is on LocalBodyLayer, shadow-only copies cast its shadow
    bool ownView;
    bool viewSet;
    float nextRendererCheck;
    readonly Dictionary<Renderer, Renderer> proxies = new Dictionary<Renderer, Renderer>();
    readonly HashSet<Renderer> proxySet = new HashSet<Renderer>();

    static bool warned;
    static readonly List<Renderer> rendererBuffer = new List<Renderer>();
    static readonly List<Renderer> staleBuffer = new List<Renderer>();
    static readonly RaycastHit[] hits = new RaycastHit[16];

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
        loggedFix.Clear();
        opaqueCopies.Clear();
        missing.Clear();
        unnumbered = 0;
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
    public static bool VisualsAllowed()
    {
        if (Application.isBatchMode || SimulationFlag()) return false;
        if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return false;
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        return rm == null || !rm.simulation;
    }

    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded || !VisualsAllowed()) return;
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
        if (!VisualsAllowed()) enabled = false;
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

    // ---------------- models (also used by Corpses and TrainingTarget) ----------------

    // the model of an agent of a side (the number wraps around)
    public static GameObject PrefabFor(int team, int agentNumber)
    {
        string[] agents = team == 1 ? CTAgents : TAgents;
        return LoadModel(agents[(agentNumber % agents.Length + agents.Length) % agents.Length]);
    }

    static readonly HashSet<string> missing = new HashSet<string>();

    // a model by its Resources path; an agent that is not in the project falls back to the first model of its side
    // (written to the log once)
    public static GameObject LoadModel(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        GameObject prefab = Resources.Load<GameObject>(path);
        if (prefab != null) return prefab;

        string fallback = System.Array.IndexOf(CTAgents, path) >= 0 ? CTFallback
            : System.Array.IndexOf(TAgents, path) >= 0 ? TFallback : null;
        if (fallback == null) return null;
        if (missing.Add(path)) Debug.Log("CharacterBody: no " + path + " in Resources, using " + fallback);
        return Resources.Load<GameObject>(fallback);
    }

    // the side a model belongs to by its name: CT_... = 1, everything else 0
    public static int TeamOf(GameObject prefab)
    {
        return prefab != null && prefab.name.StartsWith("CT", System.StringComparison.Ordinal) ? 1 : 0;
    }

    // where an instance sits under a parent of scale 1: feet at y 0, facing +Z, scaled to gameplay height
    struct Fit
    {
        public Quaternion rotation;
        public Vector3 scale;
        public float feet;
    }
    static readonly Dictionary<GameObject, Fit> fits = new Dictionary<GameObject, Fit>();
    static readonly HashSet<string> loggedFix = new HashSet<string>();

    // a copy of a character model under the parent: feet at the parent's origin, facing its forward,
    // opaque materials, without colliders
    public static GameObject SpawnModel(GameObject prefab, Transform parent)
    {
        GameObject go = Instantiate(prefab, parent, false);
        go.name = prefab.name;
        Transform t = go.transform;

        foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) Destroy(c);
        FixMaterials(go);
        // "Boost player contrast": copies of the materials with the contrast shader (the old models: with their palette)
        CharacterContrast.Apply(go, TeamOf(prefab), CharacterContrast.UsesPalette(prefab));

        if (!fits.TryGetValue(prefab, out Fit fit))
        {
            fit = Measure(t, parent, prefab.name);
            fits[prefab] = fit;
        }
        float parentScale = parent != null ? Mathf.Max(parent.lossyScale.y, 1e-4f) : 1f;
        t.localRotation = fit.rotation;
        t.localScale = fit.scale / parentScale;
        t.localPosition = new Vector3(0f, fit.feet / parentScale, 0f);
        return go;
    }

    // measures the first instance: up and forward from the bones (a wrong axis is turned by 90 degree steps),
    // height and feet from the meshes in the import pose. The turn it finds is written to the log once.
    static Fit Measure(Transform t, Transform parent, string name)
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
        Vector3 upFound = Vector3.zero, toesFound = Vector3.zero;
        if (head != null && footL != null && footR != null)
        {
            Vector3 up = ToParent(parent, head.position - (footL.position + footR.position) * 0.5f);
            upFound = up;
            if (up.sqrMagnitude > 1e-6f && Vector3.Angle(up, Vector3.up) > 45f)
                fix = Snap(Quaternion.FromToRotation(up, Vector3.up));

            // forward from the hip joints (left -> right, crossed with up). The toes were used before, but the end node
            // "Foot.L_end" of these rigs points sideways (-0.86, 0, 0.52), so every model was wrongly turned by 90 degrees
            // ("the camera looks along z, the model along x"). The toes stay as a fallback for rigs without leg bones.
            Transform legL = FindBone(t, "UpperLeg.L");
            Transform legR = FindBone(t, "UpperLeg.R");
            Vector3 facingFound = Vector3.zero;
            if (legL != null && legR != null)
            {
                Vector3 across = fix * ToParent(parent, legR.position - legL.position);
                across.y = 0f;
                if (across.sqrMagnitude > 1e-6f) facingFound = Vector3.Cross(across, Vector3.up);
            }
            if (toe != null)
            {
                Vector3 toes = fix * ToParent(parent, toe.position - footL.position);
                toes.y = 0f;
                toesFound = toes;
                if (facingFound.sqrMagnitude < 1e-6f) facingFound = toes;
            }
            if (facingFound.sqrMagnitude > 1e-6f)
            {
                // the turn that takes the facing to +Z
                float yaw = Vector3.SignedAngle(facingFound, Vector3.forward, Vector3.up);
                if (Mathf.Abs(yaw) > 45f) fix = Quaternion.AngleAxis(Mathf.Round(yaw / 90f) * 90f, Vector3.up) * fix;
                toesFound = facingFound;
            }
        }
        t.localRotation = fix * baseRot;
        if (loggedFix.Add(name))
            Debug.Log("CharacterBody: " + name + " up " + upFound.normalized + ", facing " + toesFound.normalized +
                      ", turned by " + fix.eulerAngles + " (root rotation " + baseRot.eulerAngles + ")");

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
        if (any && height > 0.01f && (height < ImportHeight * 0.85f || height > ImportHeight * 1.15f)) k = ImportHeight / height;
        k *= BodyScale;

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
    // (Middle2.R), a little down and to the thumb side; the barrel looks along the character's forward
    // (the parent's forward: SpawnModel turns the model to face it). Its world scale is 1, so a gun hung under it
    // has its real size.
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

        Quaternion facing = modelRoot.parent != null ? modelRoot.parent.rotation : modelRoot.rotation;
        Vector3 up = facing * Vector3.up;
        Vector3 right = Vector3.Cross(up, d / length).normalized;
        Vector3 grip = w + d * 1.07f - up * (0.1f * length) - right * (0.165f * length);

        Transform m = new GameObject("WeaponMount").transform;
        m.SetParent(wrist, false);
        m.SetPositionAndRotation(grip, facing);
        Vector3 s = wrist.lossyScale;
        m.localScale = new Vector3(1f / Mathf.Max(Mathf.Abs(s.x), 1e-4f), 1f / Mathf.Max(Mathf.Abs(s.y), 1e-4f), 1f / Mathf.Max(Mathf.Abs(s.z), 1e-4f));
        return m;
    }

    // ---------------- facing (the living models and TrainingTarget) ----------------

    // a bone's rest rotation relative to the frame (feet down, facing forward) the model has now
    static Quaternion RestRel(RestPose rest, Transform b, Quaternion frame)
    {
        if (b == null || !rest.Has(b)) return Quaternion.identity;
        return Quaternion.Inverse(frame) * Quaternion.Inverse(rest.LocalRot(b, Quaternion.identity));
    }

    // where a bone faces now: degrees about the frame's up from the frame's forward (+ = to the right)
    static float YawOf(Transform b, Quaternion restRel, Quaternion frame)
    {
        Vector3 up = frame * Vector3.up;
        Vector3 f = Vector3.ProjectOnPlane(b.rotation * (Quaternion.Inverse(restRel) * Vector3.forward), up);
        if (f.sqrMagnitude < 1e-8f) return 0f;
        return Vector3.SignedAngle(frame * Vector3.forward, f, up);
    }

    // how far down a bone looks, degrees (+ = down)
    static float PitchOf(Transform b, Quaternion restRel, Vector3 up)
    {
        Vector3 f = (b.rotation * (Quaternion.Inverse(restRel) * Vector3.forward)).normalized;
        return -Mathf.Asin(Mathf.Clamp(Vector3.Dot(f, up), -1f, 1f)) * Mathf.Rad2Deg;
    }

    // The lower body turns about the vertical through Body: Body (hips, legs and spine) and the feet, which are not
    // its children (they are the clip's IK targets under Root), so the legs stay on them.
    static void TurnLowerBody(Transform body, Transform footL, Transform footR, float degrees, Vector3 up)
    {
        if (body == null || Mathf.Abs(degrees) < 0.01f) return;
        Quaternion q = Quaternion.AngleAxis(degrees, up);
        Vector3 pivot = body.position;
        body.rotation = q * body.rotation;
        TurnFoot(footL, body, pivot, q);
        TurnFoot(footR, body, pivot, q);
    }

    static void TurnFoot(Transform foot, Transform body, Vector3 pivot, Quaternion q)
    {
        if (foot == null || foot.IsChildOf(body)) return;
        foot.SetPositionAndRotation(pivot + q * (foot.position - pivot), q * foot.rotation);
    }

    // the spine turns the chest to the yaw (from the frame): Abdomen and Torso a share each, the Chest the rest
    static void TurnSpine(Transform abdomen, Transform torso, Transform chest, Quaternion chestRest, Quaternion frame, float yaw)
    {
        Vector3 up = frame * Vector3.up;
        float e = Mathf.DeltaAngle(YawOf(chest, chestRest, frame), yaw);
        if (abdomen != null) abdomen.rotation = Quaternion.AngleAxis(e * 0.35f, up) * abdomen.rotation;
        if (torso != null) torso.rotation = Quaternion.AngleAxis(e * 0.35f, up) * torso.rotation;
        e = Mathf.DeltaAngle(YawOf(chest, chestRest, frame), yaw);
        chest.rotation = Quaternion.AngleAxis(e, up) * chest.rotation;
    }

    // the neck half of the way, the head the rest: the head's yaw to yaw, and its pitch (+ = down) to pitch
    // with pitchWeight (0 keeps the clip's nod)
    static void TurnHead(Transform neck, Transform head, Quaternion headRest, Quaternion frame, float yaw, float pitch, float pitchWeight)
    {
        Vector3 up = frame * Vector3.up;
        Vector3 right = Quaternion.AngleAxis(yaw, up) * (frame * Vector3.right);
        for (int i = 0; i < 2; i++)
        {
            Transform t = i == 0 ? neck : head;
            if (t == null) continue;
            float share = i == 0 ? 0.5f : 1f;
            float ey = Mathf.DeltaAngle(YawOf(head, headRest, frame), yaw);
            t.rotation = Quaternion.AngleAxis(ey * share, up) * t.rotation;
            if (pitchWeight > 0.001f)
            {
                float ep = Mathf.DeltaAngle(PitchOf(head, headRest, up), pitch) * pitchWeight;
                t.rotation = Quaternion.AngleAxis(ep * share, right) * t.rotation;
            }
        }
    }

    // A still pose (TrainingTarget): the clips stand turned (Idle: hips 27 degrees to the left), this turns the hips,
    // the chest and the head of the model to the frame's forward. Call it after the pose is set.
    public static void StraightenPose(Transform modelRoot, Quaternion frame)
    {
        if (modelRoot == null) return;
        RestPose rest = new RestPose(modelRoot);
        Transform bodyBone = FindBone(modelRoot, "Body"), hips = FindBone(modelRoot, "Hips");
        Transform chestBone = FindBone(modelRoot, "Chest"), head = FindBone(modelRoot, "Head");
        Vector3 up = frame * Vector3.up;
        if (bodyBone != null && rest.Has(hips))
            TurnLowerBody(bodyBone, FindBone(modelRoot, "Foot.L"), FindBone(modelRoot, "Foot.R"),
                          -YawOf(hips, RestRel(rest, hips, frame), frame), up);
        if (rest.Has(chestBone))
            TurnSpine(FindBone(modelRoot, "Abdomen"), FindBone(modelRoot, "Torso"), chestBone, RestRel(rest, chestBone, frame), frame, 0f);
        if (rest.Has(head))
            TurnHead(FindBone(modelRoot, "Neck"), head, RestRel(rest, head, frame), frame, 0f, 0f, 0f);
    }

    static readonly Dictionary<Material, Material> opaqueCopies = new Dictionary<Material, Material>();

    // the FBX materials have alpha 0; CharacterImport makes them opaque, this is the safety net at runtime
    public static void FixMaterials(GameObject go)
    {
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            Material[] mats = r.sharedMaterials;
            bool changedAny = false;
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
                changedAny = true;
            }
            if (changedAny) r.sharedMaterials = mats;
        }
    }

    // ---------------- rest pose (the bind pose of the skinned meshes) ----------------

    // Where every bone is in the rest pose (T-pose) of the meshes, placed where the model is now. It comes from the
    // bind poses, so it does not matter which pose the model shows at the moment.
    class RestPose
    {
        readonly Dictionary<Transform, Matrix4x4> toBone = new Dictionary<Transform, Matrix4x4>();   // world (rest) -> bone

        public RestPose(Transform modelRoot)
        {
            foreach (SkinnedMeshRenderer r in modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (r.sharedMesh == null) continue;
                Transform[] bones = r.bones;
                Matrix4x4[] bind = r.sharedMesh.bindposes;
                Matrix4x4 worldToMesh = r.transform.worldToLocalMatrix;
                for (int i = 0; i < bones.Length && i < bind.Length; i++)
                    if (bones[i] != null && !toBone.ContainsKey(bones[i])) toBone[bones[i]] = bind[i] * worldToMesh;
            }
        }

        public bool Has(Transform b)
        {
            return b != null && toBone.ContainsKey(b);
        }

        // the bone's position in the rest pose (its current position if it has no bind pose)
        public Vector3 Pos(Transform b)
        {
            if (!toBone.TryGetValue(b, out Matrix4x4 m)) return b.position;
            return m.inverse.MultiplyPoint3x4(Vector3.zero);
        }

        // a point / direction of the rest pose in the bone's own space
        public Vector3 LocalPoint(Transform b, Vector3 world)
        {
            return toBone.TryGetValue(b, out Matrix4x4 m) ? m.MultiplyPoint3x4(world) : b.InverseTransformPoint(world);
        }

        public Vector3 LocalDir(Transform b, Vector3 world)
        {
            return toBone.TryGetValue(b, out Matrix4x4 m) ? m.MultiplyVector(world) : b.InverseTransformDirection(world);
        }

        // a rotation of the rest pose in the bone's own space
        public Quaternion LocalRot(Transform b, Quaternion world)
        {
            Vector3 f = LocalDir(b, world * Vector3.forward);
            Vector3 u = LocalDir(b, world * Vector3.up);
            return f.sqrMagnitude > 1e-10f && u.sqrMagnitude > 1e-10f ? Quaternion.LookRotation(f, u) : Quaternion.identity;
        }
    }

    // ---------------- hitboxes ----------------

    enum Shape { Sphere, Box, Capsule }

    // One table for every model (all of them share the rig), so both teams have the same hitboxes. Like in CS2 the gear
    // counts: a helmet is head, a plate carrier or chest rig is chest and stomach. The sizes cover the gear envelope
    // agreed for the agents, in the Blender rest pose (m, Z up, the character faces -Y, the spine line is at y -0.04):
    // torso gear from y -0.245 (front) to +0.115 (back) and |x| <= 0.175 for z 1.05-1.42 (straps on the shoulders up to
    // |x| 0.20), belt gear |x| <= 0.19, y -0.20..+0.12, z 0.95-1.08, head gear inside the ellipsoid around
    // (0, -0.05, 1.715) with half axes (0.14, 0.15, 0.145), limb gear at most 2.5 cm on the cloth. The limbs are the
    // largest cloth radius of the pack's outfits plus about 1.5 cm, the capsules overlap the knees and elbows.
    // The regions were turned into the bone frames of the rest pose and checked on the meshes in Blender, vertices more
    // than 1 cm outside every hitbox (the old, smaller table in brackets): CT_Swat head 3.8 % (37 %, its respirator),
    // torso 0 % (5 %), arms 0 % (18 %), legs 3.4 % (19 %); the four agents: torso 0-0.4 %, arms 0 %, legs 1.9-3.2 %,
    // head 1.6-3.8 % (CT_FBI 7 %: the NVG mount and rails of its helmet, up to 5 cm out). The backpack of T_Guerrilla
    // and T_Adventurer (torso 20 % / 40 %) is outside the envelope and is no hitbox, like in CS2. In the game (AutoShot
    // test "agents": a ray through every second drawn pixel of a standing or running agent, front and side) about 1-2 %
    // of a model's pixels miss every hitbox, mostly the toe tips and the fingers of the left hand (T_Guerrilla seen from
    // the side 7.7 %: the backpack).
    // Spine and head: offsets from the bone's head in its frame (right, up = towards the next bone, forward = the
    // character's forward). Limbs: capsules along the bone, centre and height along it (caps included), radius.
    // Feet: in the character's frame.
    struct Spec
    {
        public string name, bone, toward;
        public Shape shape;
        public BodyZone zone;
        public Vector3 center;   // capsules: y = centre along the bone
        public Vector3 size;     // box: size; sphere: x = radius; capsule: x = radius, y = height
    }

    static Spec S(string name, string bone, string toward, Shape shape, BodyZone zone, Vector3 center, Vector3 size)
    {
        return new Spec { name = name, bone = bone, toward = toward, shape = shape, zone = zone, center = center, size = size };
    }

    static readonly Spec[] Specs =
    {
        S("Head", "Head", null, Shape.Sphere, BodyZone.Head, new Vector3(0f, 0.123f, 0.012f), new Vector3(0.15f, 0f, 0f)),
        S("Chest", "Chest", "Neck", Shape.Box, BodyZone.Body, new Vector3(0f, 0.094f, 0.003f), new Vector3(0.35f, 0.198f, 0.324f)),
        S("UpperBody", "Torso", "Chest", Shape.Box, BodyZone.Body, new Vector3(0f, 0.102f, 0.026f), new Vector3(0.35f, 0.329f, 0.364f)),
        S("Stomach", "Abdomen", "Torso", Shape.Box, BodyZone.Stomach, new Vector3(0f, 0.07f, 0.02f), new Vector3(0.38f, 0.206f, 0.373f)),
        S("Pelvis", "Hips", "Abdomen", Shape.Box, BodyZone.Stomach, new Vector3(0f, 0.073f, -0.01f), new Vector3(0.38f, 0.232f, 0.343f)),
        S("UpperArm.L", "UpperArm.L", "LowerArm.L", Shape.Capsule, BodyZone.Body, new Vector3(0f, 0.065f, 0f), new Vector3(0.09f, 0.27f, 0f)),
        S("UpperArm.R", "UpperArm.R", "LowerArm.R", Shape.Capsule, BodyZone.Body, new Vector3(0f, 0.065f, 0f), new Vector3(0.09f, 0.27f, 0f)),
        S("LowerArm.L", "LowerArm.L", "Wrist.L", Shape.Capsule, BodyZone.Body, new Vector3(0f, 0.12f, 0f), new Vector3(0.072f, 0.3f, 0f)),
        S("LowerArm.R", "LowerArm.R", "Wrist.R", Shape.Capsule, BodyZone.Body, new Vector3(0f, 0.12f, 0f), new Vector3(0.072f, 0.3f, 0f)),
        S("Hand.L", "Wrist.L", "Middle1.L", Shape.Sphere, BodyZone.Body, new Vector3(0f, 0.10f, 0f), new Vector3(0.075f, 0f, 0f)),
        S("Hand.R", "Wrist.R", "Middle1.R", Shape.Sphere, BodyZone.Body, new Vector3(0f, 0.10f, 0f), new Vector3(0.075f, 0f, 0f)),
        S("UpperLeg.L", "UpperLeg.L", "LowerLeg.L", Shape.Capsule, BodyZone.Legs, new Vector3(0f, 0.185f, 0f), new Vector3(0.125f, 0.57f, 0f)),
        S("UpperLeg.R", "UpperLeg.R", "LowerLeg.R", Shape.Capsule, BodyZone.Legs, new Vector3(0f, 0.185f, 0f), new Vector3(0.125f, 0.57f, 0f)),
        S("LowerLeg.L", "LowerLeg.L", "Foot.L", Shape.Capsule, BodyZone.Legs, new Vector3(0f, 0.245f, 0f), new Vector3(0.10f, 0.57f, 0f)),
        S("LowerLeg.R", "LowerLeg.R", "Foot.R", Shape.Capsule, BodyZone.Legs, new Vector3(0f, 0.245f, 0f), new Vector3(0.10f, 0.57f, 0f)),
        S("Foot.L", "Foot.L", null, Shape.Box, BodyZone.Legs, new Vector3(-0.0134f, 0.008f, 0.112f), new Vector3(0.12f, 0.095f, 0.26f)),
        S("Foot.R", "Foot.R", null, Shape.Box, BodyZone.Legs, new Vector3(0.0134f, 0.008f, 0.112f), new Vector3(0.12f, 0.095f, 0.26f)),
    };

    const float BlenderHipsToHead = 0.6934f;   // rest distance Hips -> Head of the rig in Blender, m
    // a bot's eyes are this far under the centre of its head hitbox (where they were before the head sphere grew), m
    const float EyesUnderHeadCentre = 0.056f;

    // Adds the hitboxes to the bones of a character model (children of the bones, so they follow the animation).
    // facing: the character's rotation (feet down, looking forward). Works in any current pose (uses the rest pose).
    // The hitboxes are on the Default layer with a HitZone; a kinematic Rigidbody on the model root moves them.
    public static List<Collider> AddHitboxes(Transform modelRoot, Quaternion facing)
    {
        List<Collider> list = new List<Collider>();
        if (modelRoot == null) return list;

        RestPose rest = new RestPose(modelRoot);
        Transform hips = FindBone(modelRoot, "Hips");
        Transform headB = FindBone(modelRoot, "Head");
        float k = 1f;
        if (rest.Has(hips) && rest.Has(headB))
        {
            float d = (rest.Pos(headB) - rest.Pos(hips)).magnitude;
            if (d > 0.01f) k = d / BlenderHipsToHead;
        }

        Vector3 charFwd = facing * Vector3.forward;
        Vector3 charUp = facing * Vector3.up;

        foreach (Spec s in Specs)
        {
            Transform b = FindBone(modelRoot, s.bone);
            if (b == null) continue;
            Transform toward = s.toward != null ? FindBone(modelRoot, s.toward) : null;
            Vector3 origin = rest.Pos(b);

            // the frame of this part in the rest pose
            Vector3 axis = toward != null ? rest.Pos(toward) - origin : charUp;
            if (axis.sqrMagnitude < 1e-8f) axis = charUp;
            axis.Normalize();

            Vector3 center;
            Quaternion rot;
            if (s.shape == Shape.Capsule || (s.shape == Shape.Sphere && toward != null && s.bone.StartsWith("Wrist")))
            {
                // along the bone
                center = origin + axis * (s.center.y * k);
                rot = Quaternion.FromToRotation(Vector3.up, axis);
            }
            else
            {
                Vector3 up = s.toward != null ? axis : charUp;
                Vector3 fwd = Vector3.ProjectOnPlane(charFwd, up);
                if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.ProjectOnPlane(facing * Vector3.back, up);
                fwd.Normalize();
                Vector3 right = Vector3.Cross(up, fwd);
                center = origin + (right * s.center.x + up * s.center.y + fwd * s.center.z) * k;
                rot = Quaternion.LookRotation(fwd, up);
            }

            GameObject go = new GameObject("Hitbox " + s.name);
            go.layer = DefaultLayer;
            Transform t = go.transform;
            t.SetParent(b, false);
            t.localPosition = rest.LocalPoint(b, center);
            t.localRotation = rest.LocalRot(b, rot);
            // world scale 1: the sizes are meters
            Vector3 ls = b.lossyScale;
            t.localScale = new Vector3(1f / Mathf.Max(Mathf.Abs(ls.x), 1e-5f), 1f / Mathf.Max(Mathf.Abs(ls.y), 1e-5f), 1f / Mathf.Max(Mathf.Abs(ls.z), 1e-5f));

            Collider c;
            if (s.shape == Shape.Sphere)
            {
                SphereCollider sc = go.AddComponent<SphereCollider>();
                sc.radius = s.size.x * k;
                c = sc;
            }
            else if (s.shape == Shape.Box)
            {
                BoxCollider bc = go.AddComponent<BoxCollider>();
                bc.size = s.size * k;
                c = bc;
            }
            else
            {
                CapsuleCollider cc = go.AddComponent<CapsuleCollider>();
                cc.direction = 1;
                cc.radius = s.size.x * k;
                cc.height = Mathf.Max(s.size.y, s.size.x * 2f) * k;
                c = cc;
            }
            go.AddComponent<HitZone>().zone = s.zone;
            list.Add(c);
        }

        if (list.Count > 0)
        {
            Rigidbody rb = modelRoot.GetComponent<Rigidbody>();
            if (rb == null) rb = modelRoot.gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.None;
        }
        return list;
    }

    static Collider FindHitbox(List<Collider> list, string name)
    {
        string full = "Hitbox " + name;
        foreach (Collider c in list)
            if (c != null && c.name == full) return c;
        return null;
    }

    // centres of the hitboxes to look and aim at, best first: stomach (the old capsule centre), chest, head,
    // upper arms, thighs. 0 when there are none (then the capsule is used).
    public int AimPoints(Vector3[] into)
    {
        if (!HasHitboxes || into == null) return 0;
        int n = 0;
        n = AddPoint(into, n, hbStomach);
        n = AddPoint(into, n, hbChest);
        n = AddPoint(into, n, hbHead);
        n = AddPoint(into, n, hbArmL);
        n = AddPoint(into, n, hbArmR);
        n = AddPoint(into, n, hbThighL);
        n = AddPoint(into, n, hbThighR);
        return n;
    }

    static int AddPoint(Vector3[] into, int n, Collider c)
    {
        if (c == null || n >= into.Length || !c.enabled) return n;
        into[n] = c.transform.position;
        return n + 1;
    }

    // the top of the head (crouching included), for the names over teammates
    public Vector3 HeadTop()
    {
        if (HasModel && hbHead != null) return hbHead.transform.position + Vector3.up * (0.155f * scaleK);
        if (HasModel && headBone != null) return headBone.position + Vector3.up * 0.25f;
        Collider c = GetComponent<Collider>();
        return c != null ? new Vector3(c.bounds.center.x, c.bounds.max.y, c.bounds.center.z) : transform.position + Vector3.up;
    }

    // the character's own capsule / CharacterController never collides with its hitboxes
    // (the ignore state is lost when a collider is turned off, so it is set again after a respawn)
    void IgnoreOwnCollisions()
    {
        foreach (Collider own in GetComponents<Collider>())
            foreach (Collider h in hitboxes)
                if (own != null && h != null) Physics.IgnoreCollision(own, h, true);
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

        if (!Build()) enabled = false;
    }

    // the model of the character's agent with its animation, bones, hitboxes and gun mount; false when there is none
    bool Build()
    {
        if (health == null) return false;
        builtAgent = WantedAgent();
        prefab = PrefabFor(health.team, builtAgent);
        if (prefab == null) return false;

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
            return false;
        }
        builtTeam = health.team;

        HideOwnRenderers(go.transform);

        model = go.transform;
        bottom = FeetY();
        modelY = model.localPosition.y + bottom;
        model.localPosition = new Vector3(0f, modelY, 0f);
        rootLayer = gameObject.layer == IgnoreRaycastLayer ? DefaultLayer : gameObject.layer;
        SetLayer(go, rootLayer);
        baseLocalRot = model.localRotation;
        frameRel = Quaternion.Inverse(model.rotation) * transform.rotation;

        foreach (SkinnedMeshRenderer r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;

        anim.playAutomatically = false;
        anim.cullingType = player != null ? AnimationCullingType.AlwaysAnimate : AnimationCullingType.BasedOnRenderers;

        FindBones();
        MeasureRest();

        // hitboxes on the bones; then the capsule / controller goes to Ignore Raycast, so only the hitboxes are hit
        hitboxes.AddRange(AddHitboxes(model, Frame));
        hbHead = FindHitbox(hitboxes, "Head");
        hbChest = FindHitbox(hitboxes, "Chest");
        hbStomach = FindHitbox(hitboxes, "Stomach");
        hbArmL = FindHitbox(hitboxes, "UpperArm.L");
        hbArmR = FindHitbox(hitboxes, "UpperArm.R");
        hbThighL = FindHitbox(hitboxes, "UpperLeg.L");
        hbThighR = FindHitbox(hitboxes, "UpperLeg.R");
        if (hitboxes.Count > 0 && hbHead != null)
        {
            gameObject.layer = IgnoreRaycastLayer;
            movedRootLayer = true;
            IgnoreOwnCollisions();
        }

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
        aimArm = AddLayer("Idle_Gun_Pointing", "AimArm", 1, shoulder, WrapMode.Loop, AnimationBlendMode.Blend);
        slash = AddLayer("Sword_Slash", "Slash", 2, abdomen, WrapMode.Once, AnimationBlendMode.Blend);
        hit = AddLayer("HitRecieve", "Hit", 3, abdomen, WrapMode.Once, AnimationBlendMode.Additive);

        anim.Stop();
        current = null;

        lastPos = transform.position;
        hasLastPos = true;
        RefreshRenderers();
        return true;
    }

    // The agent number: a bot's comes from its number in the team (the name T_3 / CT_3, given before the first model;
    // a halftime renames the bots, the number stays), so the agents alternate within a team. The player plays the
    // agent chosen for his side; "Авто" = like a bot, the player counting as number 0 (the second agent).
    int WantedAgent()
    {
        if (agentNumber < 0)
        {
            int number = player != null ? 0 : NumberIn(gameObject.name);
            if (number < 0) number = ++unnumbered;
            agentNumber = ((number - 1) % AgentCount + AgentCount) % AgentCount;
        }
        if (player != null && health != null)
        {
            int chosen = health.team == 1 ? GameSettings.AgentCT : health.team == 0 ? GameSettings.AgentT : 0;
            if (chosen > 0) return (chosen - 1) % AgentCount;
        }
        return agentNumber;
    }

    const int AgentCount = 1;
    static int unnumbered;   // characters without a number in the name get 1, 2, 3 ... in the order they are built

    // the number at the end of a name ("CT_12" -> 12), -1 if there is none
    static int NumberIn(string name)
    {
        int i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        if (i == name.Length || name.Length - i > 6) return -1;
        return int.Parse(name.Substring(i), System.Globalization.CultureInfo.InvariantCulture);
    }

    // the team changed (halftime, T <-> CT) or the player chose another agent: the new model replaces the old one
    void Rebuild()
    {
        Teardown();
        if (Build()) return;

        // no model for the new team: the capsule again
        foreach (Renderer r in hidden)
            if (r != null) r.forceRenderingOff = false;
        hidden.Clear();
        enabled = false;
    }

    // the old model goes away at once (its hitboxes too); the capsule stays hidden
    void Teardown()
    {
        if (model != null)
        {
            model.gameObject.SetActive(false);
            model.SetParent(null, false);
            Destroy(model.gameObject);
        }
        model = null;
        anim = null;
        mount = null;
        prefab = null;
        aimArm = slash = hit = null;
        current = null;
        idle = idleKnife = run = runBack = runLeft = runRight = walk = null;
        walkBack = false;
        changed.Clear();
        fingersL.Clear();
        hitboxes.Clear();
        hbHead = hbChest = hbStomach = hbArmL = hbArmR = hbThighL = hbThighR = null;
        // the shadow copies were children of the old model's renderers
        proxies.Clear();
        proxySet.Clear();
        viewSet = false;
        gunHolder = null;
        hasHandL = false;
        handRestL = chestRest = hipsRest = headRest = Quaternion.identity;
        legYawSet = false;
        yawSet = false;
        if (movedRootLayer)
        {
            gameObject.layer = rootLayer;
            movedRootLayer = false;
        }
    }

    void OnHalftime()
    {
        if (model != null && health != null && health.team != builtTeam) Rebuild();
    }

    void FindBones()
    {
        body = FindBone(model, "Body");
        hipsBone = FindBone(model, "Hips");
        abdomen = FindBone(model, "Abdomen");
        torso = FindBone(model, "Torso");
        chest = FindBone(model, "Chest");
        neck = FindBone(model, "Neck");
        headBone = FindBone(model, "Head");
        shoulderL = FindBone(model, "Shoulder.L");
        upperArmL = FindBone(model, "UpperArm.L");
        lowerArmL = FindBone(model, "LowerArm.L");
        wristL = FindBone(model, "Wrist.L");
        shoulderR = FindBone(model, "Shoulder.R");
        upperArmR = FindBone(model, "UpperArm.R");
        lowerArmR = FindBone(model, "LowerArm.R");
        wristR = FindBone(model, "Wrist.R");
        upperLegL = FindBone(model, "UpperLeg.L");
        lowerLegL = FindBone(model, "LowerLeg.L");
        footL = FindBone(model, "Foot.L");
        upperLegR = FindBone(model, "UpperLeg.R");
        lowerLegR = FindBone(model, "LowerLeg.R");
        footR = FindBone(model, "Foot.R");

        foreach (Transform t in new[] { body, abdomen, torso, chest, neck, headBone,
                                        upperLegL, lowerLegL, footL, upperLegR, lowerLegR, footR,
                                        shoulderL, upperArmL, lowerArmL, wristL, shoulderR, upperArmR, lowerArmR, wristR })
            if (t != null) changed.Add(new BoneState { t = t });

        // the left fingers (1 = in the palm, 2..4 bend), they curl around the fore-end
        foreach (string f in new[] { "Index", "Middle", "Ring", "Pinky" })
        {
            Transform[] chain = new Transform[5];
            for (int i = 0; i < 4; i++) chain[i] = FindBone(model, f + (i + 1) + ".L");
            chain[4] = FindBone(model, f + "4.L_end");
            if (chain[0] == null || chain[1] == null || chain[2] == null) continue;
            fingersL.Add(chain);
            for (int i = 1; i < 4; i++)
                if (chain[i] != null) changed.Add(new BoneState { t = chain[i] });
        }
    }

    // what the IK needs from the rest pose: the ankles in the shins' space, the left hand's frame, the chest's rotation
    void MeasureRest()
    {
        RestPose rest = new RestPose(model);
        Quaternion frame = Frame;
        Transform hips = FindBone(model, "Hips");
        if (rest.Has(hips) && rest.Has(headBone))
        {
            float d = (rest.Pos(headBone) - rest.Pos(hips)).magnitude;
            if (d > 0.01f) scaleK = d / BlenderHipsToHead;
        }

        if (lowerLegL != null && footL != null) shinTipL = rest.LocalPoint(lowerLegL, rest.Pos(footL));
        if (lowerLegR != null && footR != null) shinTipR = rest.LocalPoint(lowerLegR, rest.Pos(footR));

        // fingers (wrist -> middle finger) and thumb side of the left hand; the palm faces down in the T-pose
        Transform middle = FindBone(model, "Middle1.L");
        Transform thumb = FindBone(model, "Thumb2.L");
        if (thumb == null) thumb = FindBone(model, "Thumb1.L");
        if (wristL != null && middle != null && thumb != null)
        {
            Vector3 w = rest.Pos(wristL);
            Vector3 f = rest.Pos(middle) - w;
            Vector3 th = Vector3.ProjectOnPlane(rest.Pos(thumb) - w, f);
            Vector3 n = Vector3.Cross(f, th);
            if (Vector3.Dot(n, frame * Vector3.down) < 0f) n = -n;
            Vector3 fl = rest.LocalDir(wristL, f);
            Vector3 nl = rest.LocalDir(wristL, n);
            if (fl.sqrMagnitude > 1e-10f && nl.sqrMagnitude > 1e-10f)
            {
                handRestL = Quaternion.Inverse(Quaternion.LookRotation(fl, nl));
                hasHandL = true;
            }
        }

        chestRest = RestRel(rest, chest, frame);
        hipsRest = RestRel(rest, hipsBone, frame);
        headRest = RestRel(rest, headBone, frame);
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
            if (!hidden.Contains(r)) hidden.Add(r);
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
        RoundManager.Halftime += OnHalftime;

        // after a respawn: no jump of the speed, the animation starts again
        hasLastPos = false;
        velocity = Vector3.zero;
        current = null;
        aimWeight = 0f;
        lookWeight = 0f;
        kick = 0f;
        yawSet = false;
        legYawSet = false;
        airTime = 0f;
        airWeight = 0f;
        airClip = false;
        wasInAir = false;
        landTimer = 0f;
        walkBack = false;
        foreach (BoneState b in changed) b.set = false;
        if (hitboxes.Count > 0) IgnoreOwnCollisions();
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
        RoundManager.Halftime -= OnHalftime;
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
        // the capsule takes the bullets again
        if (movedRootLayer) gameObject.layer = rootLayer;
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
        else if (!s.pellet)
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

        Health target = bot.Target;
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

        // halftime swapped the teams (RoundManager.Halftime, or the team was changed some other way), or the player
        // chose another agent in the settings
        if (health != null && (health.team != builtTeam || (player != null && WantedAgent() != builtAgent)))
        {
            Rebuild();
            if (model == null) return;
        }

        // the player is not deactivated when he dies: his model goes away here (the body is made by Corpses)
        bool dead = health != null && health.IsDead;
        if (model.gameObject.activeSelf == dead)
        {
            model.gameObject.SetActive(!dead);
            current = null;
            if (!dead && hitboxes.Count > 0) IgnoreOwnCollisions();
        }
        // RoundManager.Place switches the CharacterController off and on at every round start; set the ignore pairs
        // again then, so the controller can never collide with its own hitboxes (cheap: once per switch)
        bool ccOn = cc != null && cc.enabled;
        if (ccOn && !ccWasOn && !dead && hitboxes.Count > 0) IgnoreOwnCollisions();
        ccWasOn = ccOn;
        if (dead || anim == null) return;

        Weapon w = CurrentWeapon();
        bool knife = w != null && w.IsMelee;

        // paused (time scale 0): the clip is still chosen, nothing moves
        float dt = Time.deltaTime;
        if (dt > 0f)
        {
            UpdateVelocity(dt);
            aiming = FindAim(out Vector3 dir);
            if (aiming && dir.sqrMagnitude > 1e-6f) aimDir = dir.normalized;
            aimWeight = Mathf.MoveTowards(aimWeight, aiming && !knife ? 1f : 0f, aimBlendSpeed * dt);
            lookWeight = Mathf.MoveTowards(lookWeight, aiming ? 1f : 0f, aimBlendSpeed * dt);
            kick *= Mathf.Exp(-12f * dt);

            // two hands on a gun that has a model in the fist; a long gun or a pistol
            bool gun = w != null && !knife && w.data != null && HolderUnderMount() != null;
            holdWeight = Mathf.MoveTowards(holdWeight, gun ? 1f : 0f, holdBlendSpeed * dt);
            bool longGun = w != null && w.data != null && w.data.category != WeaponCategory.Pistol &&
                           w.data.category != WeaponCategory.Gear;
            longWeight = Mathf.MoveTowards(longWeight, longGun ? 1f : 0f, holdBlendSpeed * dt);
            drawBlend = Mathf.MoveTowards(drawBlend, w != null && w.IsDrawing ? 1f : 0f, 5f * dt);
            reloadBlend = Mathf.MoveTowards(reloadBlend, w != null && w.IsReloading ? 1f : 0f, 5f * dt);

            if (bot != null) crouchWeight = bot.CrouchAmount;
            else if (player != null) crouchWeight = 1f - player.StandFactor;

            UpdateAir(dt);
            TurnModel(dt);
        }

        ChooseClip(knife, dt);

        if (aimArm != null)
        {
            // the fist of the pointing pose holds the grip (the arm itself is moved by the IK)
            float wgt = Mathf.Max(aimWeight, holdWeight);
            aimArm.enabled = wgt > 0.001f;
            aimArm.weight = wgt;
        }
    }

    // The model faces where the character aims (the player: always his view), a bot that does not aim faces where it
    // runs; standing it faces the bot's own forward. The bot's transform turns slower, so this fixes running sideways.
    void TurnModel(float dt)
    {
        Vector3 want = transform.forward;
        if (player == null)
        {
            if (aiming) want = aimDir;
            else if (moving) want = velocity;
        }
        want.y = 0f;
        if (want.sqrMagnitude < 1e-4f) want = transform.forward;

        float target = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
        if (player != null || !yawSet) modelYaw = target;
        else modelYaw = Mathf.MoveTowardsAngle(modelYaw, target, turnSpeed * dt);
        yawSet = true;

        float local = Mathf.DeltaAngle(transform.eulerAngles.y, modelYaw);
        model.localRotation = Quaternion.AngleAxis(local, Vector3.up) * baseLocalRot;
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

    // In the air: the player after airDelay off the ground, a bot while it hops. The jump pose comes in, and a short
    // dip of the crouch pose follows the landing.
    void UpdateAir(float dt)
    {
        bool inAir = false;
        if (player != null)
        {
            airTime = player.IsGrounded ? 0f : airTime + dt;
            inAir = airTime > airDelay;
        }
        else if (bot != null)
        {
            inAir = bot.IsHopping;
        }

        if (wasInAir && !inAir && airWeight > 0.5f) landTimer = landTime;
        wasInAir = inAir;
        airWeight = Mathf.MoveTowards(airWeight, inAir ? 1f : 0f, airBlendSpeed * dt);
        landTimer = Mathf.Max(0f, landTimer - dt);

        float dip = landTime > 0f && landTimer > 0f ? Mathf.Sin(Mathf.PI * (1f - landTimer / landTime)) * landDip : 0f;
        poseCrouch = Mathf.Max(crouchWeight, dip);
    }

    void ChooseClip(bool knife, float dt)
    {
        float speed = velocity.magnitude;
        // a gap between the thresholds, so the clip does not flicker at one speed
        moving = moving ? speed > 0.25f : speed > 0.45f;
        // the jump pose stands on the idle clip (the bhop of bots touches the ground for one step between hops)
        airClip = airClip ? airWeight > 0.2f : airWeight > 0.5f;

        string next;
        float clipSpeed = 1f;
        float legs = 0f;
        bool slow = (bot != null && bot.IsSneaking) || (player != null && player.IsSneaking) || crouchWeight > 0.5f;

        // the direction of the movement compared with where the model faces (the aim, or the run itself): + = right
        Vector3 local = Quaternion.Inverse(Frame) * velocity;
        float angle = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;

        if (airClip || !moving)
        {
            next = knife ? idleKnife : idle;
            walkBack = false;
        }
        else if (slow)
        {
            // walking backwards plays the walk backwards; the legs face the walk direction (at most sideways)
            float border = 90f + (walkBack && current == walk ? -clipHysteresis : clipHysteresis);
            walkBack = Mathf.Abs(angle) > border;
            next = walk;
            clipSpeed = speed / walkAnimSpeed * (walkBack ? -1f : 1f);
            legs = walkBack ? Mathf.DeltaAngle(180f, angle) : angle;
        }
        else
        {
            walkBack = false;
            next = RunClip(angle);
            // the legs run along the movement: Run and the strafe clips forwards, Run_Back backwards
            legs = next == runBack && runBack != run ? Mathf.DeltaAngle(180f, angle) : angle;
            clipSpeed = speed / runAnimSpeed;
        }

        // the hips turn smoothly to the new direction (a clip change does not snap the legs)
        legTarget = Mathf.Clamp(legs, -maxLegYaw, maxLegYaw);
        legYaw = legYawSet ? Mathf.MoveTowardsAngle(legYaw, legTarget, legTurnSpeed * dt) : legTarget;
        legYawSet = true;
        if (next == null) return;

        if (next != current || !anim.IsPlaying(next))
        {
            if (current == null) anim.Play(next);
            else anim.CrossFade(next, crossFade);
            current = next;
        }
        float s = Mathf.Clamp(Mathf.Abs(clipSpeed), 0.5f, 2f);
        anim[current].speed = clipSpeed < 0f ? -s : s;
    }

    // forward up to 50 degrees, sideways (Run_Left / Run_Right) up to 90, then backwards (the hips turn at most
    // maxLegYaw, so the legs slide least with the border at 90); each border moves away from the clip that plays
    // now, so it does not flicker
    string RunClip(float angle)
    {
        float a = Mathf.Abs(angle);
        float forwardEnd = 50f + (current == run ? clipHysteresis : -clipHysteresis);
        float backStart = 90f + (current == runBack ? -clipHysteresis : clipHysteresis);
        if (a <= forwardEnd) return run;
        if (a >= backStart) return runBack;
        return angle > 0f ? runRight : runLeft;
    }

    void LateUpdate()
    {
        if (isManager || model == null || !model.gameObject.activeInHierarchy) return;

        PlaceOnFloor();
        UpdateView();
        if (anim == null) return;

        BeginBones();
        FaceLegs();
        AirPose();
        CrouchPose();
        AimChest();
        if (holdWeight > 0.001f && mount != null) HoldGun();
        else AimMount();
        EndBones();

        // a bot sees and shoots from its visible head (it follows the crouch and the run), so its eyes are always
        // inside its own head hitbox; the rotation stays BotController's
        if (bot != null && bot.head != null && hbHead != null)
            bot.head.position = hbHead.transform.position - Vector3.up * EyesUnderHeadCentre;

        // the hitboxes moved with the bones: bullets and vision of the next frame see the pose that is drawn
        if (hitboxes.Count > 0) Physics.SyncTransforms();
    }

    void BeginBones()
    {
        foreach (BoneState b in changed)
        {
            // the animation did not write this bone this frame: undo last frame's change, or it would add up
            if (b.set && b.t.localPosition == b.setPos && b.t.localRotation == b.setRot)
            {
                b.t.localPosition = b.basePos;
                b.t.localRotation = b.baseRot;
            }
            b.basePos = b.t.localPosition;
            b.baseRot = b.t.localRotation;
        }
    }

    void EndBones()
    {
        foreach (BoneState b in changed)
        {
            b.setPos = b.t.localPosition;
            b.setRot = b.t.localRotation;
            b.set = true;
        }
    }

    // feet exactly on the floor under the character (the NavMesh is not exactly at the floor), not in a jump
    void PlaceOnFloor()
    {
        float target = modelY;
        bool grounded = cc != null ? cc.isGrounded : agent == null || agent.baseOffset <= startBaseOffset + 0.01f;
        if (grounded && bottom < 0f)
        {
            // from the centre down to a bit below the feet; the own collider and hitboxes do not count
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

    // ---------------- view: the camera's own character ----------------

    // the character the main camera is in (the living player, a watched bot) goes to LocalBodyLayer
    void UpdateView()
    {
        Camera cam = Camera.main;
        if (cam != null && (cam.cullingMask & (1 << LocalBodyLayer)) != 0) cam.cullingMask &= ~(1 << LocalBodyLayer);
        bool own = cam != null && cam.transform.IsChildOf(transform);
        if (viewSet && own == ownView && Time.time < nextRendererCheck) return;
        ApplyView(own);
    }

    void ApplyView(bool own)
    {
        ownView = own;
        viewSet = true;
        nextRendererCheck = Time.time + 0.5f;
        int layer = own ? LocalBodyLayer : rootLayer;

        model.GetComponentsInChildren(true, rendererBuffer);
        foreach (Renderer r in rendererBuffer)
        {
            if (proxySet.Contains(r)) continue;
            if (r.gameObject.layer != layer) r.gameObject.layer = layer;
            if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) r.shadowCastingMode = ShadowCastingMode.On;
            if (own) EnsureProxy(r);
        }
        rendererBuffer.Clear();

        if (own) PruneProxies();
        else ClearProxies();

        // a watched bot is not drawn by the main camera, its animation must still run
        if (anim != null && player == null)
            anim.cullingType = own ? AnimationCullingType.AlwaysAnimate : AnimationCullingType.BasedOnRenderers;
    }

    // a shadow-only copy of a renderer on the Default layer: the main camera does not draw LocalBodyLayer,
    // and a renderer it does not draw casts no shadow in its view either
    void EnsureProxy(Renderer src)
    {
        if (proxies.TryGetValue(src, out Renderer p) && p != null)
        {
            p.enabled = src.enabled;
            return;
        }

        GameObject go = new GameObject(src.name + " (shadow)");
        go.layer = DefaultLayer;
        go.transform.SetParent(src.transform, false);
        Renderer r = null;
        if (src is SkinnedMeshRenderer s)
        {
            SkinnedMeshRenderer d = go.AddComponent<SkinnedMeshRenderer>();
            d.sharedMesh = s.sharedMesh;
            d.bones = s.bones;
            d.rootBone = s.rootBone;
            d.localBounds = s.localBounds;
            d.quality = s.quality;
            d.updateWhenOffscreen = true;
            r = d;
        }
        else if (src is MeshRenderer)
        {
            MeshFilter f = src.GetComponent<MeshFilter>();
            if (f != null && f.sharedMesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
                r = go.AddComponent<MeshRenderer>();
            }
        }
        if (r == null)
        {
            Destroy(go);
            return;
        }
        r.sharedMaterials = src.sharedMaterials;
        r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        r.receiveShadows = false;
        r.enabled = src.enabled;
        proxies[src] = r;
        proxySet.Add(r);
    }

    // copies of renderers that are gone (an old gun)
    void PruneProxies()
    {
        foreach (KeyValuePair<Renderer, Renderer> pair in proxies)
            if (pair.Key == null || pair.Value == null) staleBuffer.Add(pair.Key);
        foreach (Renderer r in staleBuffer)
        {
            if (proxies.TryGetValue(r, out Renderer p))
            {
                if (p != null) Destroy(p.gameObject);
                proxySet.Remove(p);
            }
            proxies.Remove(r);
        }
        staleBuffer.Clear();
        proxySet.RemoveWhere(x => x == null);
    }

    void ClearProxies()
    {
        foreach (Renderer p in proxies.Values)
            if (p != null) Destroy(p.gameObject);
        proxies.Clear();
        proxySet.Clear();
    }

    // call after adding renderers to the model (a gun), so they get the right layer at once
    public void RefreshRenderers()
    {
        if (model == null) return;
        Camera cam = Camera.main;
        ApplyView(cam != null && cam.transform.IsChildOf(transform));
    }

    // ---------------- pose after the animation ----------------

    // The hips face legYaw from the frame: 0 = the aim (standing, in the air, running straight), the movement
    // direction while strafing or running diagonally (Run_Left / Run_Right turn them 90 degrees by themselves; the
    // legs run along the movement, they never slide sideways). Measured on the clip's pose every frame.
    void FaceLegs()
    {
        if (body == null || hipsBone == null) return;
        Quaternion f = Frame;
        float e = Mathf.DeltaAngle(YawOf(hipsBone, hipsRest, f), legYaw);
        TurnLowerBody(body, footL, footR, e, f * Vector3.up);
    }

    // in the air (the models have no jump clip): the feet come up, one a bit forward, the toes point down,
    // the knees bend forward (leg IK to the raised feet)
    void AirPose()
    {
        float w = airWeight;
        if (w < 0.001f || footL == null || footR == null) return;

        // the frame of the legs: where the hips face (FaceLegs turned them to legYaw)
        Quaternion f = Quaternion.AngleAxis(legYaw, Frame * Vector3.up) * Frame;
        Vector3 up = f * Vector3.up, fwd = f * Vector3.forward, right = f * Vector3.right;
        float k = w * scaleK;
        Quaternion toes = Quaternion.AngleAxis(25f * w, right);
        footL.SetPositionAndRotation(footL.position + (up * airTuck + fwd * 0.06f) * k, toes * footL.rotation);
        footR.SetPositionAndRotation(footR.position + (up * (airTuck * 0.75f) - fwd * 0.08f) * k, toes * footR.rotation);

        if (upperLegL != null && lowerLegL != null)
            SolveTwoBone(upperLegL, lowerLegL, shinTipL, footL.position, fwd - right * 0.15f);
        if (upperLegR != null && lowerLegR != null)
            SolveTwoBone(upperLegR, lowerLegR, shinTipR, footR.position, fwd + right * 0.15f);
    }

    // crouch (and the landing dip): hips down and back, the legs bend so the feet stay where the clip has them
    // (the spine leans forward in AimChest)
    void CrouchPose()
    {
        float w = poseCrouch;
        if (w < 0.001f || body == null) return;

        // the frame of the legs: where the hips face (FaceLegs turned them to legYaw), the knees bend that way
        Quaternion f = Quaternion.AngleAxis(legYaw, Frame * Vector3.up) * Frame;
        Vector3 up = f * Vector3.up, fwd = f * Vector3.forward, right = f * Vector3.right;

        // the ankles of the clip, before the hips move
        Vector3 ankleL = footL != null ? footL.position : Vector3.zero;
        Vector3 ankleR = footR != null ? footR.position : Vector3.zero;

        body.position += (-up * crouchDrop - fwd * crouchBack) * (w * scaleK);

        // knees forward and a little outward
        if (footL != null && upperLegL != null && lowerLegL != null)
            SolveTwoBone(upperLegL, lowerLegL, shinTipL, ankleL, fwd - right * 0.15f);
        if (footR != null && upperLegR != null && lowerLegR != null)
            SolveTwoBone(upperLegR, lowerLegR, shinTipR, ankleR, fwd + right * 0.15f);
    }

    // The chest faces exactly where the character looks: the spine turns the chest of the clip (and of the turned
    // lower body) to the look yaw, then leans for a crouch and pitches with the aim (half in Torso, half in Chest; the
    // shot kicks it up); the neck and head look exactly along the aim. Not aiming, the look is the frame's forward
    // (the run, or the bot's own forward) and the head keeps the nod of the clip.
    void AimChest()
    {
        if (chest == null) return;
        Quaternion f = Frame;
        Vector3 up = f * Vector3.up, fwd = f * Vector3.forward;

        float yaw = 0f, pitch = 0f, headPitch = 0f;
        if (lookWeight > 0.001f)
        {
            Vector3 flatAim = Vector3.ProjectOnPlane(aimDir, up);
            if (flatAim.sqrMagnitude > 1e-4f)
                yaw = Mathf.Clamp(Vector3.SignedAngle(fwd, flatAim, up), -maxAimYaw, maxAimYaw) * lookWeight;
            float raw = -Mathf.Asin(Mathf.Clamp(Vector3.Dot(aimDir, up), -1f, 1f)) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(raw, -maxAimPitch, maxAimPitch) * lookWeight;
            headPitch = Mathf.Clamp(raw, -maxHeadPitch, maxHeadPitch) * lookWeight;
        }
        Vector3 right = Quaternion.AngleAxis(yaw, up) * (f * Vector3.right);

        TurnSpine(abdomen, torso, chest, chestRest, f, yaw);

        if (abdomen != null && poseCrouch > 0.001f)
            abdomen.rotation = Quaternion.AngleAxis(crouchLean * poseCrouch, right) * abdomen.rotation;
        float p = pitch - kick * 0.3f;
        if (Mathf.Abs(p) > 0.01f)
        {
            Quaternion half = Quaternion.AngleAxis(p * 0.5f, right);
            if (torso != null) torso.rotation = half * torso.rotation;
            chest.rotation = half * chest.rotation;
        }

        // crouched the head looks ahead, not at the floor
        if (headBone != null) TurnHead(neck, headBone, headRest, f, yaw, headPitch, Mathf.Max(lookWeight, poseCrouch));
    }

    // the knife (or a gun without a model) follows the fist; while aiming its blade is turned along the aim
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

    Transform HolderUnderMount()
    {
        if (mount == null) return null;
        for (int i = 0; i < mount.childCount; i++)
        {
            Transform c = mount.GetChild(i);
            if (c.name == BotWeaponModels.HolderName && c.gameObject.activeSelf) return c;
        }
        return null;
    }

    // The gun's points in the mount's space (grip at 0, barrel +Z, top +Y, meters): the end of the stock,
    // the fore-end for the left hand, the magazine. From the model's meshes and its Muzzle / Mag / Fore objects.
    void MeasureGun(Transform holder)
    {
        gunHolder = holder;
        bool longGun = longWeight > 0.5f;
        Vector3 muzzle = longGun ? new Vector3(0f, 0.09f, 0.6f) : new Vector3(0f, 0.06f, 0.18f);
        float rear = longGun ? -0.25f : -0.03f;

        if (holder != null)
        {
            Transform m = ViewModel.FindChild(holder, "Muzzle");
            if (m != null) muzzle = mount.InverseTransformPoint(m.position);

            bool any = false;
            float minZ = 0f;
            foreach (MeshFilter mf in holder.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                    float z = mount.InverseTransformPoint(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, corner))).z;
                    if (!any || z < minZ) minZ = z;
                    any = true;
                }
            }
            if (any) rear = Mathf.Min(minZ, 0f);
        }

        gunButt = new Vector3(0f, Mathf.Clamp(muzzle.y * 0.3f, 0f, 0.06f), rear);
        // under the handguard, about halfway to the muzzle (closer on long guns: the arms are short),
        // and closer still behind a long stock (AWP), which already puts the grip far forward
        float foreZ = Mathf.Clamp(muzzle.z * 0.45f, 0.14f, 0.26f) - Mathf.Max(0f, -rear - 0.26f);
        gunFore = new Vector3(0f, Mathf.Max(0f, muzzle.y - 0.06f), Mathf.Max(foreZ, 0.12f));
        gunMag = new Vector3(0f, -0.1f, 0.12f);

        if (holder != null)
        {
            Transform fore = ViewModel.FindChild(holder, "Fore");
            if (fore == null) fore = ViewModel.FindChild(holder, "ForeEnd");
            if (fore != null) gunFore = mount.InverseTransformPoint(fore.position);
            Transform mag = ViewModel.FindChild(holder, "Mag");
            if (mag != null)
            {
                Vector3 p = mount.InverseTransformPoint(mag.position);
                gunMag = new Vector3(0f, Mathf.Min(p.y, 0f) - 0.06f, Mathf.Max(p.z, 0.02f));
            }
        }
    }

    // Both hands on the gun. The gun's pose comes from the aim (low ready without a target): a long gun has its stock
    // at the right shoulder of the square chest (the left shoulder comes forward, the right one back a little, so the
    // short arms of the model reach), a pistol is held forward in the middle. The right wrist goes where the mount is
    // the gun's grip, the left hand to the fore-end (pistol: on the left of the grip, rifle reloading: to the
    // magazine). If a hand cannot reach, the gun slides back along the aim.
    void HoldGun()
    {
        mount.localPosition = mountPos;
        mount.localRotation = mountRot;
        if (upperArmR == null || lowerArmR == null || wristR == null) return;

        Transform holder = HolderUnderMount();
        if (holder != gunHolder || holder == null) MeasureGun(holder);

        Quaternion f = Frame;
        Vector3 up = f * Vector3.up, fwd = f * Vector3.forward, right = f * Vector3.right;
        float k = scaleK;

        // the shoulders (clavicles) turn about the vertical: + moves the left one forward and the right one back
        float sw = holdWeight * longWeight;
        if (sw > 0.001f)
        {
            if (shoulderL != null) shoulderL.rotation = Quaternion.AngleAxis(supportShoulder * sw, up) * shoulderL.rotation;
            if (shoulderR != null) shoulderR.rotation = Quaternion.AngleAxis(rightShoulderBack * sw, up) * shoulderR.rotation;
        }

        // the barrel direction: low ready -> the aim; lowered while drawing and reloading
        Vector3 low = Quaternion.AngleAxis(lowReadyPitch, right) * fwd;
        Vector3 dir = Vector3.Slerp(low, aimDir, aimWeight).normalized;
        Vector3 side = Vector3.Cross(Vector3.up, dir);
        if (side.sqrMagnitude < 1e-6f) side = right;
        side.Normalize();
        dir = Quaternion.AngleAxis(drawBlend * 35f + reloadBlend * 12f - kick, side) * dir;
        Quaternion gunRot = Quaternion.LookRotation(dir, Vector3.up) * Quaternion.AngleAxis(-reloadBlend * 15f, Vector3.forward);

        // the chest's frame (AimChest turned it to the aim)
        Quaternion chestFrame = chest != null ? chest.rotation * Quaternion.Inverse(chestRest) : f;
        Vector3 pistolPos = (upperArmR.position + (upperArmL != null ? upperArmL.position : upperArmR.position)) * 0.5f
                            + dir * (pistolReach * k) - up * (pistolDrop * k);
        Vector3 pocket = upperArmR.position + chestFrame * (stockPocket * k);
        Vector3 riflePos = pocket - gunRot * (gunButt * k);
        Vector3 gunPos = Vector3.Lerp(pistolPos, riflePos, longWeight) - dir * (kick * 0.003f);

        // the right wrist: the mount (child of the wrist) must be the gun's grip
        Quaternion wristRot = gunRot * Quaternion.Inverse(mountRot);
        Vector3 offset = wristRot * Vector3.Scale(mountPos, wristR.lossyScale);
        Vector3 targetR = gunPos - offset;

        // the left hand: palm up under the fore-end (long gun) or palm on the left of the grip (pistol)
        Vector3 gR = gunRot * Vector3.right, gU = gunRot * Vector3.up, gF = gunRot * Vector3.forward;
        Vector3 fingers, palm, handPoint;
        float reloadHand = ReloadHand();
        if (longWeight > 0.5f)
        {
            // the fingers wrap the fore-end from below towards the right and forward, the forearm comes from behind
            fingers = (gR * 0.75f + gF * 0.66f).normalized;
            palm = gU;
            Vector3 fore = Vector3.Lerp(gunFore, gunMag, reloadHand) * k;
            handPoint = gunPos + gunRot * fore;
        }
        else
        {
            palm = (gR - gF * 0.3f).normalized;
            fingers = Vector3.ProjectOnPlane(gF * 0.75f - gU * 0.55f, palm).normalized;
            Vector3 grip = new Vector3(-0.045f, -0.025f, 0f);
            handPoint = gunPos + gunRot * (Vector3.Lerp(grip, gunMag, reloadHand) * k);
        }
        Quaternion leftRot = Quaternion.LookRotation(fingers, palm) * handRestL;
        Vector3 targetL = handPoint - fingers * (0.085f * k) - palm * (0.03f * k);
        bool left = hasHandL && upperArmL != null && lowerArmL != null && wristL != null;

        // slide the gun back along the aim until both wrists are in reach
        float reachR = ArmLength(upperArmR, lowerArmR, wristR) * 0.97f;
        float pull = PullBack(upperArmR.position, targetR, dir, reachR);
        if (left) pull = Mathf.Max(pull, PullBack(upperArmL.position, targetL, dir, ArmLength(upperArmL, lowerArmL, wristL) * 0.97f));
        pull = Mathf.Min(pull, maxPullBack * k);
        targetR -= dir * pull;
        targetL -= dir * pull;

        // elbows down and out
        Vector3 poleR = right * 0.6f - up - fwd * 0.2f;
        Vector3 poleL = -right * 0.7f - up;
        SolveArm(upperArmR, lowerArmR, wristR, targetR, poleR, wristRot, holdWeight);
        if (left)
        {
            SolveArm(upperArmL, lowerArmL, wristL, targetL, poleL, leftRot, holdWeight);
            CurlFingers(holdWeight);
        }
    }

    // each joint of the left fingers bends towards the palm: a loose fist around the fore-end or the grip.
    // Set from the bone in the palm, so the curl of the clip does not add to it.
    void CurlFingers(float weight)
    {
        if (fingerCurl <= 0f || weight <= 0.001f) return;
        Vector3 palm = wristL.rotation * (Quaternion.Inverse(handRestL) * Vector3.up);
        foreach (Transform[] chain in fingersL)
        {
            Vector3 m = chain[1].position - chain[0].position;
            if (m.sqrMagnitude < 1e-10f) continue;
            m.Normalize();
            Vector3 n = Vector3.ProjectOnPlane(palm, m);
            if (n.sqrMagnitude < 1e-8f) continue;
            n.Normalize();

            for (int j = 1; j < 4; j++)
            {
                Transform seg = chain[j], next = chain[j + 1];
                if (seg == null || next == null) break;
                Vector3 cur = next.position - seg.position;
                if (cur.sqrMagnitude < 1e-10f) break;
                float a = fingerCurl * j * Mathf.Deg2Rad;
                Vector3 want = m * Mathf.Cos(a) + n * Mathf.Sin(a);
                Quaternion full = Quaternion.FromToRotation(cur, want) * seg.rotation;
                seg.rotation = Quaternion.Slerp(seg.rotation, full, weight);
            }
        }
    }

    // 0..1: how far the left hand is on the way to the magazine while reloading
    float ReloadHand()
    {
        Weapon w = CurrentWeapon();
        if (w == null || !w.IsReloading) return 0f;
        float p = w.IsShellReload ? w.ShellProgress : w.ReloadProgress;
        // out to the magazine, back to the fore-end at the end; a shell reload goes once per shell
        return Mathf.Clamp01(Mathf.Sin(Mathf.Clamp01(p) * Mathf.PI) * 1.6f);
    }

    static float ArmLength(Transform upper, Transform lower, Transform end)
    {
        return (lower.position - upper.position).magnitude + (end.position - lower.position).magnitude;
    }

    // how far the target must move back along dir to be within reach of the shoulder
    static float PullBack(Vector3 shoulder, Vector3 target, Vector3 dir, float reach)
    {
        Vector3 v = target - shoulder;
        float d2 = v.sqrMagnitude;
        if (d2 <= reach * reach) return 0f;
        float b = Vector3.Dot(v, dir);
        float disc = b * b - (d2 - reach * reach);
        // moving back along dir never gets in reach: the closest place
        if (disc < 0f) return Mathf.Max(0f, b);
        return Mathf.Max(0f, b - Mathf.Sqrt(disc));
    }

    // two-bone IK of an arm to the wrist target, then the wrist turned to its rotation; weight blends with the clip
    static void SolveArm(Transform upper, Transform lower, Transform wrist, Vector3 target, Vector3 pole,
                         Quaternion wristRot, float weight)
    {
        Quaternion u0 = upper.localRotation, l0 = lower.localRotation, w0 = wrist.localRotation;
        SolveTwoBone(upper, lower, lower.InverseTransformPoint(wrist.position), target, pole);
        wrist.rotation = wristRot;
        if (weight >= 0.999f) return;
        upper.localRotation = Quaternion.Slerp(u0, upper.localRotation, weight);
        lower.localRotation = Quaternion.Slerp(l0, lower.localRotation, weight);
        wrist.localRotation = Quaternion.Slerp(w0, wrist.localRotation, weight);
    }

    // Analytic two-bone IK: the upper bone turns so the middle joint is where the triangle (upper length,
    // lower length, distance to the target) puts it, bent towards the pole; then the lower bone turns so its end
    // (endLocal: the end of the chain in the lower bone's space) is at the target. A target out of reach is
    // approached as close as the straight chain allows.
    static void SolveTwoBone(Transform upper, Transform lower, Vector3 endLocal, Vector3 target, Vector3 pole)
    {
        Vector3 a = upper.position, b = lower.position;
        Vector3 end = lower.TransformPoint(endLocal);
        float la = (b - a).magnitude, lb = (end - b).magnitude;
        if (la < 1e-4f || lb < 1e-4f) return;

        Vector3 toTarget = target - a;
        float d = toTarget.magnitude;
        if (d < 1e-4f) return;
        Vector3 dir = toTarget / d;
        d = Mathf.Clamp(d, Mathf.Abs(la - lb) + 1e-3f, la + lb - 1e-3f);

        // the bend plane from the pole, across the line to the target
        Vector3 bend = Vector3.ProjectOnPlane(pole, dir);
        if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(b - a, dir);
        if (bend.sqrMagnitude < 1e-8f) return;
        bend.Normalize();

        float cosA = Mathf.Clamp((la * la + d * d - lb * lb) / (2f * la * d), -1f, 1f);
        float sinA = Mathf.Sqrt(1f - cosA * cosA);
        Vector3 mid = a + dir * (la * cosA) + bend * (la * sinA);

        upper.rotation = Quaternion.FromToRotation(b - a, mid - a) * upper.rotation;

        // the end moved with the upper bone: where it is now, and where it should be
        Vector3 endNow = lower.TransformPoint(endLocal);
        Vector3 endWant = a + dir * d;
        Vector3 from = endNow - lower.position;
        Vector3 to = endWant - lower.position;
        if (from.sqrMagnitude > 1e-10f && to.sqrMagnitude > 1e-10f)
            lower.rotation = Quaternion.FromToRotation(from, to) * lower.rotation;
    }
}
