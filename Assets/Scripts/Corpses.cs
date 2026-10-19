using System.Collections.Generic;
using UnityEngine;

// A killed player does not vanish: his body falls over away from the killer
// and lies on the floor until the next round.
// - With a character model (CharacterBody): a new copy of the team model stands where the victim stood,
//   turned (at most maxTurnToKiller) to face the killer and to a side with room behind it, and plays its
//   Death clip once (it falls on its back and ends lying), then the animation stops. The gun stays in its hand.
//   If the victim died in a jump the body falls down to the floor; on a slope it tilts with the floor.
// - Without one (capsules) the fall is scripted, without physics, so every body really ends up lying: it turns
//   90 degrees around its foot, speeding up like under gravity, lands on the floor below (also on a slope)
//   and bounces a little. If a wall is in the way, the body falls to a free side instead.
// A body seen only by its shadow (the player's own model) is fully visible when it lies.
// The copy has no colliders and is on the Ignore Raycast layer, so bullets, bot vision
// and living players go through it. CombatEffects adds this component (never in simulation).
public class Corpses : MonoBehaviour
{
    public int maxBodies = 12;
    [Tooltip("a character body turns at most this much to face its killer, degrees")] public float maxTurnToKiller = 60f;
    [Tooltip("seconds a character body takes for that turn")] public float turnTime = 0.35f;
    [Tooltip("seconds from standing to lying")] public float fallTime = 0.45f;
    public float headshotFallTime = 0.33f;
    [Tooltip("random turn of the fall direction, degrees")] public float directionSpread = 20f;
    [Tooltip("random turn of the body around the vertical while it falls, degrees")] public float maxYaw = 15f;
    [Tooltip("the body bounces back by this angle when it hits the floor")] public float bounceAngle = 5f;
    public float bounceTime = 0.18f;

    const int IgnoreRaycastLayer = 2;
    const float Gravity = 9.81f;
    const float MaxSlope = 25f;            // degrees, the body follows the floor up to this slope
    const float SupportVolume = 0.15f;     // smaller parts (a weapon) may go into the floor, the body may not
    const int MaxPointsPerPart = 1500;

    // the fall direction is tried first, then these turns from it
    static readonly float[] TryAngles = { 0f, 40f, -40f, 80f, -80f, 120f, -120f, 160f, -160f };

    class Body
    {
        public GameObject go;
        public readonly List<Mesh> meshes = new List<Mesh>();   // snapshots of skinned meshes, destroyed with the body
        public Vector3 pivot;      // foot of the body on the side it falls to
        public Vector3 axis;       // horizontal, the body turns around it
        public float angle;        // final tilt: 90 on a flat floor
        public float yaw;
        public float startY, endY; // height of the pivot before and after the fall
        public float start, fallTime, landTime;
        public bool done;

        // a character model playing its Death clip
        public bool character;
        public Animation anim;
        public string deathState;
        public float clipLength;
        public Quaternion fromRot, toRot;   // turn to the killer / to a free side
        public float tilt;                  // degrees around the body's right axis at the end (the floor's slope)
    }

    // one visible mesh of the victim
    struct Piece
    {
        public Renderer source;
        public Mesh mesh;
        public float volume;
    }

    readonly List<Body> bodies = new List<Body>();
    readonly List<Piece> pieces = new List<Piece>();
    readonly List<Vector3> points = new List<Vector3>();
    readonly RaycastHit[] hits = new RaycastHit[32];
    Body building;

    void Awake()
    {
        // never in simulations: background runs pass -sim, RoundManager reads it later than this Awake
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        bool simFlag = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-sim") >= 0;
        if ((rm != null && rm.simulation) || simFlag) enabled = false;
    }

    void OnEnable()
    {
        Health.AnyKilled += OnKilled;
        RoundManager.RoundStarted += Clear;
    }

    void OnDisable()
    {
        Health.AnyKilled -= OnKilled;
        RoundManager.RoundStarted -= Clear;
        Clear();
    }

    void OnKilled(KillInfo k)
    {
        // an error here must not stop the other kill handlers: Health hides the victim only after all of them
        try
        {
            Spawn(k);
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            // a half built body is removed, so it can never stay standing
            if (building != null && !bodies.Contains(building)) Remove(building);
        }
        building = null;
    }

