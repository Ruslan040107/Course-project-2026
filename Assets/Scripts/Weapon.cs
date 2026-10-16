using UnityEngine;

public struct ShotInfo
{
    public Vector3 origin;
    public Vector3 end;
    public Vector3 normal;
    public bool hit;
    public Health shooter;
    public Health target;
    public Transform muzzle;
}

public class Weapon : MonoBehaviour
{
    public static event System.Action<ShotInfo> AnyShot;

    public Transform muzzle;
    public float damage = 25f;
    public float fireRate = 8f;
    public float range = 100f;
    public float spreadAngle = 0f;
    public bool automatic = true;

    float nextShotTime;
    Health owner;

    void Awake()
    {
        owner = GetComponentInParent<Health>();
    }

    public bool TryFire()
    {
        if (Time.time < nextShotTime) return false;
        nextShotTime = Time.time + 1f / fireRate;

        Stealth st = GetComponentInParent<Stealth>();
        if (st != null) st.Reveal();

        Quaternion spread = Quaternion.Euler(
            Random.Range(-spreadAngle, spreadAngle),
            Random.Range(-spreadAngle, spreadAngle),
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

            if (target != null && target != owner && !friendly)
                target.TakeDamage(damage, owner);

            shot.end = hit.point;
            shot.normal = hit.normal;
            shot.hit = true;
            shot.target = target;

            Debug.DrawLine(muzzle.position, hit.point, Color.red, 0.1f);
        }

        AnyShot?.Invoke(shot);
        return true;
    }
}