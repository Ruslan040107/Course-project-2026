using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// One weapon with its ammo, as it lies on the floor or moves between a loadout and the floor.
// ammo / reserve below 0 mean a full magazine / full reserve (like Weapon.Equip).
public struct GunState
{
    public WeaponData data;
    public int ammo;
    public int reserve;
    public float readyAt;   // Weapon.NextShotTime when it was put away (a used Zeus charges again by this time)

    public bool Valid => data != null;
    public bool Empty => ammo == 0 && reserve == 0;

    public static GunState Full(WeaponData d)
    {
        return new GunState { data = d, ammo = -1, reserve = -1, readyAt = 0f };
    }
}

// Weapons on the floor, like in CS (normal 5v5 games only: never in simulations, batch runs or the solo mode).
// - A player or a bot that dies drops his primary weapon, or his pistol if he has no primary (never the knife or the Zeus),
//   with the magazine and reserve it had. It falls from where the gun was in his hands to the floor below
//   (a scripted fall with a small spin, no rigidbody) and lies flat on its side.
// - The player: walking over a weapon takes it when its slot is empty (the hands switch to it if it is better than the
//   weapon in them, like cl_autowepswitch); E while looking at a weapon within PickupRange takes it and throws away the
//   weapon of that slot; G throws the weapon in the hands (primary, pistol or Zeus, not the knife) about 1.5 m forward,
//   the hands take the best weapon left. A hint "E поднять AK-47" shows while the player looks at a weapon he can take.
// - Bots take a primary they walk over when BotBuyer.WantsPickup says so (no primary, an empty one or a cheaper one);
//   their old primary falls at their feet.
// - A bought weapon that replaces another one (PlayerLoadout.Give, BotBuyer.Buy) drops the old one, like in CS.
// - Everything on the floor is removed when the next round starts (weapons dropped while the round starts stay),
//   at most maxDrops lie at once (the oldest goes first).
// A body (Corpses) keeps the gun of the hands; when that gun is the dropped one, the body's copy is removed.
// It adds itself to every scene with a RoundManager.
public class WeaponDrops : MonoBehaviour
{
    [Tooltip("weapons on the floor at most, the oldest one goes first")] public int maxDrops = 30;
    [Tooltip("E takes a weapon this far from the eyes, m")] public float pickupRange = 2.2f;
    [Tooltip("walking over a weapon: it is this close to the feet (horizontally), m")] public float walkOverRadius = 0.8f;
    [Tooltip("whoever dropped a weapon can not walk over it and take it again for this long, s")] public float ownerDelay = 1f;
    [Tooltip("G throw: speed along the view and extra speed up, m/s (it lands about 1.5 m ahead)")]
    public float throwSpeed = 2f;
    public float throwUp = 1.2f;
    [Tooltip("seconds between two walk-over checks of the bots")] public float botCheckInterval = 0.2f;
    [Tooltip("volume of the landing sound (Resources/Audio/Drops)")] [Range(0f, 1f)] public float landVolume = 0.55f;

    public KeyCode pickupKey = KeyCode.E;
    public KeyCode dropKey = KeyCode.G;

    const float Gravity = 9.81f;
    const float SimStep = 0.02f;
    const float MaxFallTime = 3f;
    const int IgnoreRaycastLayer = 2;
    const int LocalBodyLayer = 9;
    // the world without characters and bodies (Ignore Raycast) and the player's own model (LocalBody)
    const int WorldMask = Physics.DefaultRaycastLayers & ~(1 << IgnoreRaycastLayer) & ~(1 << LocalBodyLayer);

    class Item
    {
        public GameObject go;
        public GunState gun;
        public readonly List<Vector3> path = new List<Vector3>();   // the fall, one point every SimStep
        public float born;
        public int bornFrame;
        public float fallTime;
        public Quaternion fromRot, toRot;
        public Vector3 restOffset;   // from the floor point up to where the gun lies
        public float spin;           // degrees around the vertical, it slows down to 0 on landing
        public bool landed;
        public Bounds bounds;        // where it lies (for the look and walk-over checks)
        public Health dropper;
    }

    // the gun in a dead player's hands is also in his body (Corpses): the body's copy goes when the gun was dropped
    struct BodyGun
    {
        public string bodyName;
        public string modelName;
        public int frame;
    }

