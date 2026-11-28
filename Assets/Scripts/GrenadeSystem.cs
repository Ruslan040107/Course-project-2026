using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Headless-safe grenade simulation and gameplay logic (HE, Flash, Smoke, Molotov/Incendiary, Decoy).
// Contains NO rendering, particles, lights, GUI, or AudioSource logic so it works cleanly in -batchmode -nographics -sim.
// GrenadeVisuals and GameAudio subscribe to the static events below when graphics/audio are active.
public class GrenadeSystem : MonoBehaviour
{
    public const int IgnoreRaycastLayer = 2;
    public const int LocalBodyLayer = 9;
    public const int WorldMask = Physics.DefaultRaycastLayers & ~(1 << IgnoreRaycastLayer) & ~(1 << LocalBodyLayer);

    // ---------------- data registry ----------------

    static GrenadeData[] allData;

    public static GrenadeData[] AllData
    {
        get
        {
            if (allData == null)
            {
                allData = Resources.LoadAll<GrenadeData>("Grenades");
                System.Array.Sort(allData, (a, b) =>
                {
                    int c = a.price.CompareTo(b.price);
                    return c != 0 ? c : string.CompareOrdinal(a.grenadeName, b.grenadeName);
                });
            }
            return allData;
        }
    }

    public static GrenadeData Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (GrenadeData g in AllData)
            if (g != null && string.Equals(g.grenadeName, name, System.StringComparison.OrdinalIgnoreCase)) return g;
        foreach (GrenadeData g in AllData)
            if (g != null && string.Equals(g.name, name, System.StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    public static GrenadeData FindByType(GrenadeType type, int team = -1)
    {
        foreach (GrenadeData g in AllData)
            if (g != null && g.type == type && (g.team < 0 || g.team == team)) return g;
        foreach (GrenadeData g in AllData)
            if (g != null && g.type == type) return g;
        return null;
    }

    public static bool TeamCanBuy(GrenadeData g, int team)
    {
        return g != null && (g.team < 0 || g.team == team);
    }

    // ---------------- active states & events ----------------

    public class SmokeCloud
    {
        public int id;
        public Vector3 center;
        public float maxRadius;
        public float bornAt;
        public float duration;
        public float bloomTime = 0.75f;
        public float fadeTime = 1.5f;

        public float Age => Time.time - bornAt;
        public float Remaining => Mathf.Max(0f, bornAt + duration - Time.time);

        public float CurrentRadius
        {
            get
            {
                float age = Age;
                if (age <= 0f || age >= duration) return 0f;
                float grow = Mathf.Clamp01(age / Mathf.Max(0.05f, bloomTime));
                grow = grow * grow * (3f - 2f * grow);
                float shrink = Remaining < fadeTime ? Mathf.Clamp01(Remaining / Mathf.Max(0.05f, fadeTime)) : 1f;
                return Mathf.Lerp(0.4f, maxRadius, grow) * shrink;
            }
        }
    }

    public class FireNode
    {
        public Vector3 position;
        public float radius;
        public float bornAt;
        public int depth;
    }

    public class FireZone
    {
        public int id;
        public Health owner;
        public GrenadeData data;
        public Vector3 origin;
        public float bornAt;
        public float duration;
        public float nextSpreadAt;
        public int maxNodes = 26;
        public readonly List<FireNode> nodes = new List<FireNode>();
        public readonly Queue<FireNode> frontier = new Queue<FireNode>();
        public readonly Dictionary<Health, float> nextTick = new Dictionary<Health, float>();
    }

    class DecoyEmitter
    {
        public int id;
        public Health owner;
        public GrenadeData data;
        public WeaponData mimicWeapon;
        public Vector3 position;
        public float bornAt;
        public float duration;
        public float nextShotAt;
        public int burstLeft;
    }

    struct BlindState
    {
        public float fullUntil;
        public float fadeUntil;
        public float peak;
    }

    public static event System.Action<Health, GrenadeData> AnyPinPulled;
    public static event System.Action<GrenadeProjectile> AnyThrown;
    public static event System.Action<Vector3, float, GrenadeData> AnyBounced;
    public static event System.Action<Vector3, float, GrenadeData> AnyHEExploded;
    public static event System.Action<Vector3, GrenadeData, Health, float> AnyFlashExploded;
    public static event System.Action<SmokeCloud> AnySmokeStarted;
    public static event System.Action<int> AnySmokeEnded;
    public static event System.Action<FireZone> AnyFireStarted;
    public static event System.Action<FireZone> AnyFireNodesChanged;
    public static event System.Action<Vector3> AnyFireExtinguished;
    public static event System.Action<int> AnyFireEnded;
    public static event System.Action<Vector3, WeaponData, Health> AnyDecoyShot;
    public static event System.Action<Vector3> AnyDecoyEnded;
    public static event System.Action AnyCleared;

    static GrenadeSystem instance;
    static int nextEntityId = 1;

    readonly List<GrenadeProjectile> projectiles = new List<GrenadeProjectile>();
    readonly List<SmokeCloud> smokes = new List<SmokeCloud>();
    readonly List<FireZone> fires = new List<FireZone>();
    readonly List<DecoyEmitter> decoys = new List<DecoyEmitter>();
    readonly Dictionary<Health, BlindState> blinds = new Dictionary<Health, BlindState>();
    readonly RaycastHit[] rayHits = new RaycastHit[32];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        allData = null;
        nextEntityId = 1;
        AnyPinPulled = null;
        AnyThrown = null;
        AnyBounced = null;
        AnyHEExploded = null;
        AnyFlashExploded = null;
        AnySmokeStarted = null;
        AnySmokeEnded = null;
        AnyFireStarted = null;
        AnyFireNodesChanged = null;
        AnyFireExtinguished = null;
        AnyFireEnded = null;
        AnyDecoyShot = null;
        AnyDecoyEnded = null;
        AnyCleared = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        Ensure();
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (instance != null) instance.ClearAll();
        Ensure();
    }

    public static GrenadeSystem Ensure()
    {
        if (instance != null) return instance;
        instance = FindAnyObjectByType<GrenadeSystem>();
        if (instance != null) return instance;

        GameObject go = new GameObject("GrenadeSystem");
        instance = go.AddComponent<GrenadeSystem>();
        return instance;
    }

    void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        RoundManager.RoundStarted += ClearAll;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        RoundManager.RoundStarted -= ClearAll;
    }