    void Spawn(KillInfo k)
    {
        Health victim = k.victim;
        if (victim == null || !victim.gameObject.activeInHierarchy) return;

        // a character model plays its death; without a Death clip (or after an error) it falls like the other bodies
        CharacterBody cb = victim.GetComponent<CharacterBody>();
        if (cb != null && cb.HasModel && cb.Prefab != null)
        {
            bool done = false;
            try
            {
                done = SpawnCharacter(k, cb);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                if (building != null)
                {
                    bodies.Remove(building);
                    Remove(building);
                }
            }
            building = null;
            if (done)
            {
                TrimBodies();
                return;
            }
        }

        Body b = new Body();
        building = b;

        CollectPieces(victim, b);
        if (pieces.Count == 0)
        {
            Remove(b);
            return;
        }

        // points of the big parts: they decide where the body touches the floor
        float maxVolume = 0f;
        foreach (Piece p in pieces) maxVolume = Mathf.Max(maxVolume, p.volume);
        points.Clear();
        foreach (Piece p in pieces)
            if (p.volume >= maxVolume * SupportVolume) AddPoints(p.mesh, p.source.transform.localToWorldMatrix);

        Vector3 min = points[0], max = points[0];
        foreach (Vector3 p in points)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Vector3 center = (min + max) * 0.5f;
        float footY = min.y;
        float height = Mathf.Max(max.y - min.y, 0.5f);
        float radius = Mathf.Max(max.x - min.x, max.z - min.z) * 0.5f;

        // the floor under the victim (he may have died in a jump)
        float ground = FloorBelow(new Vector3(center.x, footY + height * 0.5f, center.z), 60f, out float g) ? g : footY;

        // away from the killer, a random side if there is none
        Vector3 wanted = Vector3.zero;
        if (k.killer != null && k.killer != victim) wanted = victim.transform.position - k.killer.transform.position;
        wanted.y = 0f;
        if (wanted.sqrMagnitude < 0.0001f) wanted = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
        wanted = Quaternion.Euler(0f, Random.Range(-directionSpread, directionSpread), 0f) * wanted.normalized;

        float yaw = Random.Range(-maxYaw, maxYaw);
        Quaternion yawRot = Quaternion.AngleAxis(yaw, Vector3.up);
        Vector3 dir = ChooseDirection(center, wanted, yawRot, ground, radius, height);
        Vector3 lyingDir = yawRot * dir;

        // the body turns around its foot on the side it falls to
        float reach = 0f;
        foreach (Vector3 p in points) reach = Mathf.Max(reach, Vector3.Dot(p - center, dir));
        Vector3 pivot = new Vector3(center.x, footY, center.z) + dir * reach;
        Vector3 axis = Vector3.Cross(Vector3.up, dir).normalized;   // turning around it by +90 takes up to dir

        // the floor under the lying body as a line through two points, so the body also lies on a slope
        float s1 = height * 0.2f, s2 = height * 0.85f;
        float y1 = FloorHeight(pivot + lyingDir * s1, ground);
        float y2 = FloorHeight(pivot + lyingDir * s2, ground);
        float maxTan = Mathf.Tan(MaxSlope * Mathf.Deg2Rad);
        float slope = Mathf.Clamp((y2 - y1) / (s2 - s1), -maxTan, maxTan);
        float angle = 90f - Mathf.Atan(slope) * Mathf.Rad2Deg;
        Quaternion lying = yawRot * Quaternion.AngleAxis(angle, axis);

        // the lowest pivot height at which no point of the body is under that floor
        float endY = float.MinValue;
        foreach (Vector3 p in points)
        {
            Vector3 o = lying * (p - pivot);
            float s = o.x * lyingDir.x + o.z * lyingDir.z;
            endY = Mathf.Max(endY, y1 + (s - s1) * slope - o.y);
        }
        endY = Mathf.Min(endY, footY + height);

        float time = Mathf.Max(0.05f, (k.headshot ? headshotFallTime : fallTime) * Random.Range(0.9f, 1.1f));
        // falling from a jump takes longer than turning over: the bounce waits for the landing
        float landTime = time;
        if (footY > endY) landTime = Mathf.Max(time, Mathf.Sqrt(2f * (footY - endY) / Gravity));

        b.pivot = pivot;
        b.axis = axis;
        b.angle = angle;
        b.yaw = yaw;
        b.startY = footY;
        b.endY = endY;
        b.start = Time.time;
        b.fallTime = time;
        b.landTime = landTime;

        b.go = new GameObject(victim.name + " (body)");
        b.go.layer = IgnoreRaycastLayer;
        b.go.transform.SetPositionAndRotation(pivot, Quaternion.identity);
        // registered before the parts are made, so Clear always removes it
        bodies.Add(b);

        foreach (Piece p in pieces) AddPart(b.go.transform, p);

        TrimBodies();
    }

