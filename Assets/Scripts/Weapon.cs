using System.Collections.Generic;
using UnityEngine;

public struct ShotInfo
{
    public Vector3 origin;
    public Vector3 end;
    public Vector3 normal;
    public bool hit;
    public bool headshot;
    public Health shooter;
    public Health target;   // the first player hit (a bullet can go on through him, see Weapon.FireThrough)
    public Transform muzzle;
    public float recoil;
    public bool melee;
    public bool wallbang;   // the bullet went through at least one object before it hit
}

public class Weapon : MonoBehaviour
{
    public static event System.Action<ShotInfo> AnyShot;
    public event System.Action ReloadStarted;
    // bullets go through objects (see Penetrable); RoundManager turns it off in simulations unless -penetration 1
    public static bool PenetrationEnabled = true;
    // a bullet went through an object: entry point, entry normal, exit point, exit normal (for bullet holes);
    // also for the second and next bodies a bullet hits (for blood, CombatEffects tells them by the point)
    public static event System.Action<Vector3, Vector3, Vector3, Vector3> AnyPenetration;

    // shots and the knife hurt teammates (FriendlyFireScale of the damage); RoundManager turns it off in simulations unless -ff 1
    public static bool FriendlyFire = true;
    // part of the damage a teammate takes (as bullets in CS 2)
    public const float FriendlyFireScale = 0.33f;

    // for sounds: any weapon started a reload / was taken in hands
    public static event System.Action<Weapon> AnyReloadStarted;
    public static event System.Action<Weapon> AnyEquipped;

    // solo test mode: the magazine never runs out
    public static bool InfiniteAmmo;

    PlayerController movement;

    [Header("Data (optional, overrides the values below)")]
    public WeaponData data;

    [Header("Info")]
    public string weaponName = "Rifle";

    [Header("Damage")]
    public Transform muzzle;
    public float damage = 25f;
    public float headMultiplier = 3f;
    public float legMultiplier = 0.75f;
    [Tooltip("used when the hit collider has no HitZone: the head is the part of the collider above this height (0..1)")]
    [Range(0f, 1f)] public float headZoneStart = 0.72f;
    [Tooltip("the legs are the part of the collider below this height (0..1)")]
    [Range(0f, 1f)] public float legZoneEnd = 0.35f;

    [Header("Fire")]
    public float fireRate = 8f;
    public float range = 100f;
    public float spreadAngle = 0f;
    public bool automatic = true;

    [Header("Ammo")]
    public int magazineSize = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 2.2f;

    [Header("Other")]
    public float botSpreadMultiplier = 2f;
    public float moveSpeedFactor = 1f;
    public float recoilFactor = 1f;

    // 0..1, how much the shooter moves; the player gets it from PlayerController, bots set it from BotController
    public float ExternalMoveInaccuracy { get; set; }

    // bots set these while they hop
    public bool ExternalInAir { get; set; }
    public float ExternalSpreadMultiplier { get; set; } = 1f;

    public bool IsMelee => data != null && data.melee;
    public Health Owner => owner;

    // everything except automatic rifles reloads by itself when the magazine gets empty
    bool AutoReloads => !(data != null && data.category == WeaponCategory.Rifle && data.automatic);

    // set by PlayerShoot; bots are always treated as scoped
    public bool Scoped { get; set; } = true;

    float nextShotTime;
    float drawEndTime;
    float reloadEnd;
    int ammo;
    int reserve;
    bool reloading;
    Health owner;

    public int Ammo { get { FinishReload(); return ammo; } }
    public int Reserve { get { FinishReload(); return reserve; } }
    public bool IsReloading { get { FinishReload(); return reloading; } }
    public float NextShotTime => nextShotTime;
    // the weapon is still being taken in the hands: no shots and no scope until the draw animation ends
    public bool IsDrawing => Time.time < drawEndTime;
    public float ReloadProgress => reloading ? Mathf.Clamp01(1f - (reloadEnd - Time.time) / reloadTime) : 1f;

    void Awake()
    {
        movement = GetComponentInParent<PlayerController>();
        owner = GetComponentInParent<Health>();
        if (data != null) CopyData();
        ResetAmmo();
    }

