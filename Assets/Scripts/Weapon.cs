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
}

public class Weapon : MonoBehaviour
{
    public static event System.Action<ShotInfo> AnyShot;
    public event System.Action ReloadStarted;

    PlayerController movement;

    [Header("Info")]
    public string weaponName = "Rifle";

    [Header("Damage")]
    public Transform muzzle;
    public float damage = 25f;
    public float headMultiplier = 3f;
    public float legMultiplier = 0.75f;
    [Range(0f, 1f)] public float headZone = 0.82f;
    [Range(0f, 1f)] public float legZone = 0.38f;

    [Header("Fire")]
    public float fireRate = 8f;
    public float range = 100f;
    public float spreadAngle = 0f;
    public bool automatic = true;

    [Header("Ammo")]
    public int magazineSize = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 2.2f;

    float nextShotTime;
    float reloadEnd;
    int ammo;
    int reserve;
    bool reloading;
    Health owner;

    public int Ammo { get { FinishReload(); return ammo; } }
    public int Reserve { get { FinishReload(); return reserve; } }
    public bool IsReloading { get { FinishReload(); return reloading; } }
    public float ReloadProgress => reloading ? Mathf.Clamp01(1f - (reloadEnd - Time.time) / reloadTime) : 1f;

    void Awake()
    {
        movement = GetComponentInParent<PlayerController>();
        owner = GetComponentInParent<Health>();
        ResetAmmo();
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
        if (reloading || ammo >= magazineSize || reserve <= 0) return;

        reloading = true;
        reloadEnd = Time.time + reloadTime;
        ReloadStarted?.Invoke();
    }

    public bool TryFire()
    {
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

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        float spreadNow = spreadAngle * (movement != null ? movement.SpreadMultiplier : 1f);
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
            muzzle = muzzle
        };

        if (Physics.Raycast(muzzle.position, direction, out RaycastHit hit, range))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;
            bool head = false;

            if (target != null && target != owner && !friendly)
            {
                float multiplier = 1f;

                Bounds b = hit.collider.bounds;
                float t = Mathf.InverseLerp(b.min.y, b.max.y, hit.point.y);

                if (t >= headZone)
                {
                    multiplier = headMultiplier;
                    head = true;
                }
                else if (t <= legZone)
                {
                    multiplier = legMultiplier;
                }

                target.TakeDamage(damage * multiplier, owner, head);
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
}