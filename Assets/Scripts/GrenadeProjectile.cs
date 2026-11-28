using System.Collections.Generic;
using UnityEngine;

// Physical grenade projectile: Rigidbody + SphereCollider, honest ballistic trajectory,
// realistic bounce damping off walls and floor, and type-specific detonation rules.
public class GrenadeProjectile : MonoBehaviour
{
    public GrenadeData Data { get; private set; }
    public Health Owner { get; private set; }
    public WeaponData MimicWeapon { get; private set; }

    GrenadeSystem system;
    Rigidbody rb;
    SphereCollider col;
    PhysicsMaterial physMat;

    float bornAt;
    float stillTime;
    float lastBounceTime = -10f;
    bool bounced;
    bool detonated;
    readonly List<Collider> ignoredOwnerCols = new List<Collider>();
    bool ownerCollisionsRestored;

    public void Init(GrenadeSystem sys, Health owner, GrenadeData data, Vector3 velocity, WeaponData mimicWeapon)
    {
        system = sys;
        Owner = owner;
        Data = data;
        MimicWeapon = mimicWeapon;
        bornAt = Time.time;

        col = gameObject.AddComponent<SphereCollider>();
        col.radius = 0.08f;

        physMat = new PhysicsMaterial("GrenadePhysMat")
        {
            bounciness = Mathf.Clamp01(data.bounciness),
            dynamicFriction = 0.55f,
            staticFriction = 0.65f,
            bounceCombine = PhysicsMaterialCombine.Maximum,
            frictionCombine = PhysicsMaterialCombine.Average
        };
        col.material = physMat;

        rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = 0.42f;
        rb.linearDamping = data.drag;
        rb.angularDamping = 0.6f;
        rb.useGravity = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.linearVelocity = velocity;
        rb.angularVelocity = new Vector3(Random.Range(-10f, 10f), Random.Range(-8f, 8f), Random.Range(-10f, 10f));

        // Do not collide with the thrower right out of the hand
        if (owner != null)
        {
            foreach (Collider c in owner.GetComponentsInChildren<Collider>(true))
            {
                if (c != null && c.enabled)
                {
                    Physics.IgnoreCollision(col, c, true);
                    ignoredOwnerCols.Add(c);
                }
            }
        }
    }

    void OnDestroy()
    {
        if (system != null) system.UnregisterProjectile(this);
        if (physMat != null) Destroy(physMat);
    }

    void OnCollisionEnter(Collision collision)
    {
        if (detonated || Data == null) return;

        ContactPoint cp = collision.GetContact(0);
        Vector3 n = cp.normal;
        float speed = collision.relativeVelocity.magnitude;

        // Fire grenades (Molotov / Incendiary) bounce off steep walls and detonate on floor contact
        if (Data.type == GrenadeType.Fire && n.y > 0.7f)
        {
            TriggerDetonate(cp.point, n);
            return;
        }

        // Dampen tangential velocity on floor hits so grenades roll and settle naturally
        if (n.y > 0.6f && rb != null)
        {
            Vector3 v = rb.linearVelocity;
            Vector3 horiz = new Vector3(v.x, 0f, v.z) * 0.72f;
            rb.linearVelocity = new Vector3(horiz.x, v.y, horiz.z);
        }

        bounced = true;
        if (speed > 1.2f && Time.time - lastBounceTime > 0.08f)
        {
            lastBounceTime = Time.time;
            GrenadeSystem.NotifyBounce(cp.point, speed, Data);
        }
    }

    void FixedUpdate()
    {
        if (detonated || Data == null) return;

        float age = Time.time - bornAt;

        if (!ownerCollisionsRestored && age > 0.25f)
        {
            ownerCollisionsRestored = true;
            for (int i = 0; i < ignoredOwnerCols.Count; i++)
                if (ignoredOwnerCols[i] != null && col != null) Physics.IgnoreCollision(col, ignoredOwnerCols[i], false);
            ignoredOwnerCols.Clear();
        }

        switch (Data.type)
        {
            case GrenadeType.HE:
            case GrenadeType.Flash:
                if (age >= Data.fuseTime) TriggerDetonate(transform.position, Vector3.up);
                break;

            case GrenadeType.Fire:
                // Safety mid-air expiration if thrown high into open sky
                if (age >= Data.airFuseTime)
                {
                    detonated = true;
                    Destroy(gameObject);
                }
                break;

            case GrenadeType.Smoke:
                // Smoke hitting active fire immediately detonates and extinguishes it
                if (GrenadeSystem.IsPointInActiveFire(transform.position))
                {
                    TriggerDetonate(transform.position, Vector3.up);
                    return;
                }
                WaitForStop(age);
                break;

            case GrenadeType.Decoy:
                WaitForStop(age);
                break;
        }
    }

    void WaitForStop(float age)
    {
        float speed = rb != null ? rb.linearVelocity.magnitude : 0f;
        if (bounced && speed < 0.45f) stillTime += Time.fixedDeltaTime;
        else stillTime = 0f;

        if (stillTime >= 0.2f || age >= 6.5f)
            TriggerDetonate(transform.position, Vector3.up);
    }

    void TriggerDetonate(Vector3 pos, Vector3 normal)
    {
        if (detonated) return;
        detonated = true;
        if (system != null) system.Detonate(this, pos, normal);
        Destroy(gameObject);
    }
}