    void CopyData()
    {
        weaponName = data.weaponName;
        damage = data.damage;
        headMultiplier = data.headMultiplier;
        legMultiplier = data.legMultiplier;
        range = data.range;
        fireRate = data.fireRate;
        automatic = data.automatic;
        spreadAngle = data.spreadAngle;
        recoilFactor = data.recoil;
        magazineSize = data.magazineSize;
        reserveAmmo = data.reserveAmmo;
        reloadTime = data.reloadTime;
        moveSpeedFactor = data.moveSpeedFactor;
    }

    // ammoNow / reserveNow below 0 means a full magazine and full reserve
    public void Equip(WeaponData newData, int ammoNow = -1, int reserveNow = -1, float readyAt = 0f)
    {
        data = newData;
        if (data != null) CopyData();

        ResetAmmo();
        if (ammoNow >= 0) ammo = Mathf.Min(ammoNow, magazineSize);
        if (reserveNow >= 0) reserve = reserveNow;

        // the weapon is ready after its draw time, but never before its own shot cooldown has passed
        float drawEnd = Time.time + (data != null ? data.drawTime : 0f);
        nextShotTime = Mathf.Max(readyAt, drawEnd);
        drawEndTime = drawEnd;
        AnyEquipped?.Invoke(this);
    }

    public void ResetAmmo()
    {
        ammo = magazineSize;
        reserve = reserveAmmo;
        reloading = false;
        nextShotTime = 0f;
    }

    void FinishReload()
    {
        if (!reloading || Time.time < reloadEnd) return;

        int take = Mathf.Min(magazineSize - ammo, reserve);
        ammo += take;
        reserve -= take;
        reloading = false;
    }

    public void StartReload()
    {
        FinishReload();
        if (InfiniteAmmo) return;
        if (reloading || ammo >= magazineSize || reserve <= 0) return;

        reloading = true;
        reloadEnd = Time.time + reloadTime;
        ReloadStarted?.Invoke();
        AnyReloadStarted?.Invoke(this);
    }

    public bool TryFire()
    {
        // nothing until the weapon is drawn (also after ResetAmmo, which clears the shot cooldown)
        if (IsDrawing) return false;
        if (IsMelee) return Melee(false);

        FinishReload();
        if (reloading) return false;

        if (ammo <= 0)
        {
            StartReload();
            return false;
        }

        if (Time.time < nextShotTime) return false;
        nextShotTime = Time.time + 1f / fireRate;
        ammo--;
        if (InfiniteAmmo) ammo = magazineSize;
        if (ammo <= 0 && AutoReloads) StartReload();

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        bool inAir = movement != null ? !movement.IsGrounded : ExternalInAir;
        float mult = movement != null ? movement.SpreadMultiplier : botSpreadMultiplier * ExternalSpreadMultiplier;
        bool noScope = data != null && data.scopeFov != null && data.scopeFov.Length > 0 && !Scoped;
        if (noScope) mult *= data.unscopedSpreadMultiplier;

        float spreadNow = spreadAngle * mult;
        if (noScope) spreadNow = Mathf.Max(spreadNow, data.unscopedMinSpread);

        // scoped sniper while moving: the scope does not help, the shot goes wide
        bool scopedSniper = data != null && data.scopeFov != null && data.scopeFov.Length > 0 && Scoped;
        if (scopedSniper)
        {
            float mi = movement != null ? movement.MoveInaccuracy : ExternalMoveInaccuracy;
            spreadNow = Mathf.Max(spreadNow, data.scopedMoveSpread * mi);
        }
        Quaternion spread = Quaternion.Euler(
            Random.Range(-spreadNow, spreadNow),
            Random.Range(-spreadNow, spreadNow),
            0f);
        Vector3 direction = muzzle.rotation * spread * Vector3.forward;

        ShotInfo shot = new ShotInfo
        {
            origin = muzzle.position,
            end = muzzle.position + direction * range,
            shooter = owner,
            muzzle = muzzle,
            recoil = recoilFactor
        };

        if (PenetrationEnabled)
        {
            FireThrough(direction, noScope, inAir, ref shot);
        }
        else if (Physics.Raycast(muzzle.position, direction, out RaycastHit hit, range))
        {
            // the old shot without penetration: the first collider stops the bullet,
            // a teammate takes damage only with friendly fire
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;
            bool head = false;

            if (target != null && target != owner && (!friendly || FriendlyFire))
            {
                float multiplier = ZoneMultiplier(hit, out head);
                if (friendly) multiplier *= FriendlyFireScale;
                target.TakeDamage(damage * multiplier, owner, head, noScope, inAir, false, data);
            }

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.headshot = head;
            shot.target = target;

            Debug.DrawLine(muzzle.position, hit.point, Color.red, 0.1f);
        }

        AnyShot?.Invoke(shot);
        return true;
    }

