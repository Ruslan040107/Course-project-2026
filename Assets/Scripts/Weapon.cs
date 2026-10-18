using UnityEngine;

public struct ShotInfo
{
    public Vector3 origin;
    public Vector3 end;
    public Vector3 normal;
    public bool hit;
    public bool headshot;
    public Health shooter;
    public Health target;
    public Transform muzzle;
    public float recoil;
    public bool melee;
}

public class Weapon : MonoBehaviour
{
    public static event System.Action<ShotInfo> AnyShot;
    public event System.Action ReloadStarted;

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

    // everything except automatic rifles reloads by itself when the magazine gets empty
    bool AutoReloads => !(data != null && data.category == WeaponCategory.Rifle && data.automatic);

    // set by PlayerShoot; bots are always treated as scoped
    public bool Scoped { get; set; } = true;

    float nextShotTime;
    float reloadEnd;
    int ammo;
    int reserve;
    bool reloading;
    Health owner;

    public int Ammo { get { FinishReload(); return ammo; } }
    public int Reserve { get { FinishReload(); return reserve; } }
    public bool IsReloading { get { FinishReload(); return reloading; } }
    public float NextShotTime => nextShotTime;
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
    }

    public bool TryFire()
    {
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

        if (Physics.Raycast(muzzle.position, direction, out RaycastHit hit, range))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;
            bool head = false;

            if (target != null && target != owner && !friendly)
            {
                float multiplier = 1f;

                HitZone hitZone = hit.collider.GetComponent<HitZone>();

                if (hitZone != null)
                {
                    // character model with separate head, body and leg colliders
                    if (hitZone.zone == BodyZone.Head)
                    {
                        multiplier = headMultiplier;
                        head = true;
                    }
                    else if (hitZone.zone == BodyZone.Legs)
                    {
                        multiplier = legMultiplier;
                    }
                }
                else
                {
                    // one collider: decide by the height of the hit
                    Bounds b = hit.collider.bounds;
                    float t = Mathf.InverseLerp(b.min.y, b.max.y, hit.point.y);

                    if (t >= headZoneStart)
                    {
                        multiplier = headMultiplier;
                        head = true;
                    }
                    else if (t <= legZoneEnd)
                    {
                        multiplier = legMultiplier;
                    }
                }

                target.TakeDamage(damage * multiplier, owner, head, noScope, inAir);
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

    // right click with the knife
    public bool TrySecondary()
    {
        return IsMelee && Melee(true);
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

            if (target != null && owner != null && target != owner && !friendly && !target.IsDead)
            {
                float dmg = stab ? data.secondaryDamage : damage;

                Vector3 toTarget = target.transform.position - owner.transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.01f && Vector3.Dot(target.transform.forward, toTarget.normalized) > 0.5f)
                    dmg = stab ? data.backstabDamage : data.backstabDamage * 0.5f;

                target.TakeDamage(dmg, owner, false, false, inAir);
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