    void TrimBodies()
    {
        while (bodies.Count > maxBodies)
        {
            Remove(bodies[0]);
            bodies.RemoveAt(0);
        }
    }

    // a new copy of the victim's team model at his place, playing the Death clip (it falls on its back)
    bool SpawnCharacter(KillInfo k, CharacterBody cb)
    {
        Health victim = k.victim;
        Transform src = cb.Model;
        Vector3 feet = src.position;
        Vector3 forward = src.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        Body b = new Body { character = true };
        building = b;
        b.go = new GameObject(victim.name + " (body)");
        b.go.layer = IgnoreRaycastLayer;
        b.go.transform.SetPositionAndRotation(feet, Quaternion.LookRotation(forward));
        // registered before the parts are made, so Clear always removes it
        bodies.Add(b);

        // the copy stands exactly where the living model stood: the body's origin is at the feet below it
        GameObject model = CharacterBody.SpawnModel(cb.Prefab, b.go.transform);
        feet -= Vector3.up * model.transform.localPosition.y;
        b.go.transform.position = feet;
        Animation anim = model.GetComponent<Animation>();
        string death = CharacterBody.FindState(anim, "Death");
        if (anim == null || death == null)
        {
            bodies.Remove(b);
            Remove(b);
            building = null;
            return false;
        }

        // the gun stays in the right hand (the mount is measured on the pointing pose, before the death starts)
        BotController bot = victim.GetComponent<BotController>();
        Weapon weapon = bot != null ? bot.weapon : victim.GetComponentInChildren<Weapon>(true);
        GameObject gun = weapon != null ? ViewModel.ModelFor(weapon.data) : null;
        if (gun != null)
        {
            Transform mount = CharacterBody.MakeWeaponMount(model.transform, anim);
            if (mount != null)
            {
                Transform holder = new GameObject(BotWeaponModels.HolderName).transform;
                holder.SetParent(mount, false);
                ViewModel.SpawnModel(gun, holder, true);
            }
        }

        foreach (Transform t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = IgnoreRaycastLayer;
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
        {
            // fully visible, also a copy of the player's own (shadow only) body
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            if (r is SkinnedMeshRenderer s) s.updateWhenOffscreen = true;
        }

        anim.cullingType = AnimationCullingType.AlwaysAnimate;
        anim.playAutomatically = false;
        AnimationState state = anim[death];
        state.wrapMode = WrapMode.ClampForever;
        state.speed = 1f;
        anim.Stop();
        anim.Play(death);

        // it falls on its back: face the killer (a limited turn), so it falls away from him,
        // and turn to a side with room behind it if a wall is there
        Vector3 wanted = -forward;
        if (k.killer != null && k.killer != victim)
        {
            Vector3 toKiller = k.killer.transform.position - feet;
            toKiller.y = 0f;
            if (toKiller.sqrMagnitude > 0.0001f)
            {
                float turn = Mathf.Clamp(Vector3.SignedAngle(forward, toKiller, Vector3.up), -maxTurnToKiller, maxTurnToKiller);
                wanted = -(Quaternion.AngleAxis(turn, Vector3.up) * forward);
            }
        }

        Vector3 chest = feet + Vector3.up * (CharacterBody.ModelHeight * 0.5f);
        float ground = FloorBelow(chest, 60f, out float g) ? g : feet.y;
        Vector3 dir = ChooseDirection(chest, wanted, Quaternion.identity, ground, 0.3f, CharacterBody.ModelHeight);
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = wanted;
        dir.Normalize();

        // the floor under the lying body as a line, so it also lies on a slope
        float s1 = 0.3f, s2 = 1.4f;
        float y1 = FloorHeight(feet + dir * s1, ground);
        float y2 = FloorHeight(feet + dir * s2, ground);
        float maxTan = Mathf.Tan(MaxSlope * Mathf.Deg2Rad);
        float slope = Mathf.Clamp((y2 - y1) / (s2 - s1), -maxTan, maxTan);

        b.anim = anim;
        b.deathState = death;
        b.clipLength = Mathf.Max(0.1f, state.length);
        b.fromRot = Quaternion.LookRotation(forward);
        b.toRot = Quaternion.LookRotation(-dir);
        b.tilt = Mathf.Atan(slope) * Mathf.Rad2Deg;
        b.pivot = feet;
        b.startY = feet.y;
        b.endY = FloorHeight(feet, ground);
        b.start = Time.time;
        building = null;
        return true;
    }

    // every visible mesh except the ones on the camera (the first person weapon)
    void CollectPieces(Health victim, Body b)
    {
        pieces.Clear();
        foreach (Renderer r in victim.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled || r.forceRenderingOff || r.GetComponentInParent<Camera>() != null) continue;

            Mesh mesh = null;
            if (r is MeshRenderer)
            {
                MeshFilter f = r.GetComponent<MeshFilter>();
                if (f != null) mesh = f.sharedMesh;
            }
            else if (r is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
            {
                // a snapshot of the current pose, in the renderer's own space like a normal mesh
                mesh = new Mesh();
                skinned.BakeMesh(mesh);
                b.meshes.Add(mesh);
            }
            if (mesh == null) continue;

            Vector3 size = r.bounds.size;
            float volume = Mathf.Max(size.x, 0.05f) * Mathf.Max(size.y, 0.05f) * Mathf.Max(size.z, 0.05f);
            pieces.Add(new Piece { source = r, mesh = mesh, volume = volume });
        }
    }

    // world positions of the mesh vertices (the corners of its box if it cannot be read)
    void AddPoints(Mesh mesh, Matrix4x4 toWorld)
    {
        if (mesh.isReadable && mesh.vertexCount > 0)
        {
            Vector3[] v = mesh.vertices;
            int step = Mathf.Max(1, v.Length / MaxPointsPerPart);
            for (int i = 0; i < v.Length; i += step) points.Add(toWorld.MultiplyPoint3x4(v[i]));
            return;
        }

        Bounds bounds = mesh.bounds;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
            points.Add(toWorld.MultiplyPoint3x4(bounds.center + Vector3.Scale(bounds.extents, corner)));
        }
    }

