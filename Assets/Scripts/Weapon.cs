using UnityEngine;

public class Weapon : MonoBehaviour
{
    public Transform muzzle;
    public float damage = 25f;
    public float fireRate = 8f;
    public float range = 100f;
    public bool automatic = true;

    float nextShotTime;

    public bool TryFire()
    {
        if (Time.time < nextShotTime) return false;
        nextShotTime = Time.time + 1f / fireRate;

        if (Physics.Raycast(muzzle.position, muzzle.forward, out RaycastHit hit, range))
        {
            Health target = hit.collider.GetComponentInParent<Health>();
            if (target != null) target.TakeDamage(damage);

            Debug.DrawLine(muzzle.position, hit.point, Color.red, 0.1f);
        }
        return true;
    }
}