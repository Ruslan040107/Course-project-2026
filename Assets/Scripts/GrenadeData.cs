using UnityEngine;

public enum GrenadeType { HE, Flash, Smoke, Fire, Decoy }

public enum ThrowStrength { High, Mid, Low }

[CreateAssetMenu(menuName = "Game/Grenade Data", fileName = "NewGrenade")]
public class GrenadeData : ScriptableObject
{
    [Header("Identity")]
    public string grenadeName = "HE Grenade";
    public string displayName = "Осколочная граната";
    public GrenadeType type = GrenadeType.HE;
    [Tooltip("-1 = both teams, 0 = T only, 1 = CT only")] public int team = -1;
    [TextArea(2, 4)] public string description = "";

    [Header("Economy")]
    public int price = 300;
    public int killReward = 300;
    [Tooltip("Maximum carried of this grenade type")] public int maxCarry = 1;

    [Header("Gameplay")]
    [Tooltip("Fuse time in seconds (0 = detonates on ground impact like Molotov/Incendiary)")]
    public float fuseTime = 1.6f;
    [Tooltip("Airborne safety timeout in seconds for impact grenades (Molotov/Incendiary)")]
    public float airFuseTime = 2.0f;
    [Tooltip("Effect radius in meters (HE blast, Smoke sphere, Fire spread, Flash range, Decoy noise)")]
    public float radius = 9f;
    [Tooltip("Max HE damage, Fire damage per second, or Decoy pop damage")]
    public float damage = 98f;
    [Tooltip("Active duration in seconds (Smoke ~18s, Fire ~7s, Flash max blind ~3.5s, Decoy ~15s)")]
    public float duration = 0f;

    [Header("Physics & Movement")]
    public float throwSpeedHigh = 18.5f;
    public float throwSpeedMid = 12.5f;
    public float throwSpeedLow = 7.2f;
    public float throwUpBoost = 3.2f;
    [Range(0f, 1f)] public float bounciness = 0.42f;
    public float drag = 0.12f;
    [Tooltip("1 = full speed (CS units: 245 / 250)")] public float moveSpeedFactor = 245f / 250f;
    public float drawTime = 0.45f;
    public float pinPullTime = 0.22f;

    [Header("Prefabs & Audio (optional, falls back to Resources)")]
    public GameObject worldPrefab;
    public AudioClip throwSound;
    public AudioClip bounceSound;
    public AudioClip detonateSound;
    public AudioClip loopSound;

    public float SpeedFor(ThrowStrength s)
    {
        switch (s)
        {
            case ThrowStrength.Low: return throwSpeedLow;
            case ThrowStrength.Mid: return throwSpeedMid;
            default: return throwSpeedHigh;
        }
    }

    public float UpBoostFor(ThrowStrength s)
    {
        switch (s)
        {
            case ThrowStrength.Low: return throwUpBoost * 0.45f;
            case ThrowStrength.Mid: return throwUpBoost * 0.72f;
            default: return throwUpBoost;
        }
    }
}
