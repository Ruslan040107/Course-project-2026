using UnityEngine;

// new values are added at the END, so the numbers saved in the existing assets keep their meaning
public enum WeaponCategory { Pistol, SMG, Rifle, Sniper, Melee, Shotgun, MachineGun, Gear }

// which hand slot a weapon goes to (like CS: 1 primary, 2 pistol, 3 knife / gear)
public enum WeaponSlot { Primary, Secondary, Knife, Gear }

// the shape of the spray (Weapon.PatternPoint has the points); new values are added at the END
public enum RecoilPattern { None, AK, M4, SMG, Pistol, Shotgun, MachineGun, Sniper }

[CreateAssetMenu(menuName = "Game/Weapon Data", fileName = "NewWeapon")]
public class WeaponData : ScriptableObject
{
    public string weaponName = "Weapon";
    public WeaponCategory category;
    [Tooltip("-1 = both teams can buy it, 0 = only T, 1 = only CT")] public int team = -1;

    [Header("Model in the hands (optional)")]
    [Tooltip("your own first person model; empty = the model built from simple shapes. " +
             "Barrel along +Z, Y up, real size in meters, origin at the grip. " +
             "Optional empty children: Muzzle, Eject, Mag, Slide (or Bolt), Hands")]
    public GameObject viewModelPrefab;

    [Header("Economy")]
    public int price = 0;
    public int killReward = 300;

    [Header("Damage")]
    [Tooltip("base damage without armour (the 'chest and arms' value is about damage x chestMultiplier)")]
    public float damage = 30f;
    public float headMultiplier = 4f;
    [Tooltip("chest and arms")] public float chestMultiplier = 1f;
    public float stomachMultiplier = 1.25f;
    public float legMultiplier = 0.75f;
    [Tooltip("0..1, part of the damage that goes through armour (for the economy step)")] [Range(0f, 1f)] public float armorPenetration = 0.775f;
    [Tooltip("the longest distance a bullet flies, meters")] public float range = 100f;
    [Tooltip("'Дальность поражения': up to this distance the damage is full, then it falls off, meters")] public float effectiveRange = 30f;

    [Header("Fire")]
    [Tooltip("shots per second (rpm / 60)")] public float fireRate = 6f;
    public bool automatic = false;
    public float drawTime = 0.6f;
    [Tooltip("bolt action rifle (AWP, SSG 08): the bolt is cycled after every shot")] public bool boltAction;
    public bool silenced;
    [Tooltip("two pistols, one in each hand (Dual Berettas)")] public bool dual;

    [Header("Burst mode (Glock-18, FAMAS): right click switches it")]
    [Tooltip("shots in one burst, 0 = the weapon has no burst mode")] public int burstCount;
    [Tooltip("shots per second inside a burst")] public float burstFireRate = 20f;

    [Header("Shotgun")]
    [Tooltip("pellets in one shell, 0 or 1 = a single bullet; damage is per pellet")] public int pellets;
    [Tooltip("spread of the pellets, degrees")] public float pelletSpread = 3f;
    [Tooltip("reloads one shell at a time (Nova, XM1014, Sawed-Off)")] public bool shellReload;
    [Tooltip("time for one shell")] public float shellReloadTime = 0.4f;

    [Header("Revolver R8")]
    [Tooltip("left click: the hammer is pulled for this time before the shot")] public float fireDelay;
    [Tooltip("right click fanning: shots per second, 0 = no alternative fire")] public float altFireRate;
    public float altSpreadMultiplier = 3f;

    [Header("Zeus (taser)")]
    public bool taser;
    [Tooltip("seconds until the taser is charged again")] public float rechargeTime = 30f;

    [Header("Accuracy")]
    [Tooltip("BOTS only: base spread in degrees, x the bot's multipliers (the bots' accuracy of the research, unchanged); " +
             "the player uses the 'Player accuracy' values below")] public float spreadAngle = 1f;
    [Tooltip("kick multiplier of the model and the camera ('Отдача' in % is the opposite: more % = less kick)")] public float recoil = 1f;
    [Tooltip("penetration power: how much 'strength x meters' of objects a bullet can go through (see Penetrable)")]
    public float penetration = 1.5f;

    [Header("Player accuracy (like CS 2)")]
    [Tooltip("the largest angle (degrees) the first shot goes off the crosshair when standing still; most shots land " +
             "much closer to the middle. With a scope it is the scoped value")] public float standInaccuracy = 0.3f;
    [Tooltip("the same when crouching")] public float crouchInaccuracy = 0.22f;
    [Tooltip("degrees added at full running speed (part of it when walking, x1.5 in the air)")] public float moveInaccuracy = 5f;
    [Tooltip("degrees added by every shot; they go away when the shooting stops")] public float inaccuracyPerShot = 0.4f;
    [Tooltip("seconds for the inaccuracy of the shots to fall to 10%")] public float recoveryTime = 0.4f;
    [Tooltip("the spray: every spray the bullets go the same way, shot by shot (the camera shows about half of it)")]
    public RecoilPattern recoilPattern = RecoilPattern.M4;
    [Tooltip("size of the pattern: how far up (degrees) the 10th bullet of a spray goes")] public float recoilScale = 3f;

    [Header("Ammo")]
    public int magazineSize = 30;
    public int reserveAmmo = 90;
    public float reloadTime = 2.4f;

    [Header("Movement")]
    [Tooltip("1 = full speed (CS units: speed / 250)")] public float moveSpeedFactor = 1f;
    [Tooltip("speed factor while firing (R8, G3SG1); 0 = same as moveSpeedFactor")] public float firingMoveSpeedFactor;

    [Header("Melee (knife)")]
    public bool melee;
    public float meleeRange = 2f;
    [Tooltip("right click stab")] public float secondaryDamage = 65f;
    public float secondaryInterval = 1f;
    [Tooltip("stab in the back, a slash in the back does half")] public float backstabDamage = 180f;
    [Tooltip("a slash soon after the previous slash (CS: the first one 40, the next ones 25); 0 = every slash does 'damage'")]
    public float followUpDamage;

    [Header("Scope (leave empty if no scope)")]
    [Tooltip("camera FOV for each zoom level, e.g. 40 and 10")] public float[] scopeFov;
    [Tooltip("spread multiplier when not scoped")] public float unscopedSpreadMultiplier = 1f;
    [Tooltip("minimum spread in degrees when not scoped, so a no-scope shot is never perfectly accurate")] public float unscopedMinSpread = 1.2f;
    [Tooltip("speed multiplier while scoped")] public float scopedMoveFactor = 0.5f;
    [Tooltip("spread in degrees while scoped and running at full speed, smaller when walking slowly")] public float scopedMoveSpread = 3f;
    [Tooltip("the scope is a red dot / small optic (SG 553, AUG): no black mask, the gun stays visible")] public bool scopeIsOptic;

    // the hand slot of this weapon
    public WeaponSlot Slot
    {
        get
        {
            switch (category)
            {
                case WeaponCategory.Pistol: return WeaponSlot.Secondary;
                case WeaponCategory.Melee: return WeaponSlot.Knife;
                case WeaponCategory.Gear: return WeaponSlot.Gear;
                default: return WeaponSlot.Primary;
            }
        }
    }

    public bool IsShotgun => pellets > 1;
    public bool HasScope => scopeFov != null && scopeFov.Length > 0;
}
