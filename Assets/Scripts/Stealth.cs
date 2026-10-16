using UnityEngine;

public class Stealth : MonoBehaviour
{
    [Range(0, 3)] public int level = 0;
    public float revealTime = 1f;
    public float speedPenaltyPerLevel = 0.1f;
    public float radiusFactor = 0.5f;

    float revealUntil;

    public bool Revealed => Time.time < revealUntil;

    public void Reveal()
    {
        revealUntil = Time.time + revealTime;
    }

    public float DetectionRadius(float baseRadius)
    {
        if (Revealed) return baseRadius;
        return baseRadius * Mathf.Pow(radiusFactor, level);
    }

    public float SpeedFactor()
    {
        return 1f - speedPenaltyPerLevel * level;
    }
}