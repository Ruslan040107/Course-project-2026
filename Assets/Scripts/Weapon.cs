using UnityEngine;

public class Weapon : MonoBehaviour
{
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

        if (Physics.Raycast(muzzle.position, direction, out RaycastHit hit, range))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            bool friendly = owner != null && target != null && target.team == owner.team;

            if (target != null && target != owner && !friendly)
                target.TakeDamage(damage);

            Debug.DrawLine(muzzle.position, hit.point, Color.red, 0.1f);
        }
        return true;
    }
}