using System.Collections.Generic;
using UnityEngine;

public class MapBounds : MonoBehaviour
{
    public float halfSizeX = 25.5f;
    public float halfSizeZ = 25.5f;
    public float minY = -3f;
    public float maxY = 30f;
    public float graceTime = 1.5f;

    readonly Dictionary<Health, float> outside = new Dictionary<Health, float>();

    void FixedUpdate()
    {
        foreach (Health h in Health.All)
        {
            if (h.IsDead || !h.gameObject.activeInHierarchy)
            {
                outside.Remove(h);
                continue;
            }

            Vector3 p = h.transform.position;
            bool isOut = Mathf.Abs(p.x) > halfSizeX || Mathf.Abs(p.z) > halfSizeZ || p.y < minY || p.y > maxY;

            if (!isOut)
            {
                outside.Remove(h);
                continue;
            }

            outside.TryGetValue(h, out float t);
            t += Time.fixedDeltaTime;

            if (t >= graceTime)
            {
                h.TakeDamage(h.maxHealth * 10f);
                outside.Remove(h);
            }
            else
            {
                outside[h] = t;
            }
        }
    }
}