    void AddPart(Transform parent, Piece p)
    {
        Transform src = p.source.transform;
        GameObject part = new GameObject(p.source.name);
        part.layer = IgnoreRaycastLayer;
        part.transform.SetParent(parent, false);
        part.transform.SetPositionAndRotation(src.position, src.rotation);
        part.transform.localScale = src.lossyScale;
        part.AddComponent<MeshFilter>().sharedMesh = p.mesh;

        MeshRenderer mr = part.AddComponent<MeshRenderer>();
        mr.sharedMaterials = p.source.sharedMaterials;
        // a body seen only by its shadow (first person) is fully visible when it lies
        mr.shadowCastingMode = p.source.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly
            ? UnityEngine.Rendering.ShadowCastingMode.On
            : p.source.shadowCastingMode;
        mr.receiveShadows = p.source.receiveShadows;
    }

    // the wanted direction if the body has room to lie there, otherwise the nearest side with the most room
    Vector3 ChooseDirection(Vector3 center, Vector3 wanted, Quaternion yawRot, float ground, float radius, float height)
    {
        float castHeight = Mathf.Clamp(radius, 0.35f, 0.6f);
        float sphere = Mathf.Min(0.25f, castHeight - 0.1f);
        Vector3 from = new Vector3(center.x, ground + castHeight, center.z);
        float length = radius + height;
        float side = Random.value < 0.5f ? 1f : -1f;

        Vector3 best = wanted;
        float bestScore = -1f;
        foreach (float a in TryAngles)
        {
            Vector3 d = Quaternion.Euler(0f, a * side, 0f) * wanted;
            Vector3 lyingDir = yawRot * d;

            float score = FreeDistance(from, lyingDir, sphere, length) / length;
            if (FloorNear(from + lyingDir * (radius + height * 0.5f), ground) &&
                FloorNear(from + lyingDir * (radius + height * 0.9f), ground))
                score += 1f;

            if (score >= 1.999f) return d;
            if (score > bestScore)
            {
                bestScore = score;
                best = d;
            }
        }
        return best;
    }

    // how far a sphere moves before it hits something that is not a character or a floor
    float FreeDistance(Vector3 from, Vector3 dir, float radius, float length)
    {
        int n = Physics.SphereCastNonAlloc(from, radius, dir, hits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float free = length;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hits[i];
            if (IsCharacter(h.collider)) continue;
            if (h.distance > 0f && h.normal.y > 0.6f) continue;   // floor or a ramp, the body lies on it
            free = Mathf.Min(free, h.distance);
        }
        return free;
    }