    // ---------------- cleanup ----------------

    public static void ClearRoundObjects()
    {
        if (instance != null) instance.ClearAll();
    }

    public void ClearAll()
    {
        for (int i = projectiles.Count - 1; i >= 0; i--)
            if (projectiles[i] != null) Destroy(projectiles[i].gameObject);
        projectiles.Clear();

        for (int i = 0; i < smokes.Count; i++) AnySmokeEnded?.Invoke(smokes[i].id);
        smokes.Clear();

        for (int i = 0; i < fires.Count; i++) AnyFireEnded?.Invoke(fires[i].id);
        fires.Clear();

        decoys.Clear();
        blinds.Clear();
        AnyCleared?.Invoke();
    }

    // ---------------- throwing & notifications ----------------

    public static void NotifyPinPulled(Health thrower, GrenadeData data)
    {
        if (data != null) AnyPinPulled?.Invoke(thrower, data);
    }

    public static void NotifyBounce(Vector3 pos, float speed, GrenadeData data)
    {
        AnyBounced?.Invoke(pos, speed, data);
    }

    public static GrenadeProjectile SpawnThrow(Health owner, GrenadeData data, Vector3 origin, Vector3 velocity, WeaponData mimicWeapon = null)
    {
        if (data == null) return null;
        GrenadeSystem sys = Ensure();

        GameObject go = new GameObject("Projectile_" + data.grenadeName);
        go.layer = IgnoreRaycastLayer;
        go.transform.position = origin;
        go.transform.rotation = Quaternion.LookRotation(velocity.sqrMagnitude > 0.01f ? velocity.normalized : Vector3.forward);

        GrenadeProjectile proj = go.AddComponent<GrenadeProjectile>();
        proj.Init(sys, owner, data, velocity, mimicWeapon);
        sys.projectiles.Add(proj);
        AnyThrown?.Invoke(proj);
        return proj;
    }

