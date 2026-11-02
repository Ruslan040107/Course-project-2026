using System.Collections.Generic;
using UnityEngine;

// Immortal target dummy for solo test mode: shows every hit and some stats.
// With graphics it is a static model of the first CT agent (CT_SAS; CT_Swat while that is not in the project) in its
// idle pose (no movement) instead of the capsule, with "Boost player contrast" like the bots, the root turned
// once to face the player and the pose straightened to it (CharacterBody.StraightenPose: hips, chest and head face
// the root's forward), with the same bone hitboxes as the bots (CharacterBody.AddHitboxes): the panel shows the
// body part of every hit (head, chest and arms, stomach, legs) and counts the hits per part. The capsule then goes to the Ignore Raycast layer,
// so only the hitboxes are hit. Without graphics (or without the model) it stays the capsule, hit by height.
[RequireComponent(typeof(Health))]
public class TrainingTarget : MonoBehaviour
{
    public int team = 1;
    public float popupTime = 1.2f;
    public float dpsWindow = 1f;
    public KeyCode resetKey = KeyCode.T;

    [Header("Model")]
    [Tooltip("Resources path of the model, empty = keep the capsule (the old default CT_Swat means the first CT agent)")]
    public string modelPath = DefaultModel;
    [Tooltip("the clip of the pose and the moment of it (0..1)")] public string poseClip = "Idle";
    [Range(0f, 1f)] public float poseTime = 0f;
    [Tooltip("the model turns to the player once, at the start")] public bool facePlayer = true;

    // the first CT agent (CharacterBody.LoadModel falls back to CT_Swat while it is not in the project)
    public const string DefaultModel = "Characters/CT_SAS";

    struct Popup
    {
        public string text;
        public Vector3 pos;
        public float born;
        public bool head;
    }

    struct Hit
    {
        public float time;
        public float damage;
    }

    // BodyZone order: Head, Body (chest and arms), Legs, Stomach
    static readonly string[] ZoneNames = { "HEAD", "CHEST/ARMS", "LEGS", "STOMACH" };

    Health health;
    PlayerController player;
    readonly List<Popup> popups = new List<Popup>();
    readonly List<Hit> window = new List<Hit>();

    float lastDamage;
    bool lastHead;
    int lastZone = -1;
    readonly int[] zoneHits = new int[4];
    float totalDamage;
    int hits;
    int shots;

    GameObject model;
    List<Collider> hitboxes = new List<Collider>();
    int oldLayer;
    bool movedLayer;
    readonly List<RaycastHit> zoneRay = new List<RaycastHit>();

    GUIStyle panelStyle;
    GUIStyle popupStyle;

    void Awake()
    {
        health = GetComponent<Health>();
        health.team = team;
        health.invulnerable = true;
    }

    void OnEnable()
    {
        health.DamageTaken += OnDamage;
        Weapon.AnyShot += OnShot;
    }

    void OnDisable()
    {
        health.DamageTaken -= OnDamage;
        Weapon.AnyShot -= OnShot;
    }

    void Start()
    {
        player = FindAnyObjectByType<PlayerController>();
        BuildModel();
    }

    void OnDestroy()
    {
        if (movedLayer) gameObject.layer = oldLayer;
        if (model != null) Destroy(model);
    }