    // there is a floor at about the victim's floor level (not a hole or a drop)
    bool FloorNear(Vector3 point, float ground)
    {
        return FloorBelow(new Vector3(point.x, ground + 0.9f, point.z), 2.5f, out float y) && Mathf.Abs(y - ground) <= 0.6f;
    }

    // floor height at a point near the body, the victim's floor if it is missing or too far
    float FloorHeight(Vector3 point, float ground)
    {
        if (FloorBelow(new Vector3(point.x, ground + 0.9f, point.z), 2.5f, out float y) && y > ground - 1.2f && y < ground + 0.8f)
            return y;
        return ground;
    }

    // the nearest surface below, not counting players, bots and triggers (bodies have no colliders)
    bool FloorBelow(Vector3 from, float length, out float y)
    {
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hits, length, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        y = 0f;
        for (int i = 0; i < n; i++)
        {
            if (IsCharacter(hits[i].collider) || hits[i].distance >= best) continue;
            best = hits[i].distance;
            y = hits[i].point.y;
        }
        return best < float.MaxValue;
    }

    static bool IsCharacter(Collider c)
    {
        return c != null && c.GetComponentInParent<Health>() != null;
    }

    void Update()
    {
        float now = Time.time;
        foreach (Body b in bodies)
        {
            if (b.done || b.go == null) continue;
            float t = now - b.start;
            if (b.character)
            {
                PlaceCharacter(b, t);
                continue;
            }
            Place(b, t);
            if (t >= b.landTime + bounceTime) b.done = true;
        }
    }

    // turns to its side, falls to the floor if it died in a jump, tilts with the floor while the Death clip plays;
    // when the clip is over and the body is down, the animation stops on the last frame
    void PlaceCharacter(Body b, float t)
    {
        float k = Mathf.Clamp01(t / Mathf.Max(turnTime, 0.01f));
        Quaternion yaw = Quaternion.Slerp(b.fromRot, b.toRot, k * k * (3f - 2f * k));
        float f = Mathf.Clamp01(t / b.clipLength);
        Quaternion rot = Quaternion.AngleAxis(b.tilt * f * f, yaw * Vector3.right) * yaw;

        float y = b.startY > b.endY
            ? Mathf.Max(b.endY, b.startY - 0.5f * Gravity * t * t)
            : Mathf.Lerp(b.startY, b.endY, Mathf.Clamp01(t / 0.2f));
        b.go.transform.SetPositionAndRotation(new Vector3(b.pivot.x, y, b.pivot.z), rot);

        if (t < b.clipLength + 0.05f || t < turnTime || Mathf.Abs(y - b.endY) > 0.001f) return;
        if (b.anim != null)
        {
            AnimationState s = b.anim[b.deathState];
            if (s != null)
            {
                s.enabled = true;
                s.weight = 1f;
                s.time = s.length;
                b.anim.Sample();
            }
            b.anim.enabled = false;
        }
        b.done = true;
    }

    void Place(Body b, float t)
    {
        float k = Mathf.Clamp01(t / b.fallTime);
        float angle = b.angle * k * k;   // like gravity: slow at first, fast at the end

        float bt = t - b.landTime;
        if (bt > 0f && bt < bounceTime) angle -= bounceAngle * Mathf.Sin(Mathf.PI * bt / bounceTime);

        // falls down from a jump, or moves up a little (a crouching player's body goes into the floor)
        float y = b.startY > b.endY
            ? Mathf.Max(b.endY, b.startY - 0.5f * Gravity * t * t)
            : Mathf.Lerp(b.startY, b.endY, k);

        Quaternion rot = Quaternion.AngleAxis(b.yaw * k, Vector3.up) * Quaternion.AngleAxis(angle, b.axis);
        b.go.transform.SetPositionAndRotation(new Vector3(b.pivot.x, y, b.pivot.z), rot);
    }

    void Remove(Body b)
    {
        if (b.go != null) Destroy(b.go);
        foreach (Mesh m in b.meshes)
            if (m != null) Destroy(m);
        b.meshes.Clear();
    }

    void Clear()
    {
        foreach (Body b in bodies) Remove(b);
        bodies.Clear();
    }
}