    // damage multiplier of the body part that was hit
    float ZoneMultiplier(RaycastHit hit, out bool head)
    {
        head = false;
        HitZone hitZone = hit.collider.GetComponent<HitZone>();

        if (hitZone != null)
        {
            // character model with separate head, body and leg colliders
            if (hitZone.zone == BodyZone.Head)
            {
                head = true;
                return headMultiplier;
            }
            if (hitZone.zone == BodyZone.Legs) return legMultiplier;
            return 1f;
        }

        // one collider: decide by the height of the hit
        Bounds b = hit.collider.bounds;
        float t = Mathf.InverseLerp(b.min.y, b.max.y, hit.point.y);

        if (t >= headZoneStart)
        {
            head = true;
            return headMultiplier;
        }
        if (t <= legZoneEnd) return legMultiplier;
        return 1f;
    }

    // ---------------- penetration ----------------

    const float MaxThickness = 3f;      // a thicker object always stops the bullet
    const int MaxPenetrations = 4;      // the bullet goes through at most this many objects and bodies together
    const float MinDamageLeft = 0.1f;   // with less damage left the bullet gets stuck in the object

    // a body (player, bot, training target) works like an object of this strength (damage loss per metre),
    // its thickness along the shot is kept between these values
    const float BodyStrength = 1f;
    const float BodyMinThickness = 0.3f;
    const float BodyMaxThickness = 1f;

    // players hit by the current bullet: each one is hit once, even if the bullet crosses several of his colliders
    static readonly List<Health> hitBodies = new List<Health>();

    // the shot with penetration: the bullet goes through objects and bodies and loses damage in each of them,
    // the loss is strength * thickness / penetration power of the weapon (see Penetrable).
    // The shot info (for effects) describes the first player hit, or the point where the bullet stopped.
    void FireThrough(Vector3 direction, bool noScope, bool inAir, ref ShotInfo shot)
    {
        RaycastHit[] hits = Physics.RaycastAll(muzzle.position, direction, range,
                                               Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        float power = data != null ? data.penetration : 0f;
        float left = 1f;          // part of the damage the bullet still has
        int passed = 0;           // objects and bodies the bullet went through
        float behind = 0f;        // distance along the shot where the bullet left the last object or body
        bool hitPlayer = false;   // the shot info already describes the first player hit
        hitBodies.Clear();

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];

            // the muzzle is inside this collider (a single Raycast does not see it either)
            if (hit.distance <= 0f) continue;

            Health target = hit.collider.GetComponentInParent<Health>();
            if (target != null)
            {
                // the shooter's own body, another collider of a player who was already hit, a dead player
                if (target == owner || target.IsDead || hitBodies.Contains(target)) continue;
                hitBodies.Add(target);

                bool friendly = owner != null && target.team == owner.team;

                // without friendly fire a teammate stops the bullet and takes no damage
                if (friendly && !FriendlyFire)
                {
                    if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, target, false, passed);
                    return;
                }

                // the second player and the next ones are hit through something, so it is a wallbang for them
                float multiplier = ZoneMultiplier(ZoneHit(hits, i, target), out bool head);
                if (friendly) multiplier *= FriendlyFireScale;
                target.TakeDamage(damage * multiplier * left, owner, head, noScope, inAir, passed > 0, data);

                bool first = !hitPlayer;
                if (first)
                {
                    StopShot(ref shot, hit.point, hit.normal, target, head, passed);
                    hitPlayer = true;
                }

                // the bullet goes on through the body like through an object;
                // the damage loss uses the clamped thickness, the overlap with the next objects uses the real one
                float depth = BodyThickness(hit, direction);
                bool goesOn = power > 0f && passed < MaxPenetrations;
                if (goesOn)
                {
                    float bodyThickness = Mathf.Clamp(depth, BodyMinThickness, BodyMaxThickness);
                    left *= Mathf.Max(0f, 1f - BodyStrength * bodyThickness / power);
                    goesOn = left >= MinDamageLeft;
                }

                // blood on the second and next bodies, where the bullet goes in and out (AnyShot shows the first one)
                if (!first) AnyPenetration?.Invoke(hit.point, hit.normal, hit.point + direction * depth, direction);
                if (!goesOn) return;

                passed++;
                behind = Mathf.Max(behind, hit.distance + depth);
                continue;
            }