    // the static model on the floor at the bottom of the capsule, its hitboxes, the capsule hidden
    void BuildModel()
    {
        if (!CharacterBody.VisualsAllowed() || string.IsNullOrEmpty(modelPath)) return;
        // CT_Swat was the default before the agents: the scenes saved then (the desert) keep it in their field
        string path = modelPath == CharacterBody.CTFallback ? DefaultModel : modelPath;
        GameObject prefab = CharacterBody.LoadModel(path);
        if (prefab == null) return;

        model = CharacterBody.SpawnModel(prefab, transform);
        Transform m = model.transform;
        m.localPosition += Vector3.up * BottomY();

        Quaternion facing = transform.rotation;
        if (facePlayer && player != null)
        {
            Vector3 to = player.transform.position - transform.position;
            Vector3 fwd = transform.forward;
            to.y = 0f;
            fwd.y = 0f;
            if (to.sqrMagnitude > 0.01f && fwd.sqrMagnitude > 0.01f)
            {
                float yaw = Vector3.SignedAngle(fwd, to, Vector3.up);
                // the root turns (the model is its child), so the knife's backstab test (root forward) matches the model
                transform.rotation = transform.rotation * Quaternion.AngleAxis(yaw, Vector3.up);
                facing = transform.rotation;
            }
        }

        // the hitboxes are made from the rest pose, then the pose is set and they follow the bones
        hitboxes = CharacterBody.AddHitboxes(m, facing);
        Animation anim = model.GetComponent<Animation>();
        string state = CharacterBody.FindState(anim, poseClip);
        if (state == null) state = CharacterBody.FindState(anim, "Idle_Gun");
        if (anim != null && state != null) CharacterBody.SamplePose(anim, state, poseTime);
        if (anim != null) anim.enabled = false;
        // the idle clip stands with the hips turned 27 degrees: hips, chest and head face the root's forward
        // (the player), like the living models
        CharacterBody.StraightenPose(m, facing);

        foreach (SkinnedMeshRenderer r in model.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.updateWhenOffscreen = true;
        foreach (Renderer r in GetComponents<Renderer>()) r.forceRenderingOff = true;

        if (hitboxes.Count > 0)
        {
            oldLayer = gameObject.layer;
            gameObject.layer = CharacterBody.IgnoreRaycastLayer;
            movedLayer = true;
            foreach (Collider own in GetComponents<Collider>())
                foreach (Collider h in hitboxes)
                    if (h != null) Physics.IgnoreCollision(own, h, true);
        }
        Physics.SyncTransforms();
    }

    // the local height of the bottom of the capsule (the floor under the target)
    float BottomY()
    {
        CapsuleCollider cap = GetComponent<CapsuleCollider>();
        if (cap != null && cap.direction == 1) return cap.center.y - cap.height * 0.5f;
        Collider c = GetComponent<Collider>();
        if (c != null) return transform.InverseTransformPoint(c.bounds.min).y;
        return -1f;
    }

    void OnShot(ShotInfo s)
    {
        if (s.shooter == null || s.shooter.team == health.team) return;
        // a shotgun counts one shot per trigger pull, every pellet that hits counts for its body part
        if (!s.pellet) shots++;
        if (s.hit && s.target == health)
        {
            lastZone = ZoneOf(s);
            if (lastZone >= 0 && lastZone < zoneHits.Length) zoneHits[lastZone]++;
        }
    }

    // the body part the bullet hit: like Weapon, the first hitbox of the target along the shot;
    // the capsule without hitboxes by the height of the hit
    int ZoneOf(ShotInfo s)
    {
        if (s.headshot) return (int)BodyZone.Head;

        Vector3 dir = s.end - s.origin;
        float length = dir.magnitude;
        if (hitboxes.Count > 0 && length > 0.01f)
        {
            RaycastHit[] all = Physics.RaycastAll(s.origin, dir / length, length + 0.05f,
                                                  Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            zoneRay.Clear();
            zoneRay.AddRange(all);
            zoneRay.Sort((a, b) => a.distance.CompareTo(b.distance));
            foreach (RaycastHit h in zoneRay)
            {
                HitZone z = h.collider.GetComponent<HitZone>();
                if (z != null && h.collider.GetComponentInParent<Health>() == health) return (int)z.zone;
            }
        }

        Collider cap = GetComponent<Collider>();
        if (cap == null) return (int)BodyZone.Body;
        Bounds b = cap.bounds;
        float t = Mathf.InverseLerp(b.min.y, b.max.y, s.end.y);
        if (t >= 0.72f) return (int)BodyZone.Head;
        if (t <= 0.35f) return (int)BodyZone.Legs;
        if (t <= 0.52f) return (int)BodyZone.Stomach;
        return (int)BodyZone.Body;
    }

    void OnDamage(float amount, Health attacker, bool head)
    {
        lastDamage = amount;
        lastHead = head;
        totalDamage += amount;
        hits++;

        window.Add(new Hit { time = Time.time, damage = amount });

        popups.Add(new Popup
        {
            text = Mathf.RoundToInt(amount).ToString(),
            pos = transform.position + Vector3.up * 1.2f + Random.insideUnitSphere * 0.3f,
            born = Time.time,
            head = head
        });
    }

    void Update()
    {
        // not while the letter is typed into the developer console
        if (Input.GetKeyDown(resetKey) && !DevConsole.IsOpen) ResetStats();

        window.RemoveAll(h => Time.time - h.time > dpsWindow);
        popups.RemoveAll(p => Time.time - p.born > popupTime);
    }

    void ResetStats()
    {
        lastDamage = 0f;
        lastHead = false;
        lastZone = -1;
        for (int i = 0; i < zoneHits.Length; i++) zoneHits[i] = 0;
        totalDamage = 0f;
        hits = 0;
        shots = 0;
        window.Clear();
        popups.Clear();
    }

    void OnGUI()
    {
        if (panelStyle == null)
        {
            panelStyle = new GUIStyle(GUI.skin.label);
            panelStyle.fontSize = 20;
            panelStyle.normal.textColor = Color.white;

            popupStyle = new GUIStyle(GUI.skin.label);
            popupStyle.fontSize = 26;
            popupStyle.fontStyle = FontStyle.Bold;
            popupStyle.alignment = TextAnchor.MiddleCenter;
        }

        DrawPopups();
        DrawPanel();
    }

    void DrawPopups()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        foreach (Popup p in popups)
        {
            Vector3 sp = cam.WorldToScreenPoint(p.pos);
            if (sp.z <= 0f) continue;

            float age = (Time.time - p.born) / popupTime;
            float x = sp.x;
            float y = Screen.height - sp.y - age * 70f;

            Color c = p.head ? new Color(1f, 0.85f, 0.2f) : Color.white;
            c.a = 1f - age;

            popupStyle.normal.textColor = c;
            GUI.Label(new Rect(x - 60f, y - 20f, 120f, 40f), p.text, popupStyle);
        }
    }

    void DrawPanel()
    {
        float dps = 0f;
        foreach (Hit h in window) dps += h.damage;
        dps /= Mathf.Max(dpsWindow, 0.01f);

        string shotsToKill = lastDamage > 0f ? Mathf.CeilToInt(health.maxHealth / lastDamage).ToString() : "-";
        string accuracy = shots > 0 ? Mathf.RoundToInt(100f * hits / shots) + "%" : "-";
        string distance = player != null ? Vector3.Distance(player.transform.position, transform.position).ToString("F1") + " m" : "-";
        string zone = lastZone >= 0 && lastZone < ZoneNames.Length ? ZoneNames[lastZone] : (lastHead ? "HEAD" : "");
        string last = lastDamage > 0f ? Mathf.RoundToInt(lastDamage) + (zone.Length > 0 ? "  (" + zone + ")" : "") : "-";
        string zones = "Head " + zoneHits[(int)BodyZone.Head] + " / Chest " + zoneHits[(int)BodyZone.Body] +
                       " / Stomach " + zoneHits[(int)BodyZone.Stomach] + " / Legs " + zoneHits[(int)BodyZone.Legs];

        // ends above the radar, which moves down to y 282 while this panel is shown
        Rect box = new Rect(20f, 80f, 440f, 198f);
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = Color.white;

        string text =
            "TRAINING TARGET\n" +
            "Last hit: " + last + "\n" +
            "Shots to kill (100 HP): " + shotsToKill + "\n" +
            "Total damage: " + Mathf.RoundToInt(totalDamage) +
            "   (last " + dpsWindow.ToString("F0") + " s: " + Mathf.RoundToInt(dps) + ")\n" +
            "Hits / shots: " + hits + " / " + shots + "  (" + accuracy + ")\n" +
            "Hits: " + zones + "\n" +
            "Distance: " + distance + "\n" +
            "[" + resetKey + "] reset";

        GUI.Label(new Rect(box.x + 10f, box.y + 6f, box.width - 20f, box.height - 12f), text, panelStyle);
    }
}