    static WeaponDrops instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
    }

    RoundManager rounds;
    PlayerController player;
    Health playerHealth;
    PlayerLoadout loadout;
    CharacterController playerController;

    readonly List<Item> drops = new List<Item>();
    readonly List<BodyGun> bodyGuns = new List<BodyGun>();
    readonly RaycastHit[] hits = new RaycastHit[32];
    Item looked;
    float nextBotCheck;
    GUIStyle hintStyle, keyStyle;
    string hintText;
    WeaponData hintData;
    AudioClip[] landClips;

    // ---------------- bootstrap ----------------

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

    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<WeaponDrops>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
        }
        if (rm == null || rm.simulation) return;

        GameObject go = new GameObject("Weapon Drops");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<WeaponDrops>().rounds = rm;
    }

    void Awake()
    {
        RoundManager rm = rounds != null ? rounds : FindAnyObjectByType<RoundManager>();
        if ((rm != null && rm.simulation) || SimulationFlag() || Application.isBatchMode)
        {
            enabled = false;
            return;
        }
        instance = this;
    }

    void Start()
    {
        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
        if (rounds == null)
        {
            enabled = false;
            return;
        }
        if (rounds.player != null) player = rounds.player.GetComponent<PlayerController>();
        if (player == null) player = FindAnyObjectByType<PlayerController>();
        if (player != null)
        {
            playerHealth = player.GetComponent<Health>();
            loadout = player.GetComponent<PlayerLoadout>();
            playerController = player.GetComponent<CharacterController>();
        }
    }

    void OnEnable()
    {
        Health.AnyKilled += OnKilled;
        RoundManager.RoundStarted += OnRoundStarted;
    }

    void OnDisable()
    {
        Health.AnyKilled -= OnKilled;
        RoundManager.RoundStarted -= OnRoundStarted;
        ClearAll();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    // normal 5v5 games only (the solo mode is decided in RoundManager.Start, so it is checked every time)
    bool Running => rounds != null && !rounds.simulation && !rounds.IsSolo;

    // ---------------- for PlayerLoadout and BotBuyer ----------------

    // weapons can be dropped now (a normal 5v5 game)
    public static bool Active => instance != null && instance.isActiveAndEnabled && instance.Running;

    // this player or bot drops the weapon: the human player throws it forward from his eyes, a bot lets it fall
    // in front of it. False (nothing happens) where there are no drops (simulations, the solo mode)
    public static bool Drop(Health owner, GunState gun)
    {
        if (!Active || owner == null || !gun.Valid) return false;
        instance.DropFrom(owner, gun);
        return true;
    }

    // ---------------- rounds and deaths ----------------

    void OnRoundStarted()
    {
        // everything from the last round goes; a weapon dropped while this round starts (a bot bought a better one) stays
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            if (drops[i].bornFrame == Time.frameCount) continue;
            Destroy(drops[i].go);
            drops.RemoveAt(i);
        }
        bodyGuns.Clear();
        looked = null;
    }

    void ClearAll()
    {
        foreach (Item d in drops)
            if (d.go != null) Destroy(d.go);
        drops.Clear();
        bodyGuns.Clear();
        looked = null;
    }

    // the dead drop the primary, else the pistol, with the ammo it had
    void OnKilled(KillInfo k)
    {
        if (!Running) return;
        Health victim = k.victim;
        if (victim == null || victim.invulnerable) return;

        // the gun in the hands now (its model is in the hands of the body that Corpses makes)
        Weapon held = HeldWeapon(victim);
        WeaponData heldData = held != null ? held.data : null;

        GunState gun = default;
        PlayerLoadout lo = victim.GetComponent<PlayerLoadout>();
        BotBuyer buyer = victim.GetComponent<BotBuyer>();
        if (lo != null)
        {
            int slot = lo.InSlot(PlayerLoadout.PrimarySlot) != null ? PlayerLoadout.PrimarySlot
                : lo.InSlot(PlayerLoadout.PistolSlot) != null ? PlayerLoadout.PistolSlot : -1;
            if (slot >= 0) gun = lo.Take(slot);
        }
        else if (buyer != null)
        {
            gun = buyer.TakeDeathDrop();
        }
        else if (heldData != null && IsDroppedOnDeath(heldData))
        {
            gun = new GunState { data = heldData, ammo = held.Ammo, reserve = held.Reserve, readyAt = 0f };
        }
        if (!gun.Valid) return;

        // it falls from the gun in his hands, away from the killer
        Vector3 start;
        Quaternion rot;
        if (!HeldGunPose(victim, out start, out rot))
        {
            Vector3 f = Flat(victim.transform.forward, Vector3.forward);
            start = new Vector3(victim.transform.position.x, FeetY(victim) + 1.1f, victim.transform.position.z) + f * 0.3f;
            rot = Quaternion.LookRotation(f, Vector3.up);
        }

        Vector3 away = Random.insideUnitSphere;
        if (k.killer != null && k.killer != victim) away = victim.transform.position - k.killer.transform.position;
        away = Flat(away, victim.transform.forward);
        Vector3 side = Vector3.Cross(Vector3.up, away);
        Vector3 velocity = away * Random.Range(0.5f, 1.1f) + side * Random.Range(-0.4f, 0.4f) + Vector3.up * Random.Range(0.4f, 1f);

        Item d = Spawn(gun, start, rot, velocity, BodyCentre(victim), victim);
        if (d == null) return;

        // the body would hold a second copy of this gun
        if (heldData == gun.data)
        {
            GameObject prefab = ViewModel.ModelFor(gun.data);
            if (prefab != null)
                bodyGuns.Add(new BodyGun { bodyName = victim.name + " (body)", modelName = prefab.name, frame = Time.frameCount });
        }
    }

    static bool IsDroppedOnDeath(WeaponData d)
    {
        if (d == null || d.melee || d.taser) return false;
        WeaponSlot s = d.Slot;
        return s == WeaponSlot.Primary || s == WeaponSlot.Secondary;
    }

    static Weapon HeldWeapon(Health h)
    {
        BotController bot = h.GetComponent<BotController>();
        if (bot != null && bot.weapon != null) return bot.weapon;
        PlayerShoot shoot = h.GetComponent<PlayerShoot>();
        if (shoot != null && shoot.weapon != null) return shoot.weapon;
        return h.GetComponentInChildren<Weapon>(true);
    }

    // the third person gun model in the hands (BotWeaponModels), not the first person one under the camera
    static bool HeldGunPose(Health h, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        foreach (Transform t in h.GetComponentsInChildren<Transform>(false))
        {
            if (t.name != BotWeaponModels.HolderName || t.childCount == 0) continue;
            if (t.GetComponentInParent<Camera>() != null) continue;
            position = t.position;
            rotation = t.rotation;
            return true;
        }
        return false;
    }

    // ---------------- dropping ----------------

    void DropFrom(Health owner, GunState gun)
    {
        bool isPlayer = player != null && owner == playerHealth;
        Transform cam = isPlayer ? player.cameraTransform : null;

        Vector3 start, velocity;
        Quaternion rot;
        if (cam != null)
        {
            // thrown from the eyes along the view, plus some of the player's own speed
            Vector3 f = cam.forward;
            start = cam.position + f * 0.4f - cam.up * 0.2f;
            rot = Quaternion.LookRotation(f, cam.up);
            velocity = f * throwSpeed + Vector3.up * throwUp;
            if (playerController != null && playerController.enabled) velocity += playerController.velocity * 0.5f;
        }
        else
        {
            // a bot lets it fall in front of itself
            Vector3 f = Flat(owner.transform.forward, Vector3.forward);
            Vector3 p = owner.transform.position;
            start = new Vector3(p.x, FeetY(owner) + 1.1f, p.z) + f * 0.35f;
            rot = Quaternion.LookRotation(f, Vector3.up);
            velocity = f * Random.Range(0.5f, 0.9f) + Vector3.Cross(Vector3.up, f) * Random.Range(-0.3f, 0.3f) + Vector3.up * 0.5f;
        }

        Vector3 from = cam != null ? cam.position : BodyCentre(owner);
        Spawn(gun, start, rot, velocity, from, owner);
    }

    // a new weapon on the floor: it falls from start; 'from' is a point inside the owner, so the gun never starts in a wall
    Item Spawn(GunState gun, Vector3 start, Quaternion rot, Vector3 velocity, Vector3 from, Health owner)
    {
        // never inside a wall: from the owner's body to the start point
        Vector3 toStart = start - from;
        if (toStart.sqrMagnitude > 1e-4f && CastWorld(from, toStart.normalized, toStart.magnitude, out RaycastHit wall))
            start = wall.point + wall.normal * 0.08f;
        // never under the floor it is on (a crouched or falling pose can put the gun a little below it)
        if (CastWorld(start + Vector3.up * 0.35f, Vector3.down, 0.41f, out RaycastHit under) &&
            under.normal.y >= 0.55f && under.point.y > start.y - 0.03f)
            start.y = under.point.y + 0.06f;

        Item d = new Item
        {
            gun = gun,
            born = Time.time,
            bornFrame = Time.frameCount,
            fromRot = rot,
            spin = Random.Range(90f, 240f) * (Random.value < 0.5f ? -1f : 1f),
            dropper = owner
        };

        if (!SimulateFall(start, velocity, d.path, out Vector3 floor, out Vector3 normal)) return null;
        d.fallTime = Mathf.Max(SimStep, (d.path.Count - 1) * SimStep);

        d.go = new GameObject("Dropped " + gun.data.weaponName);
        d.go.transform.SetParent(transform, false);
        MakeModel(gun.data, d.go.transform);
        foreach (Transform t in d.go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = IgnoreRaycastLayer;

        // it lies flat on its side, the barrel about where it flew, turned a little at random
        Vector3 dir = velocity;
        dir.y = 0f;
        dir = Flat(Quaternion.AngleAxis(Random.Range(-50f, 50f), Vector3.up) * (dir.sqrMagnitude > 0.01f ? dir : rot * Vector3.forward), Vector3.forward);
        Vector3 top = Vector3.Cross(Vector3.up, dir) * (Random.value < 0.5f ? -1f : 1f);
        d.toRot = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.LookRotation(dir, top);

        // how high the grip (the model's origin) is above the floor when the gun lies there
        d.go.transform.SetPositionAndRotation(floor, d.toRot);
        float lift = 0.02f;
        if (ModelBounds(d.go, out Bounds b)) lift = floor.y - b.min.y;
        d.restOffset = Vector3.up * (Mathf.Max(lift, 0f) + 0.004f);
        d.go.transform.SetPositionAndRotation(start, rot);

        drops.Add(d);
        while (drops.Count > Mathf.Max(1, maxDrops))
        {
            if (drops[0] == looked) looked = null;
            if (drops[0].go != null) Destroy(drops[0].go);
            drops.RemoveAt(0);
        }
        return d;
    }

    // the flight under gravity in steps of SimStep: walls bounce it back (slower), it stops on the first floor it reaches.
    // False when it finds no floor (out of the map)
    bool SimulateFall(Vector3 start, Vector3 velocity, List<Vector3> path, out Vector3 floor, out Vector3 normal)
    {
        floor = start;
        normal = Vector3.up;
        path.Clear();
        path.Add(start);

        Vector3 p = start;
        Vector3 v = velocity;
        int steps = Mathf.CeilToInt(MaxFallTime / SimStep);
        for (int i = 0; i < steps; i++)
        {
            v.y -= Gravity * SimStep;
            Vector3 move = v * SimStep;
            float len = move.magnitude;
            if (len < 1e-5f)
            {
                path.Add(p);
                continue;
            }

            if (CastWorld(p, move / len, len, out RaycastHit hit))
            {
                if (hit.normal.y >= 0.55f)
                {
                    floor = hit.point;
                    normal = hit.normal;
                    path.Add(floor);
                    return true;
                }
                // a wall: back off it and bounce, slower
                p = hit.point + hit.normal * 0.05f;
                v = Vector3.Reflect(v, hit.normal) * 0.35f;
                path.Add(p);
                continue;
            }

            p += move;
            path.Add(p);
        }
        return false;
    }

    // the first hit of the world (no characters, no bodies, no triggers) along a segment
    bool CastWorld(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        int n = Physics.RaycastNonAlloc(origin, dir, hits, distance, WorldMask, QueryTriggerInteraction.Ignore);
        float bestDist = float.MaxValue;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            Collider c = hits[i].collider;
            if (c == null || c.GetComponentInParent<Health>() != null) continue;
            if (hits[i].distance < bestDist)
            {
                bestDist = hits[i].distance;
                best = hits[i];
                found = true;
            }
        }
        return found;
    }

    // the weapon's model at real size (opaque materials, shadows); a dark box when there is no model
    static void MakeModel(WeaponData d, Transform parent)
    {
        GameObject prefab = ViewModel.ModelFor(d);
        if (prefab != null)
        {
            try
            {
                ViewModel.SpawnModel(prefab, parent, true);
                return;
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Collider c = box.GetComponent<Collider>();
        if (c != null) DestroyImmediate(c);
        box.name = d.weaponName;
        box.transform.SetParent(parent, false);
        float length = d.category == WeaponCategory.Pistol || d.category == WeaponCategory.Gear ? 0.22f : 0.75f;
        box.transform.localScale = new Vector3(0.05f, 0.12f, length);
        box.transform.localPosition = new Vector3(0f, 0.03f, length * 0.3f);
        Renderer r = box.GetComponent<Renderer>();
        if (r != null) r.material.color = new Color(0.12f, 0.12f, 0.13f);
    }

    static bool ModelBounds(GameObject go, out Bounds b)
    {
        b = default;
        bool any = false;
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>())
        {
            if (!any)
            {
                b = r.bounds;
                any = true;
            }
            else
            {
                b.Encapsulate(r.bounds);
            }
        }
        return any;
    }

    // ---------------- every frame ----------------

    void Update()
    {
        if (!Running)
        {
            if (drops.Count > 0) ClearAll();
            looked = null;
            return;
        }

        PlayerInput();

        if (Time.time >= nextBotCheck)
        {
            nextBotCheck = Time.time + botCheckInterval;
            BotPickups();
        }
    }

    // the human player can use the keys now: alive, no menu, the cursor in the game
    bool PlayerCanAct()
    {
        if (player == null || loadout == null || playerHealth == null || playerHealth.IsDead) return false;
        if (!player.isActiveAndEnabled) return false;
        if (MainMenu.IsOpen || BuyMenu.IsOpen || DevConsole.IsOpen || Time.timeScale <= 0f) return false;
        return Cursor.lockState == CursorLockMode.Locked;
    }

    void PlayerInput()
    {
        looked = null;
        if (player == null || loadout == null || playerHealth == null || playerHealth.IsDead) return;
        if (MainMenu.IsOpen || Time.timeScale <= 0f) return;

        // walking over a weapon whose slot is empty takes it (also while the buy menu is open, like in CS)
        Item walked = WalkedOver(playerHealth, d => loadout.InSlot(PlayerLoadout.SlotOf(d.gun.data)) == null);
        if (walked != null)
        {
            int slot = PlayerLoadout.SlotOf(walked.gun.data);
            int now = loadout.CurrentSlot;
            // the hands switch to it when it is better than the weapon in them (primary > pistol > knife)
            bool take = now < 0 || (slot < now && slot != PlayerLoadout.GearSlot);
            if (loadout.PickUp(walked.gun, take, out GunState old))
            {
                Remove(walked);
                if (old.Valid) DropFrom(playerHealth, old);
            }
        }

        if (!PlayerCanAct()) return;

        looked = LookedAt();

        if (Input.GetKeyDown(pickupKey) && looked != null)
        {
            Item d = looked;
            if (loadout.PickUp(d.gun, true, out GunState old))
            {
                Remove(d);
                looked = null;
                // the weapon of that slot is thrown away
                if (old.Valid) DropFrom(playerHealth, old);
            }
        }
        else if (Input.GetKeyDown(dropKey))
        {
            if (loadout.InGrenadeSlot) return;
            int slot = loadout.CurrentSlot;
            if (slot >= 0 && slot != PlayerLoadout.KnifeSlot && loadout.InSlot(slot) != null)
            {
                GunState gun = loadout.Take(slot);
                if (gun.Valid) DropFrom(playerHealth, gun);
            }
        }
    }

    // the weapon on the floor in the middle of the view, within pickupRange and not behind a wall
    Item LookedAt()
    {
        Transform cam = player.cameraTransform;
        if (cam == null) return null;

        Ray ray = new Ray(cam.position, cam.forward);
        Item best = null;
        float bestDist = float.MaxValue;
        foreach (Item d in drops)
        {
            if (!d.landed || d.go == null) continue;
            Bounds b = d.bounds;
            b.Expand(0.25f);
            if (!b.IntersectRay(ray, out float dist) || dist > pickupRange || dist >= bestDist) continue;
            best = d;
            bestDist = dist;
        }
        if (best == null) return null;

        // a wall between the eyes and the gun
        Vector3 to = best.bounds.center - cam.position;
        float len = to.magnitude;
        if (len > 0.05f && CastWorldIgnoring(cam.position, to / len, len - 0.05f, player.transform)) return null;
        return best;
    }

    // something of the world (not the given character) along the segment
    bool CastWorldIgnoring(Vector3 origin, Vector3 dir, float distance, Transform ignore)
    {
        int n = Physics.RaycastNonAlloc(origin, dir, hits, distance, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Collider c = hits[i].collider;
            if (c == null) continue;
            if (ignore != null && c.transform.IsChildOf(ignore)) continue;
            if (c.GetComponentInParent<Health>() != null) continue;
            return true;
        }
        return false;
    }

    // a landed weapon at this character's feet that 'wanted' accepts (the closest one)
    Item WalkedOver(Health h, System.Predicate<Item> wanted)
    {
        Vector3 p = h.transform.position;
        float feet = FeetY(h);
        Item best = null;
        float bestSq = walkOverRadius * walkOverRadius;
        foreach (Item d in drops)
        {
            if (!d.landed || d.go == null) continue;
            if (d.dropper == h && Time.time - d.born < ownerDelay + d.fallTime) continue;
            Vector3 c = d.bounds.center;
            if (c.y < feet - 0.6f || c.y > feet + 0.9f) continue;
            float dx = c.x - p.x, dz = c.z - p.z;
            float sq = dx * dx + dz * dz;
            if (sq > bestSq || !wanted(d)) continue;
            best = d;
            bestSq = sq;
        }
        return best;
    }

    void BotPickups()
    {
        if (drops.Count == 0 || RoundManager.BotsFrozen) return;
        foreach (Health h in Health.All)
        {
            if (h == null || h.IsDead || h == playerHealth || !h.isActiveAndEnabled) continue;
            BotBuyer buyer = h.GetComponent<BotBuyer>();
            if (buyer == null) continue;
            BotController bot = h.GetComponent<BotController>();
            bool inFight = bot != null && bot.Target != null;

            Item d = WalkedOver(h, x => buyer.WantsPickup(x.gun, inFight));
            if (d == null) continue;
            GunState old = buyer.PickUp(d.gun);
            Remove(d);
            if (old.Valid) DropFrom(h, old);
        }
    }

    void Remove(Item d)
    {
        if (d == looked) looked = null;
        if (d.go != null) Destroy(d.go);
        drops.Remove(d);
    }

    void LateUpdate()
    {
        float now = Time.time;
        for (int i = drops.Count - 1; i >= 0; i--)
        {
            Item d = drops[i];
            if (d.go == null)
            {
                drops.RemoveAt(i);
                continue;
            }
            if (!d.landed) Animate(d, now);
        }

        HideBodyGuns();
    }

    // the fall along the path: it turns to lying flat with a spin that slows down, and settles on the floor
    void Animate(Item d, float now)
    {
        float t = Mathf.Min(now - d.born, d.fallTime);
        float p = Mathf.Clamp01(t / d.fallTime);

        float f = t / SimStep;
        int i = Mathf.Min(Mathf.FloorToInt(f), d.path.Count - 1);
        int j = Mathf.Min(i + 1, d.path.Count - 1);
        Vector3 pos = Vector3.Lerp(d.path[i], d.path[j], f - i);

        float e = p * p * (3f - 2f * p);
        Quaternion rot = Quaternion.AngleAxis(d.spin * (1f - e), Vector3.up) * Quaternion.Slerp(d.fromRot, d.toRot, e);
        d.go.transform.SetPositionAndRotation(pos + d.restOffset * e, rot);

        if (p >= 1f)
        {
            d.landed = true;
            Vector3 last = d.path[d.path.Count - 1];
            d.go.transform.SetPositionAndRotation(last + d.restOffset, d.toRot);
            if (!ModelBounds(d.go, out d.bounds)) d.bounds = new Bounds(last, new Vector3(0.3f, 0.1f, 0.3f));
            PlayLanding(d);
        }
    }

    // a metal clatter where it lands (Resources/Audio/Drops/*, nothing when there are no clips)
    void PlayLanding(Item d)
    {
        if (landClips == null) landClips = Resources.LoadAll<AudioClip>("Audio/Drops");
        if (landClips.Length == 0) return;

        AudioSource src = d.go.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 1f;
        src.rolloffMode = AudioRolloffMode.Linear;
        src.minDistance = 1.5f;
        src.maxDistance = 25f;
        src.dopplerLevel = 0f;
        src.pitch = Random.Range(0.92f, 1.08f);
        src.PlayOneShot(landClips[Random.Range(0, landClips.Length)], landVolume);
    }

    // the copy of a dropped gun in a body's hand (Corpses makes the body in the same kill, it may be after this script)
    void HideBodyGuns()
    {
        for (int i = bodyGuns.Count - 1; i >= 0; i--)
        {
            BodyGun g = bodyGuns[i];
            GameObject body = GameObject.Find(g.bodyName);
            if (body != null)
            {
                foreach (Transform t in body.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name != BotWeaponModels.HolderName) continue;
                    if (ViewModel.FindChild(t, g.modelName) != null) Destroy(t.gameObject);
                }
                bodyGuns.RemoveAt(i);
            }
            else if (Time.frameCount > g.frame + 2)
            {
                bodyGuns.RemoveAt(i);
            }
        }
    }

    // ---------------- hint ----------------

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (looked == null || !looked.gun.Valid || !Running || !PlayerCanAct()) return;

        float k = GameHud.Scale;
        if (hintStyle == null)
        {
            hintStyle = GameHud.MakeStyle(FontStyle.Bold);
            keyStyle = GameHud.MakeStyle(FontStyle.Bold);
        }
        hintStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(19f * k));
        keyStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(17f * k));
        if (hintData != looked.gun.data)
        {
            hintData = looked.gun.data;
            hintText = "поднять " + hintData.weaponName;
        }

        // a key cap "E" and the text, under the crosshair
        float pad = 10f * k;
        float key = Mathf.Round(24f * k);
        float h = Mathf.Round(36f * k);
        float tw = GameHud.Width(hintStyle, hintText);
        float w = pad + key + 8f * k + tw + pad * 1.5f;
        Rect box = new Rect(Mathf.Round(Screen.width * 0.5f - w * 0.5f), Mathf.Round(Screen.height * 0.5f + 70f * k), Mathf.Round(w), h);
        GameHud.Fill(box, new Color(0.03f, 0.04f, 0.05f, 0.6f));

        Rect cap = new Rect(Mathf.Round(box.x + pad), Mathf.Round(box.y + (h - key) * 0.5f), key, key);
        GameHud.Fill(cap, new Color(1f, 1f, 1f, 0.9f));
        GameHud.Text(cap, "E", keyStyle, new Color(0.05f, 0.05f, 0.06f, 1f), TextAnchor.MiddleCenter);
        GameHud.Text(new Rect(cap.xMax + 8f * k, box.y, tw + pad, h), hintText, hintStyle, Color.white, TextAnchor.MiddleLeft);
        GUI.color = Color.white;
    }

    // ---------------- helpers ----------------

    // the height of the feet: the bottom of the player's controller, the navmesh point of a bot
    static float FeetY(Health h)
    {
        CharacterController cc = h.GetComponent<CharacterController>();
        if (cc != null && cc.enabled) return cc.bounds.min.y;
        NavMeshAgent agent = h.GetComponent<NavMeshAgent>();
        if (agent != null) return h.transform.position.y - agent.baseOffset;
        return h.transform.position.y - 1f;
    }

    static Vector3 BodyCentre(Health h)
    {
        Vector3 p = h.transform.position;
        return new Vector3(p.x, FeetY(h) + 1f, p.z);
    }

    // horizontal and normalized; the fallback (made horizontal too) when it has no horizontal part
    static Vector3 Flat(Vector3 v, Vector3 fallback)
    {
        v.y = 0f;
        if (v.sqrMagnitude < 1e-4f)
        {
            v = fallback;
            v.y = 0f;
            if (v.sqrMagnitude < 1e-4f) v = Vector3.forward;
        }
        return v.normalized;
    }
}