            // a wall, a box, the floor
            float strength = Penetrable.StrengthOf(hit.collider);
            bool canPass = power > 0f && !Penetrable.IsImpenetrable(strength) && passed < MaxPenetrations;

            // the exit: cast back at this collider from a point MaxThickness behind the entry;
            // if that point is still inside the object, there is no exit and the object is too thick
            Ray back = new Ray(hit.point + direction * MaxThickness, -direction);
            if (!canPass || !hit.collider.Raycast(back, out RaycastHit exit, MaxThickness + 0.01f) || exit.distance <= 0.001f)
            {
                if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, null, false, passed);
                return;
            }

            float exitDistance = hit.distance + Mathf.Max(0f, MaxThickness - exit.distance);

            // objects that overlap: only the part behind the previous object counts, an object fully inside it is skipped
            if (exitDistance <= behind) continue;
            float thickness = exitDistance - Mathf.Max(hit.distance, behind);

            left *= Mathf.Max(0f, 1f - strength * thickness / power);
            if (left < MinDamageLeft)
            {
                if (!hitPlayer) StopShot(ref shot, hit.point, hit.normal, null, false, passed);
                return;
            }

            passed++;
            behind = exitDistance;
            AnyPenetration?.Invoke(hit.point, hit.normal, exit.point, exit.normal);
        }

        // went through everything (or hit nothing) and flew on
        if (!hitPlayer) shot.wallbang = passed > 0;
    }

    // the collider that decides the body part: the first collider of this player along the shot that has a HitZone,
    // otherwise the collider the bullet went in through (then the height of the hit decides)
    static RaycastHit ZoneHit(RaycastHit[] hits, int first, Health target)
    {
        for (int i = first; i < hits.Length; i++)
        {
            if (hits[i].distance <= 0f || hits[i].collider.GetComponent<HitZone>() == null) continue;
            if (hits[i].collider.GetComponentInParent<Health>() == target) return hits[i];
        }
        return hits[first];
    }

    // how far the bullet goes inside a body collider: cast back at it from a point behind the entry, like for objects
    static float BodyThickness(RaycastHit hit, Vector3 direction)
    {
        const float reach = BodyMaxThickness * 2f;
        Ray back = new Ray(hit.point + direction * reach, -direction);
        if (hit.collider.Raycast(back, out RaycastHit exit, reach + 0.01f) && exit.distance > 0.001f)
            return reach - exit.distance;
        return BodyMaxThickness;
    }

    // the bullet stopped at this point (in an object or in a player)
    static void StopShot(ref ShotInfo shot, Vector3 point, Vector3 normal, Health target, bool head, int passed)
    {
        shot.end = point;
        shot.normal = normal;
        shot.hit = true;
        shot.headshot = head;
        shot.target = target;
        shot.wallbang = passed > 0;

        Debug.DrawLine(shot.origin, point, Color.red, 0.1f);
    }

    // right click with the knife
    public bool TrySecondary()
    {
        return IsMelee && !IsDrawing && Melee(true);
    }

    bool Melee(bool stab)
    {
        if (Time.time < nextShotTime) return false;
        nextShotTime = Time.time + (stab ? data.secondaryInterval : 1f / Mathf.Max(fireRate, 0.01f));

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        bool inAir = movement != null ? !movement.IsGrounded : ExternalInAir;
        Vector3 origin = muzzle.position;
        Vector3 dir = muzzle.forward;

        ShotInfo shot = new ShotInfo
        {
            origin = origin,
            end = origin + dir * data.meleeRange,
            shooter = owner,
            muzzle = muzzle,
            melee = true
        };

        if (Physics.SphereCast(origin, 0.2f, dir, out RaycastHit hit, data.meleeRange))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;

            // a teammate gets hurt only with friendly fire
            if (target != null && owner != null && target != owner && (!friendly || FriendlyFire) && !target.IsDead)
            {
                float dmg = stab ? data.secondaryDamage : damage;

                Vector3 toTarget = target.transform.position - owner.transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.01f && Vector3.Dot(target.transform.forward, toTarget.normalized) > 0.5f)
                    dmg = stab ? data.backstabDamage : data.backstabDamage * 0.5f;

                if (friendly) dmg *= FriendlyFireScale;
                target.TakeDamage(dmg, owner, false, false, inAir, false, data);
            }

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.target = target;
        }

        AnyShot?.Invoke(shot);
        return true;
    }
}