    public void UnregisterProjectile(GrenadeProjectile p)
    {
        projectiles.Remove(p);
    }

    // ---------------- detonation entry ----------------

    public void Detonate(GrenadeProjectile proj, Vector3 pos, Vector3 surfaceNormal)
    {
        if (proj == null || proj.Data == null) return;
        GrenadeData data = proj.Data;
        Health owner = proj.Owner;

        switch (data.type)
        {
            case GrenadeType.HE:
                DetonateHE(pos, data, owner);
                break;
            case GrenadeType.Flash:
                DetonateFlash(pos, data, owner);
                break;
            case GrenadeType.Smoke:
                StartSmoke(pos, data);
                break;
            case GrenadeType.Fire:
                StartFire(pos, surfaceNormal, data, owner);
                break;
            case GrenadeType.Decoy:
                StartDecoy(pos, data, owner, proj.MimicWeapon);
                break;
        }
    }

    // ---------------- HE ----------------

    void DetonateHE(Vector3 pos, GrenadeData data, Health owner)
    {
        float R = Mathf.Max(0.1f, data.radius);
        float maxD = Mathf.Max(0f, data.damage);
        Vector3 rayOrigin = pos + Vector3.up * 0.18f;

        foreach (Health h in Health.All)
        {
            if (h == null || h.IsDead || !h.gameObject.activeInHierarchy) continue;

            // Friendly fire check (self-damage is always allowed, teammates follow Weapon.FriendlyFire)
            if (owner != null && h != owner && h.team == owner.team && !Weapon.FriendlyFire) continue;

            Vector3 targetCenter = TargetCenter(h);
            float d = Vector3.Distance(pos, targetCenter);
            if (d >= R) continue;

            if (!HasBlastLineOfSight(rayOrigin, h, targetCenter)) continue;

            // Required formula: D = Dmax * (1 - d / R)
            float dmg = maxD * (1f - d / R);
            if (owner != null && h != owner && h.team == owner.team)
                dmg *= Weapon.FriendlyFireScale;

            if (dmg > 0.1f)
                h.TakeDamage(dmg, owner, headshot: false, noScope: false, inAir: false, wallbang: false, weapon: null, grenade: data);
        }

        AnyHEExploded?.Invoke(pos, R, data);
    }

    bool HasBlastLineOfSight(Vector3 origin, Health target, Vector3 center)
    {
        Vector3 head = center + Vector3.up * 0.55f;
        Vector3 low = center - Vector3.up * 0.45f;
        return WorldLineClear(origin, center, target)
            || WorldLineClear(origin, head, target)
            || WorldLineClear(origin, low, target);
    }

    bool WorldLineClear(Vector3 from, Vector3 to, Health ignoreTarget)
    {
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.05f) return true;
        dir /= dist;

