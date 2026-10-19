using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// All game sounds: shots (a far shot sounds duller), reloads, weapon draw, AWP bolt, knife,
// bullet hits (body, head, wall), footsteps (concrete, wood, sand by the floor material), jumps and landings.
// Clips are loaded from Resources/Audio (sources and licenses are in CREDITS.md).
// The player's own sounds are 2D, everybody else's are 3D at their position.
// The player's reload, bolt and draw sounds come from his first person animations (ViewModel.AnimationSound),
// the bots' ones are timed by the reload time.
// Bots hear the player: his shots, his footsteps when he does not sneak or crouch, and his landings.
// A sneaking bot (BotController.IsSneaking) makes no footsteps either; a landing is always heard.
// CombatEffects adds this component, so there is no sound (and no hearing) in simulations.
public class GameAudio : MonoBehaviour
{
    [Range(0f, 1f)] public float masterVolume = 0.8f;
    public int voices = 32;

    [Header("Ranges (m)")]
    public float shotRange = 90f;
    public float silencedRange = 30f;
    public float footstepRange = 28f;
    public float hitRange = 30f;

    [Header("Footsteps")]
    [Tooltip("meters between two steps at full speed")] public float stepDistance = 1.8f;
    [Range(0f, 1f)] public float footstepVolume = 0.9f;

    [Header("Jumps")]
    [Tooltip("a bot is in the air when it is this much above the ground")] public float hopUp = 0.15f;
    [Tooltip("shorter falls make no landing sound")] public float minAirTime = 0.2f;

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

    // what we remember about everybody who walks: steps, jumps and landings
    class Mover
    {
        public PlayerController pc;
        public BotController bot;
        public NavMeshAgent agent;
        public bool target;
        public bool hasPos;
        public Vector3 lastPos;
        public float walked;
        public bool air;
        public float airSince;
        public float quietUntil;
        public bool hasRest;
        public float restOffset;
    }

    readonly List<Voice> pool = new List<Voice>();
    readonly Dictionary<string, AudioClip[]> clips = new Dictionary<string, AudioClip[]>();
    readonly Dictionary<Health, Mover> movers = new Dictionary<Health, Mover>();
    readonly Dictionary<Health, Weapon> weapons = new Dictionary<Health, Weapon>();
    readonly List<Pending> pending = new List<Pending>();
    int next;
    AudioListener listener;

    // steps, jumps and landings fade slower than the linear rolloff: still heard at half the range
    AnimationCurve softRolloff;

