using System.Collections.Generic;
using UnityEngine;

// A killed player does not vanish: a copy of his body falls over in the direction of the shot
// and stays on the floor until the next round.
// The bodies are on the Ignore Raycast layer, so bullets and bot vision go through them,
// and living players walk through them. CombatEffects adds this component (never in simulation).
public class Corpses : MonoBehaviour
{
    public int maxBodies = 12;
    [Tooltip("speed given to the upper body, m/s")] public float push = 2.5f;
    public float headshotPush = 4f;
    [Tooltip("after this time the body is frozen, so nothing can move it any more")] public float settleTime = 3f;

    const int IgnoreRaycastLayer = 2;

    class Body
    {
        public GameObject go;
        public Rigidbody rb;
        public float freezeAt;
    }

    readonly List<Body> bodies = new List<Body>();

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
        Health victim = k.victim;
        if (victim == null || !victim.gameObject.activeInHierarchy) return;

        GameObject go = new GameObject(victim.name + " (body)");
        go.layer = IgnoreRaycastLayer;
        go.transform.SetPositionAndRotation(victim.transform.position, victim.transform.rotation);

        // copy every visible mesh except the ones on the camera (the first person weapon)
        bool any = false;
        Bounds bounds = new Bounds();
        foreach (MeshRenderer r in victim.GetComponentsInChildren<MeshRenderer>())
        {
            if (!r.enabled || r.GetComponentInParent<Camera>() != null) continue;
            MeshFilter f = r.GetComponent<MeshFilter>();
            if (f == null || f.sharedMesh == null) continue;

            GameObject part = new GameObject(r.name);
            part.layer = IgnoreRaycastLayer;
            part.transform.SetParent(go.transform, false);
            part.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
            part.transform.localScale = r.transform.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = f.sharedMesh;
            part.AddComponent<MeshRenderer>().sharedMaterials = r.sharedMaterials;

            if (!any) bounds = r.bounds;
            else bounds.Encapsulate(r.bounds);
            any = true;
        }

        if (!any)
        {
            Destroy(go);
            return;
        }

        // a box does not roll after it falls, a capsule would
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size = new Vector3(bounds.size.x * 0.8f, bounds.size.y, bounds.size.z * 0.8f);

        foreach (Health h in Health.All)
            foreach (Collider c in h.GetComponentsInChildren<Collider>(true))
                Physics.IgnoreCollision(box, c);

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0.5f;
        rb.angularDamping = 1.5f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // push the upper body away from the killer, so the body falls in the direction of the shot
        Vector3 dir = k.killer != null ? victim.transform.position - k.killer.transform.position : -victim.transform.forward;
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.001f ? dir.normalized : -victim.transform.forward;
        dir = Quaternion.Euler(0f, Random.Range(-25f, 25f), 0f) * dir;

        Vector3 upper = bounds.center + Vector3.up * bounds.extents.y * 0.7f;
        rb.AddForceAtPosition(dir * (k.headshot ? headshotPush : push), upper, ForceMode.VelocityChange);

        bodies.Add(new Body { go = go, rb = rb, freezeAt = Time.time + settleTime });

        while (bodies.Count > maxBodies)
        {
            if (bodies[0].go != null) Destroy(bodies[0].go);
            bodies.RemoveAt(0);
        }
    }

    void Update()
    {
        foreach (Body b in bodies)
            if (b.rb != null && !b.rb.isKinematic && Time.time > b.freezeAt)
                b.rb.isKinematic = true;
    }

    void Clear()
    {
        foreach (Body b in bodies)
            if (b.go != null) Destroy(b.go);
        bodies.Clear();
    }
}