        int n = Physics.RaycastNonAlloc(from, dir, rayHits, dist, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = rayHits[i].collider;
            if (c == null) continue;
            if (c.GetComponentInParent<Health>() != null) continue;
            if (c.GetComponentInParent<GrenadeProjectile>() != null) continue;
            return false;
        }
        return true;
    }

    static Vector3 TargetCenter(Health h)
    {
        Collider col = h.GetComponent<Collider>();
        return col != null ? col.bounds.center : h.transform.position + Vector3.up * 0.9f;
    }

    // ---------------- Flashbang ----------------

    void DetonateFlash(Vector3 pos, GrenadeData data, Health owner)
    {
        float R = Mathf.Max(0.5f, data.radius);
        float maxDur = Mathf.Max(0.5f, data.duration);
        Vector3 flashPos = pos + Vector3.up * 0.15f;

        Health localPlayer = null;
        float localStrength = 0f;

        foreach (Health h in Health.All)
        {
            if (h == null || h.IsDead || !h.gameObject.activeInHierarchy) continue;

            GetEyePose(h, out Vector3 eye, out Vector3 lookDir);
            float d = Vector3.Distance(flashPos, eye);
            if (d >= R) continue;

            if (IsLineBlockedBySmoke(flashPos, eye)) continue;
            if (!WorldLineClear(flashPos, eye, h)) continue;

            Vector3 toFlash = (flashPos - eye).normalized;
            float angle = Vector3.Angle(lookDir, toFlash);

            // Facing the flash gives full blind; looking away reduces duration and intensity
            float angleFactor;
            if (angle <= 55f) angleFactor = 1f;
            else if (angle <= 100f) angleFactor = Mathf.Lerp(1f, 0.35f, (angle - 55f) / 45f);
            else angleFactor = Mathf.Lerp(0.35f, 0.18f, (angle - 100f) / 80f);

            float distFactor = Mathf.Clamp01(1f - (d / R) * 0.7f);
            float strength = Mathf.Clamp01(angleFactor * distFactor);
            if (strength < 0.05f) continue;

            float fullTime = maxDur * 0.45f * strength;
            float fadeTime = maxDur * 0.55f * Mathf.Lerp(0.4f, 1f, strength);
            float now = Time.time;

            blinds.TryGetValue(h, out BlindState cur);
            cur.fullUntil = Mathf.Max(cur.fullUntil, now + fullTime);
            cur.fadeUntil = Mathf.Max(cur.fadeUntil, now + fullTime + fadeTime);
            cur.peak = Mathf.Max(cur.peak, strength);
            blinds[h] = cur;

            if (h.GetComponent<PlayerController>() != null)
            {
                localPlayer = h;
                localStrength = strength;
            }
        }

        AnyFlashExploded?.Invoke(pos, data, localPlayer, localStrength);
    }

    static void GetEyePose(Health h, out Vector3 eye, out Vector3 lookDir)
    {
        PlayerController pc = h.GetComponent<PlayerController>();
        if (pc != null)
        {
            eye = pc.cameraTransform != null ? pc.cameraTransform.position : h.transform.position + Vector3.up * 0.6f;
            lookDir = pc.AimRotation * Vector3.forward;
            return;
        }
        BotController bot = h.GetComponent<BotController>();
        if (bot != null && bot.head != null)
        {
            eye = bot.head.position;
            lookDir = bot.head.forward;
            return;
        }
        eye = h.transform.position + Vector3.up * 0.6f;
        lookDir = h.transform.forward;
    }

    // 0 = clear, 1 = completely white/blinded
    public static float GetBlindness(Health h)
    {
        if (instance == null || h == null) return 0f;
        if (!instance.blinds.TryGetValue(h, out BlindState b)) return 0f;

        float now = Time.time;
        if (now >= b.fadeUntil)
        {
            instance.blinds.Remove(h);
            return 0f;
        }
        if (now <= b.fullUntil) return b.peak;
        float t = 1f - Mathf.Clamp01((now - b.fullUntil) / Mathf.Max(0.05f, b.fadeUntil - b.fullUntil));
        return b.peak * t;
    }

    // ---------------- Smoke ----------------

    void StartSmoke(Vector3 pos, GrenadeData data)
    {
        Vector3 center = pos + Vector3.up * 0.8f;
        if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 2f, WorldMask, QueryTriggerInteraction.Ignore))
            center = hit.point + Vector3.up * 1.1f;

        SmokeCloud cloud = new SmokeCloud
        {
            id = nextEntityId++,
            center = center,
            maxRadius = Mathf.Max(1.5f, data.radius),
            bornAt = Time.time,
            duration = Mathf.Max(2f, data.duration),
        };
        smokes.Add(cloud);
        ExtinguishFireInSphere(center, cloud.maxRadius + 0.6f);
        AnySmokeStarted?.Invoke(cloud);
    }

    public static bool IsLineBlockedBySmoke(Vector3 a, Vector3 b)
    {
        if (instance == null || instance.smokes.Count == 0) return false;
        float now = Time.time;
        for (int i = 0; i < instance.smokes.Count; i++)
        {
            SmokeCloud c = instance.smokes[i];
            if (now >= c.bornAt + c.duration) continue;
            float r = c.CurrentRadius * 0.92f;
            if (r <= 0.1f) continue;
            if (SegmentSphereHit(a, b, c.center, r)) return true;
        }
        return false;
    }

    public static float GetSmokeDensityAt(Vector3 point)
    {
        if (instance == null || instance.smokes.Count == 0) return 0f;
        float best = 0f;
        float now = Time.time;
        for (int i = 0; i < instance.smokes.Count; i++)
        {
            SmokeCloud c = instance.smokes[i];
            if (now >= c.bornAt + c.duration) continue;
            float r = c.CurrentRadius;
            if (r <= 0.1f) continue;
            float d = Vector3.Distance(point, c.center);
            if (d < r)
            {
                float density = Mathf.Clamp01(1f - (d - r * 0.45f) / Mathf.Max(0.1f, r * 0.55f));
                if (density > best) best = density;
            }
        }
        return best;
    }

    public static bool IsPointInActiveFire(Vector3 point)
    {
        if (instance == null || instance.fires.Count == 0) return false;
        for (int i = 0; i < instance.fires.Count; i++)
        {
            FireZone f = instance.fires[i];
            for (int j = 0; j < f.nodes.Count; j++)
            {
                FireNode n = f.nodes[j];
                Vector2 diff = new Vector2(point.x - n.position.x, point.z - n.position.z);
                if (diff.sqrMagnitude <= (n.radius + 0.5f) * (n.radius + 0.5f) && Mathf.Abs(point.y - n.position.y) <= 1.8f)
                    return true;
            }
        }
        return false;
    }

    static bool SegmentSphereHit(Vector3 a, Vector3 b, Vector3 c, float r)
    {
        Vector3 ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-6f) return (a - c).sqrMagnitude <= r * r;
        float t = Mathf.Clamp01(Vector3.Dot(c - a, ab) / len2);
        Vector3 closest = a + ab * t;
        return (closest - c).sqrMagnitude <= r * r;
    }

    // ---------------- Fire (Molotov / Incendiary) ----------------

    void StartFire(Vector3 pos, Vector3 normal, GrenadeData data, Health owner)
    {
        // Snap origin cleanly to the floor
        Vector3 origin = pos;
        if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 2.5f, WorldMask, QueryTriggerInteraction.Ignore))
            origin = hit.point;

        // If thrown straight into an active smoke, extinguish immediately without burning
        if (GetSmokeDensityAt(origin + Vector3.up * 0.4f) > 0.15f)
        {
            AnyFireExtinguished?.Invoke(origin);
            return;
        }

        FireZone zone = new FireZone
        {
            id = nextEntityId++,
            owner = owner,
            data = data,
            origin = origin,
            bornAt = Time.time,
            duration = Mathf.Max(1f, data.duration),
            nextSpreadAt = Time.time + 0.1f,
        };

        FireNode root = new FireNode { position = origin, radius = 0.85f, bornAt = Time.time, depth = 0 };
        zone.nodes.Add(root);
        zone.frontier.Enqueue(root);
        fires.Add(zone);
        AnyFireStarted?.Invoke(zone);
    }

    void StepFires()
    {
        float now = Time.time;
        for (int i = fires.Count - 1; i >= 0; i--)
        {
            FireZone f = fires[i];
            if (now >= f.bornAt + f.duration || f.nodes.Count == 0)
            {
                fires.RemoveAt(i);
                AnyFireEnded?.Invoke(f.id);
                continue;
            }

            // BFS floor spread constrained by walls and active smokes
            if (f.frontier.Count > 0 && f.nodes.Count < f.maxNodes && now >= f.nextSpreadAt && now - f.bornAt < 1.5f)
            {
                f.nextSpreadAt = now + 0.11f;
                ExpandFire(f, now);
            }

            // Damage every second to players standing inside any node
            float dps = f.data != null ? f.data.damage : 40f;
            foreach (Health h in Health.All)
            {
                if (h == null || h.IsDead || !h.gameObject.activeInHierarchy) continue;
                if (f.owner != null && h != f.owner && h.team == f.owner.team && !Weapon.FriendlyFire) continue;

                if (!InsideFire(h.transform.position, f))
                {
                    f.nextTick.Remove(h);
                    continue;
                }

                if (!f.nextTick.TryGetValue(h, out float tickAt))
                {
                    // Short grace before first burn tick, then once every 1.0 s as requested
                    f.nextTick[h] = now + 0.25f;
                    continue;
                }

                if (now >= tickAt)
                {
                    f.nextTick[h] = now + 1.0f;
                    float dmg = dps;
                    if (f.owner != null && h != f.owner && h.team == f.owner.team)
                        dmg *= Weapon.FriendlyFireScale;

                    h.TakeDamage(dmg, f.owner, headshot: false, noScope: false, inAir: false, wallbang: false, weapon: null, grenade: f.data);
                }
            }
        }
    }

    void ExpandFire(FireZone f, float now)
    {
        const float step = 0.92f;
        float maxDist = Mathf.Max(1.5f, f.data != null ? f.data.radius : 3.6f);
        int expandBatch = Mathf.Min(4, f.frontier.Count);
        bool added = false;

        for (int b = 0; b < expandBatch; b++)
        {
            FireNode parent = f.frontier.Dequeue();
            float baseYaw = ((f.id * 53 + parent.depth * 29) % 360) * Mathf.Deg2Rad;

            for (int k = 0; k < 6; k++)
            {
                if (f.nodes.Count >= f.maxNodes) break;
                float ang = baseYaw + k * (Mathf.PI / 3f);
                Vector3 candidate = parent.position + new Vector3(Mathf.Cos(ang) * step, 0f, Mathf.Sin(ang) * step);

                if (Vector2.Distance(new Vector2(candidate.x, candidate.z), new Vector2(f.origin.x, f.origin.z)) > maxDist)
                    continue;

                // Skip if too close to an existing node
                bool tooClose = false;
                for (int n = 0; n < f.nodes.Count; n++)
                {
                    Vector3 np = f.nodes[n].position;
                    if ((np.x - candidate.x) * (np.x - candidate.x) + (np.z - candidate.z) * (np.z - candidate.z) < 0.65f * 0.65f
                        && Mathf.Abs(np.y - candidate.y) < 0.8f)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;

                // Wall check between parent and candidate
                Vector3 from = parent.position + Vector3.up * 0.35f;
                Vector3 to = candidate + Vector3.up * 0.35f;
                if (!WorldLineClear(from, to, null)) continue;

                // Floor check under candidate
                if (!Physics.Raycast(candidate + Vector3.up * 0.85f, Vector3.down, out RaycastHit floor, 1.6f, WorldMask, QueryTriggerInteraction.Ignore))
                    continue;
                if (floor.normal.y < 0.72f || Mathf.Abs(floor.point.y - parent.position.y) > 0.65f)
                    continue;

                // Smoke blocks fire spread
                if (GetSmokeDensityAt(floor.point + Vector3.up * 0.4f) > 0.15f)
                    continue;

                FireNode node = new FireNode
                {
                    position = floor.point,
                    radius = 0.85f,
                    bornAt = now,
                    depth = parent.depth + 1
                };
                f.nodes.Add(node);
                f.frontier.Enqueue(node);
                added = true;
            }
        }

        if (added) AnyFireNodesChanged?.Invoke(f);
    }

    static bool InsideFire(Vector3 pos, FireZone f)
    {
        for (int i = 0; i < f.nodes.Count; i++)
        {
            FireNode n = f.nodes[i];
            float dx = pos.x - n.position.x;
            float dz = pos.z - n.position.z;
            float r = n.radius + 0.35f;
            if (dx * dx + dz * dz <= r * r && Mathf.Abs(pos.y - n.position.y) <= 1.8f)
                return true;
        }
        return false;
    }

    void ExtinguishFireInSphere(Vector3 center, float radius)
    {
        float r2 = radius * radius;
        for (int i = fires.Count - 1; i >= 0; i--)
        {
            FireZone f = fires[i];
            bool changed = false;
            for (int j = f.nodes.Count - 1; j >= 0; j--)
            {
                if ((f.nodes[j].position - center).sqrMagnitude <= r2)
                {
                    f.nodes.RemoveAt(j);
                    changed = true;
                }
            }
            if (!changed) continue;

            if (f.nodes.Count <= 2 || (f.origin - center).sqrMagnitude <= r2)
            {
                AnyFireExtinguished?.Invoke(f.origin);
                fires.RemoveAt(i);
                AnyFireEnded?.Invoke(f.id);
            }
            else
            {
                AnyFireExtinguished?.Invoke(center);
                AnyFireNodesChanged?.Invoke(f);
            }
        }
    }

    // ---------------- Decoy ----------------

    void StartDecoy(Vector3 pos, GrenadeData data, Health owner, WeaponData mimicWeapon)
    {
        if (mimicWeapon == null || mimicWeapon.melee || mimicWeapon.taser)
            mimicWeapon = Economy.Find(owner != null && owner.team == 1 ? "M4A4" : "AK-47");

        decoys.Add(new DecoyEmitter
        {
            id = nextEntityId++,
            owner = owner,
            data = data,
            mimicWeapon = mimicWeapon,
            position = pos,
            bornAt = Time.time,
            duration = Mathf.Max(2f, data.duration),
            nextShotAt = Time.time + 0.25f,
            burstLeft = Random.Range(1, 4),
        });
    }

    void StepDecoys()
    {
        float now = Time.time;
        for (int i = decoys.Count - 1; i >= 0; i--)
        {
            DecoyEmitter d = decoys[i];
            if (now >= d.bornAt + d.duration)
            {
                Vector3 pos = d.position;
                GrenadeData data = d.data;
                Health owner = d.owner;
                decoys.RemoveAt(i);
                // Small pop at end of decoy life (5 damage max in 2.5m radius)
                if (data != null && data.damage > 0f)
                {
                    foreach (Health h in Health.All)
                    {
                        if (h == null || h.IsDead || !h.gameObject.activeInHierarchy) continue;
                        if (owner != null && h != owner && h.team == owner.team && !Weapon.FriendlyFire) continue;
                        float dist = Vector3.Distance(pos, TargetCenter(h));
                        if (dist < 2.5f)
                        {
                            float dmg = data.damage * (1f - dist / 2.5f);
                            if (owner != null && h != owner && h.team == owner.team) dmg *= Weapon.FriendlyFireScale;
                            if (dmg > 0.1f) h.TakeDamage(dmg, owner, grenade: data);
                        }
                    }
                }
                AnyDecoyEnded?.Invoke(pos);
                continue;
            }

            if (now < d.nextShotAt) continue;

            // Emit fake shot sound + attract enemy bots like a real shooter
            float noiseRange = d.data != null ? d.data.radius : 40f;
            if (d.owner != null) BotController.Noise(d.position, noiseRange, d.owner);
            AnyDecoyShot?.Invoke(d.position, d.mimicWeapon, d.owner);

            d.burstLeft--;
            if (d.burstLeft > 0)
            {
                float rate = d.mimicWeapon != null && d.mimicWeapon.fireRate > 0.5f ? d.mimicWeapon.fireRate : 7f;
                d.nextShotAt = now + 1f / Mathf.Min(rate, 12f);
            }
            else
            {
                d.burstLeft = Random.Range(1, 5);
                d.nextShotAt = now + Random.Range(0.75f, 2.2f);
            }
        }
    }

    public static void GetActiveDecoyPositions(int viewerTeam, List<Vector3> outList)
    {
        outList.Clear();
        if (instance == null) return;
        for (int i = 0; i < instance.decoys.Count; i++)
        {
            DecoyEmitter d = instance.decoys[i];
            if (d.owner != null && d.owner.team == viewerTeam) continue;
            outList.Add(d.position);
        }
    }

    // ---------------- simulation tick ----------------

    void FixedUpdate()
    {
        float now = Time.time;

        for (int i = smokes.Count - 1; i >= 0; i--)
        {
            SmokeCloud c = smokes[i];
            if (now >= c.bornAt + c.duration)
            {
                smokes.RemoveAt(i);
                AnySmokeEnded?.Invoke(c.id);
                continue;
            }
            ExtinguishFireInSphere(c.center, c.CurrentRadius + 0.4f);
        }

        StepFires();
        StepDecoys();
    }
}
