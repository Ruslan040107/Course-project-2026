using UnityEngine;

public enum WeaponCategory { Pistol, SMG, Rifle, Sniper, Melee }

[CreateAssetMenu(menuName = "Game/Weapon Data", fileName = "NewWeapon")]
public class WeaponData : ScriptableObject
{
    public string weaponName = "Weapon";
    public WeaponCategory category;

    [Header("Economy")]
    public int price = 0;
    public int killReward = 300;

    [Header("Damage")]
    public float damage = 30f;
    public float headMultiplier = 4f;
    public float legMultiplier = 0.75f;
    public float range = 100f;

    [Header("Fire")]
    [Tooltip("shots per second")] public float fireRate = 6f;
    public bool automatic = false;
    public float drawTime = 0.6f;

    [Header("Accuracy")]
    [Tooltip("base spread in degrees, the player stance multiplies it")] public float spreadAngle = 1f;
    [Tooltip("camera kick multiplier")] public float recoil = 1f;

    [Header("Ammo")]
    public int magazineSize = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 2.4f;

    [Header("Movement")]
    [Tooltip("1 = full speed")] public float moveSpeedFactor = 1f;

    [Header("Melee (knife)")]
    public bool melee;
    public float meleeRange = 2f;
    [Tooltip("right click stab")] public float secondaryDamage = 65f;
    public float secondaryInterval = 1f;
    [Tooltip("stab in the back, a slash in the back does half")] public float backstabDamage = 180f;

    [Header("Scope (leave empty if no scope)")]
    [Tooltip("camera FOV for each zoom level, e.g. 40 and 10")] public float[] scopeFov;
    [Tooltip("spread multiplier when not scoped")] public float unscopedSpreadMultiplier = 1f;
    [Tooltip("minimum spread in degrees when not scoped, so a no-scope shot is never perfectly accurate")] public float unscopedMinSpread = 1.2f;
    [Tooltip("speed multiplier while scoped")] public float scopedMoveFactor = 0.5f;
}