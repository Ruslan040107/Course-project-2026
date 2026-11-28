using UnityEngine;

public class MapCallouts : MonoBehaviour
{
    public Vector3[] floorPoints;
    public string[] labels;

    public string At(Vector3 feet)
    {
        float closest = 56.25f;
        string label = null;
        for (int i = 0; i < floorPoints.Length; i++)
        {
            Vector3 delta = floorPoints[i] - feet;
            if (Mathf.Abs(delta.y) > 1.9f) continue;
            delta.y *= 2.5f;
            float distance = delta.sqrMagnitude;
            if (distance >= closest) continue;
            closest = distance;
            label = labels[i];
        }
        return label;
    }
}
