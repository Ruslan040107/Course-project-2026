using System.Collections.Generic;
using UnityEngine;

// All game sounds: shots (a far shot sounds duller), reloads, weapon draw, AWP bolt, knife,
// bullet hits (body, head, wall) and footsteps (concrete, wood, sand by the floor material).
// Clips are loaded from Resources/Audio (sources and licenses are in CREDITS.md).
// The player's own sounds are 2D, everybody else's are 3D at their position.
// Bots hear the player: his shots and his footsteps when he does not sneak or crouch.
// CombatEffects adds this component, so there is no sound (and no hearing) in simulations.
public class GameAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float masterVolume = 0.8f;
    public int voices = 32;

    [Header("Ranges (m)")]
    public float shotRange = 90f;
    public float silencedRange = 30f;
    public float footstepRange = 22f;
    public float hitRange = 30f;

    [Header("Footsteps")]
    [Tooltip("meters between two steps at full speed")] public float stepDistance = 2.2f;
    [Range(0f, 1f)] public float footstepVolume = 0.35f;

    [Header("Bots hear the player")]
    public float hearShot = 40f;
    public float hearSilencedShot = 10f;
    public float hearStep = 15f;

    class Voice
    {
        public AudioSource src;
        public AudioLowPassFilter lowPass;
    }

    struct Pending
    {
        public float at;
        public string clip;
        public Weapon weapon;
        public bool onlyWhileReloading;
        public float volume, pitch;
    }

    readonly List<Voice> pool = new List<Voice>();
    readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
    readonly Dictionary<Health, Vector3> lastPos = new Dictionary<Health, Vector3>();
    readonly Dictionary<Health, float> walked = new Dictionary<Health, float>();
    readonly Dictionary<Health, Weapon> weapons = new Dictionary<Health, Weapon>();
    readonly List<Pending> pending = new List<Pending>();
    int next;
    AudioListener listener;

    void Awake()
    {
        for (int i = 0; i < voices; i++)
        {
            GameObject go = new GameObject("Voice " + i);
            go.transform.SetParent(transform, false);
            AudioSource src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.dopplerLevel = 0f;
            AudioLowPassFilter lp = go.AddComponent<AudioLowPassFilter>();
            lp.cutoffFrequency = 22000f;
            pool.Add(new Voice { src = src, lowPass = lp });
        }

        Weapon.AnyShot += OnShot;
        Weapon.AnyReloadStarted += OnReload;
        Weapon.AnyEquipped += OnEquipped;
    }

    void OnDestroy()
    {
        Weapon.AnyShot -= OnShot;
        Weapon.AnyReloadStarted -= OnReload;
        Weapon.AnyEquipped -= OnEquipped;
    }

    // ---------------- playing ----------------

    // "Weapons/AK-47/shot" = every clip in Resources/Audio/Weapons/AK-47 whose name starts with "shot"
    AudioClip[] Get(string path)
    {
        if (clips.TryGetValue(path, out AudioClip[] set)) return set;

        int slash = path.LastIndexOf('/');
        string folder = "Audio/" + path.Substring(0, slash);
        string prefix = path.Substring(slash + 1);

        List<AudioClip> list = new List<AudioClip>();
        foreach (AudioClip c in Resources.LoadAll<AudioClip>(folder))
            if (c.name.StartsWith(prefix)) list.Add(c);
        list.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        set = list.ToArray();
        clips[path] = set;
        return set;
    }

    Transform ListenerTransform()
    {
        if (listener == null || !listener.isActiveAndEnabled) listener = FindAnyObjectByType<AudioListener>();
        return listener != null ? listener.transform : null;
    }

    // local = the player's own sound, played without position
    void Play(string path, Vector3 position, float volume, float range, bool local, float pitch = 1f)
    {
        AudioClip[] set = Get(path);
        if (set.Length == 0) return;

        Voice v = pool[next];
        next = (next + 1) % pool.Count;

        AudioSource src = v.src;
        src.Stop();
        src.clip = set[Random.Range(0, set.Length)];
        src.volume = volume * masterVolume;
        src.pitch = pitch * Random.Range(0.95f, 1.05f);

        if (local)
        {
            src.spatialBlend = 0f;
            v.lowPass.cutoffFrequency = 22000f;
        }
        else
        {
            src.transform.position = position;
            src.spatialBlend = 1f;
            src.minDistance = 1f;
            src.maxDistance = range;

            // far away the high frequencies are lost, the shot sounds dull
            Transform l = ListenerTransform();
            float d = l != null ? Vector3.Distance(l.position, position) : 0f;
            v.lowPass.cutoffFrequency = Mathf.Lerp(22000f, 1500f, Mathf.Clamp01(d / range));
        }

        src.Play();
    }

    // the player hears his own sounds without position, but not while he watches a teammate
    bool IsLocal(Health h)
    {
        if (h == null || h.IsDead || h.GetComponent<PlayerController>() == null) return false;
        Transform l = ListenerTransform();
        return l != null && l.IsChildOf(h.transform);
    }

    Weapon WeaponOf(Health h)
    {
        if (h == null) return null;
        if (!weapons.TryGetValue(h, out Weapon w) || w == null)
        {
            w = h.GetComponentInChildren<Weapon>(true);
            weapons[h] = w;
        }
        return w;
    }

    void Schedule(float delay, string clip, Weapon w, bool onlyWhileReloading, float volume, float pitch = 1f)
    {
        pending.Add(new Pending { at = Time.time + delay, clip = clip, weapon = w, onlyWhileReloading = onlyWhileReloading, volume = volume, pitch = pitch });
    }

    // ---------------- events ----------------

    void OnShot(ShotInfo s)
    {
        bool local = IsLocal(s.shooter);
        Vector3 from = s.muzzle != null ? s.muzzle.position : s.origin;
        Weapon w = WeaponOf(s.shooter);
        bool byPlayer = s.shooter != null && s.shooter.GetComponent<PlayerController>() != null;
        bool damaging = s.target != null && s.shooter != null && s.target.team != s.shooter.team;

        if (s.melee)
        {
            Play("Knife/swing", from, 0.6f, 15f, local);
            if (damaging) Play("Impacts/knife_body", s.end, 0.8f, hitRange, false);
            else if (s.hit && s.target == null) Play("Impacts/knife_wall", s.end, 0.5f, hitRange, false);
            return;
        }

        string weaponName = w != null && w.data != null ? w.data.weaponName : "AK-47";
        if (Get("Weapons/" + weaponName + "/shot").Length == 0) weaponName = "AK-47";
        bool silenced = weaponName == "USP-S";

        Play("Weapons/" + weaponName + "/shot", from, silenced ? 0.7f : 0.9f, silenced ? silencedRange : shotRange, local);

        // the AWP bolt: back and forward while the next round goes in
        if (w != null && w.data != null && w.data.category == WeaponCategory.Sniper)
        {
            float boltTime = 1f / Mathf.Max(w.fireRate, 0.01f);
            Schedule(boltTime * 0.3f, "Mech/bolt", w, false, 0.5f, 0.85f);
            Schedule(boltTime * 0.55f, "Mech/bolt", w, false, 0.5f, 1.15f);
        }

        if (damaging)
        {
            Play("Impacts/body", s.end, 0.7f, hitRange, false);
            if (s.headshot) Play("Impacts/head", s.end, 0.9f, hitRange, false, 1.5f);
        }
        else if (s.hit && s.target == null)
        {
            Play("Impacts/wall", s.end, 0.25f, 20f, false, 1.3f);
        }

        if (byPlayer) BotController.Noise(from, silenced ? hearSilencedShot : hearShot, s.shooter);
    }

    void OnReload(Weapon w)
    {
        if (w == null || w.IsMelee || w.Owner == null) return;

        float t = w.reloadTime;
        Schedule(t * 0.15f, "Mech/mag_out", w, true, 0.6f);
        Schedule(t * 0.6f, "Mech/mag_in", w, true, 0.7f);
        Schedule(t * 0.86f, "Mech/bolt", w, true, 0.55f);
    }

    void OnEquipped(Weapon w)
    {
        // only the player's weapon switch is heard, bots change weapons only at the round start
        if (w == null || !IsLocal(w.Owner)) return;
        Play("Mech/draw", Vector3.zero, 0.35f, 10f, true, w.IsMelee ? 1.4f : 1f);
    }

    // ---------------- every frame ----------------

    void Update()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            Pending p = pending[i];
            if (Time.time < p.at) continue;
            pending.RemoveAt(i);

            if (p.weapon == null || p.weapon.Owner == null || p.weapon.Owner.IsDead) continue;
            if (p.onlyWhileReloading && !p.weapon.IsReloading) continue;   // the reload was cancelled

            Health owner = p.weapon.Owner;
            Play(p.clip, owner.transform.position, p.volume, 20f, IsLocal(owner), p.pitch);
        }

        Footsteps();
    }

    void Footsteps()
    {
        foreach (Health h in Health.All)
        {
            if (h.IsDead || !h.gameObject.activeInHierarchy) continue;

            Vector3 p = h.transform.position;
            if (!lastPos.TryGetValue(h, out Vector3 before)) before = p;
            lastPos[h] = p;

            Vector3 delta = p - before;
            delta.y = 0f;
            float moved = delta.magnitude;
            if (moved > 3f) moved = 0f;   // respawn, not a step

            PlayerController pc = h.GetComponent<PlayerController>();
            bool grounded = pc != null ? pc.IsGrounded : Mathf.Abs(p.y - before.y) < 0.05f;
            bool silent = pc != null && pc.IsSilent;
            if (!grounded || h.GetComponent<TrainingTarget>() != null) continue;

            walked.TryGetValue(h, out float w);
            w += moved;
            if (w < stepDistance)
            {
                walked[h] = w;
                continue;
            }
            walked[h] = 0f;

            // sneaking and crouching are silent: no sound and the bots do not hear it
            if (silent) continue;

            Play("Footsteps/" + Surface(p), p, footstepVolume, footstepRange, IsLocal(h));
            if (pc != null) BotController.Noise(p, hearStep, h);
        }
    }

    // the floor under the feet: wood, sand or concrete, by the name of its material
    static string Surface(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 3f))
        {
            Renderer r = hit.collider.GetComponent<Renderer>();
            string n = r != null && r.sharedMaterial != null ? r.sharedMaterial.name.ToLowerInvariant() : "";
            if (n.Contains("wood") || n.Contains("plank")) return "wood";
            if (n.Contains("ground") || n.Contains("sand")) return "sand";
        }
        return "concrete";
    }
}