    void Awake()
    {
        softRolloff = new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(0.1f, 1f), new Keyframe(0.35f, 0.6f),
            new Keyframe(0.65f, 0.28f), new Keyframe(1f, 0f));
        for (int i = 0; i < softRolloff.length; i++) softRolloff.SmoothTangents(i, 0f);

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
        ViewModel.AnimationSound += OnAnimationSound;
    }

    void OnDestroy()
    {
        Weapon.AnyShot -= OnShot;
        Weapon.AnyReloadStarted -= OnReload;
        Weapon.AnyEquipped -= OnEquipped;
        ViewModel.AnimationSound -= OnAnimationSound;
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

    // local = the player's own sound, played without position; soft = the slower rolloff of steps
    void Play(string path, Vector3 position, float volume, float range, bool local, float pitch = 1f, bool soft = false)
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
            if (soft)
            {
                src.rolloffMode = AudioRolloffMode.Custom;
                src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, softRolloff);
            }
            else src.rolloffMode = AudioRolloffMode.Linear;

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

    // a reload part of the weapon ("mag_out", "mag_in", "bolt_back", "bolt_forward", "slide_back", "slide_forward"):
    // Reload/<weapon name>/<key> (a bolt key takes the slide clip and back, if the weapon has only the other one),
    // or the old generic clip when the weapon has no own set
    string ReloadClip(WeaponData d, string key, out float pitch)
    {
        pitch = 1f;
        if (d != null)
        {
            string own = "Reload/" + d.weaponName + "/" + key;
            if (Get(own).Length > 0) return own;

            string other = key.StartsWith("bolt") ? key.Replace("bolt", "slide") : key.Replace("slide", "bolt");
            own = "Reload/" + d.weaponName + "/" + other;
            if (other != key && Get(own).Length > 0) return own;
        }

        if (key == "mag_out") return "Mech/mag_out";
        if (key == "mag_in") return "Mech/mag_in";
        pitch = key.EndsWith("_back") ? 0.85f : 1.15f;
        return "Mech/bolt";
    }

    static float ReloadVolume(string key)
    {
        if (key == "mag_out") return 0.55f;
        if (key.EndsWith("_back")) return 0.6f;
        return 0.7f;   // mag_in and the bolt or slide going forward are the loud ones
    }

    void ScheduleReload(float delay, Weapon w, string key, bool onlyWhileReloading, float volume)
    {
        string clip = ReloadClip(w.data, key, out float pitch);
        Schedule(delay, clip, w, onlyWhileReloading, volume, pitch);
    }

    // ---------------- events ----------------

    void OnShot(ShotInfo s)
    {
        bool local = IsLocal(s.shooter);
        Vector3 from = s.muzzle != null ? s.muzzle.position : s.origin;
        Weapon w = WeaponOf(s.shooter);
        bool byPlayer = s.shooter != null && s.shooter.GetComponent<PlayerController>() != null;
        // a hit body sounds like a body, a teammate's too while friendly fire is on
        bool damaging = s.target != null && s.shooter != null && s.target != s.shooter &&
                        (s.target.team != s.shooter.team || Weapon.FriendlyFire);

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

        // the AWP bolt of a bot: back and forward while the next round goes in
        // (the player's bolt sounds come from his first person animation)
        if (!byPlayer && w != null && w.data != null && w.data.category == WeaponCategory.Sniper)
        {
            float boltTime = 1f / Mathf.Max(w.fireRate, 0.01f);
            ScheduleReload(boltTime * 0.3f, w, "bolt_back", false, 0.5f);
            ScheduleReload(boltTime * 0.55f, w, "bolt_forward", false, 0.55f);
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

    // bots only: the player's reload is heard from his first person animation
    void OnReload(Weapon w)
    {
        if (w == null || w.IsMelee || w.Owner == null) return;
        if (w.Owner.GetComponent<PlayerController>() != null) return;

        bool pistol = w.data != null && w.data.category == WeaponCategory.Pistol;
        string action = pistol ? "slide" : "bolt";
        float t = w.reloadTime;
        ScheduleReload(t * 0.18f, w, "mag_out", true, 0.6f);
        ScheduleReload(t * 0.6f, w, "mag_in", true, 0.7f);
        ScheduleReload(t * 0.82f, w, action + "_back", true, 0.55f);
        ScheduleReload(t * 0.9f, w, action + "_forward", true, 0.65f);
    }

    void OnEquipped(Weapon w)
    {
        // only the player's weapon switch is heard, bots change weapons only at the round start;
        // a short quiet click, the bolt or slide of the draw animation comes from ViewModel
        if (w == null || !IsLocal(w.Owner)) return;
        Play("Mech/draw", Vector3.zero, 0.2f, 10f, true, w.IsMelee ? 1.4f : 1f);
    }

    // a sound of the player's first person animation (draw, reload, AWP bolt), always his own, so 2D
    void OnAnimationSound(string key, WeaponData weapon)
    {
        if (key == "knife_flip")
        {
            if (Get("Knife/flip").Length > 0) Play("Knife/flip", Vector3.zero, 0.45f, 10f, true);
            else Play("Mech/draw", Vector3.zero, 0.3f, 10f, true, 1.4f);
            return;
        }

        string clip = ReloadClip(weapon, key, out float pitch);
        Play(clip, Vector3.zero, ReloadVolume(key), 10f, true, pitch);
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

        Movement();
    }

    Mover MoverOf(Health h)
    {
        if (!movers.TryGetValue(h, out Mover m))
        {
            m = new Mover
            {
                pc = h.GetComponent<PlayerController>(),
                bot = h.GetComponent<BotController>(),
                agent = h.GetComponent<NavMeshAgent>(),
                target = h.GetComponent<TrainingTarget>() != null,
                quietUntil = Time.time + 0.5f,
            };
            movers[h] = m;
        }
        return m;
    }

    // the player is in the air when his controller is not grounded;
    // a bot hops by raising its NavMeshAgent.baseOffset, so it is in the air when it is above its usual offset
    bool InAir(Mover m)
    {
        if (m.pc != null) return !m.pc.IsGrounded;

        float offset = m.agent.baseOffset;
        if (!m.hasRest || offset < m.restOffset)
        {
            m.restOffset = offset;
            m.hasRest = true;
        }
        float height = offset - m.restOffset;
        if (height > hopUp) return true;
        if (height < 0.05f) return false;
        return m.air;
    }

    void Movement()
    {
        foreach (Health h in Health.All)
        {
            Mover m = MoverOf(h);
            if (h.IsDead || !h.gameObject.activeInHierarchy || m.target)
            {
                // a respawn is not a landing
                m.hasPos = false;
                m.air = false;
                m.quietUntil = Time.time + 0.5f;
                continue;
            }

            Vector3 p = h.transform.position;
            Vector3 before = m.hasPos ? m.lastPos : p;
            m.lastPos = p;
            m.hasPos = true;

            Vector3 delta = p - before;
            float rise = delta.y;
            delta.y = 0f;
            float moved = delta.magnitude;
            if (moved > 3f)   // respawn, not a step
            {
                moved = 0f;
                m.quietUntil = Time.time + 0.5f;
            }

            bool canJump = m.pc != null || m.agent != null;
            bool inAir = canJump ? InAir(m) : Mathf.Abs(rise) >= 0.05f;

            if (canJump && inAir && !m.air)
            {
                // left the ground: a jump when going up fast (the player can also walk off a ledge,
                // or leave the ground for a moment at the top of a ramp, that is slower than half the jump speed)
                m.air = true;
                m.airSince = Time.time;
                bool up = m.pc == null || rise > 0.5f * JumpSpeed(m.pc) * Time.deltaTime;
                if (up && Time.time >= m.quietUntil) Jump(p, IsLocal(h));
            }
            else if (canJump && !inAir && m.air)
            {
                m.air = false;
                if (Time.time - m.airSince >= minAirTime && Time.time >= m.quietUntil)
                {
                    m.walked = 0f;   // the landing is the step
                    Land(p, IsLocal(h));
                    // the landing is heard even when the player sneaks
                    if (m.pc != null) BotController.Noise(p, hearStep, h);
                }
            }

            if (inAir) continue;

            m.walked += moved;
            if (m.walked < stepDistance) continue;
            m.walked = 0f;

            // sneaking and crouching are silent: no sound and the bots do not hear it;
            // a sneaking bot is silent too
            if (m.pc != null && m.pc.IsSilent) continue;
            if (m.bot != null && m.bot.IsSneaking) continue;

            Play("Footsteps/" + Surface(p), p, footstepVolume, footstepRange, IsLocal(h), 1f, true);
            if (m.pc != null) BotController.Noise(p, hearStep, h);
        }
    }

    // the speed the player leaves the ground with when he jumps
    static float JumpSpeed(PlayerController pc)
    {
        return Mathf.Sqrt(Mathf.Max(0f, pc.jumpHeight * -2f * pc.gravity));
    }

    // the push off: a soft scuff and a quiet step on the floor
    void Jump(Vector3 p, bool local)
    {
        Play("Movement/jump", p, 0.7f * footstepVolume, footstepRange, local, 1f, true);
        Play("Footsteps/" + Surface(p), p, 0.5f * footstepVolume, footstepRange, local, 1.1f, true);
    }

    // the landing: a thud of the body and a loud step on the floor
    void Land(Vector3 p, bool local)
    {
        Play("Movement/land", p, footstepVolume, footstepRange, local, 1f, true);
        Play("Footsteps/" + Surface(p), p, footstepVolume, footstepRange, local, 0.9f, true);
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
