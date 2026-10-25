using UnityEditor;
using UnityEngine;

// Tools > Create Weapon Assets: creates or updates all 36 weapons in Assets/Resources/Weapons.
// The numbers are the user's table (Docs/weapons_table.md). An existing asset is loaded by its path and updated,
// so its GUID (and every reference to it in scenes) stays the same.
public static class WeaponAssetsCreator
{
    const string Folder = "Assets/Resources/Weapons";

    // WeaponData.team: -1 = both teams, 0 = only T, 1 = only CT
    const int Both = -1, T = 0, CT = 1;

    static int count;

    [MenuItem("Tools/Create Weapon Assets")]
    public static void Create()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Resources", "Weapons");
        count = 0;

        // Columns as in the table: name, category, team, price $, kill reward $, damage, recoil %, rpm,
        // effective range m, armour penetration %, reload s, magazine, reserve, speed (CS units, 250 = full speed),
        // damage without armour: head, chest/arms, stomach, legs.
        // Then our own values: draw time s, spread degrees (bots only; the player's accuracy is the Aim table below),
        // penetration power (see Penetrable).

        // pistols
        WeaponData glock = Make("Glock-18",      WeaponCategory.Pistol, T,    200, 300, 30, 84, 400, 20, 47,     2.2f, 20, 120, 240, 118, 29, 37, 22,  0.5f, 1.0f,  0.8f);
        WeaponData usp   = Make("USP-S",         WeaponCategory.Pistol, CT,   200, 300, 35, 69, 352, 21, 50.5f,  2.2f, 12, 24,  240, 140, 34, 43, 26,  0.5f, 0.8f,  1.0f);
        Make("P2000",                            WeaponCategory.Pistol, CT,   200, 300, 35, 73, 352, 22, 50.5f,  2.2f, 13, 52,  240, 140, 35, 43, 26,  0.5f, 0.85f, 1.0f);
        Make("P250",                             WeaponCategory.Pistol, Both, 300, 300, 38, 69, 400, 14, 64,     2.2f, 13, 26,  240, 151, 37, 47, 28,  0.5f, 1.0f,  1.3f);
        WeaponData duals = Make("Dual Berettas", WeaponCategory.Pistol, Both, 300, 300, 38, 69, 500, 24, 57.5f,  3.8f, 30, 120, 240, 152, 38, 42, 28,  0.6f, 1.1f,  1.1f);
        WeaponData cz    = Make("CZ75-Auto",     WeaponCategory.Pistol, Both, 500, 300, 31, 65, 600, 11, 77.65f, 2.7f, 12, 12,  240, 123, 30, 38, 23,  0.6f, 1.2f,  1.4f);
        Make("Tec-9",                            WeaponCategory.Pistol, T,    500, 300, 33, 65, 500, 19, 90.15f, 2.5f, 18, 90,  240, 132, 33, 41, 24,  0.5f, 1.1f,  1.6f);
        Make("Five-SeveN",                       WeaponCategory.Pistol, CT,   500, 300, 32, 65, 400, 22, 91,     2.2f, 20, 100, 240, 123, 30, 38, 25,  0.5f, 0.9f,  1.6f);
        WeaponData r8    = Make("R8 Revolver",   WeaponCategory.Pistol, Both, 600, 300, 86, 4,  85,  60, 93.2f,  2.3f, 8,  8,   220, 344, 86, 107, 64, 0.6f, 0.8f,  2.0f);
        Make("Desert Eagle",                     WeaponCategory.Pistol, Both, 700, 300, 73, 12, 267, 35, 93.2f,  2.2f, 7,  35,  230, 250, 62, 77, 46,  0.6f, 0.8f,  2.0f);

        // Glock-18: right click switches to a 3 shot burst at 1200 rpm
        Burst(glock, 3, 1200f);
        usp.silenced = true;
        duals.dual = true;
        cz.automatic = true;
        // R8: left click pulls the hammer for 0.4 s first, right click fans at 150 rpm (less accurate);
        // 220 speed, 180 while firing
        r8.fireDelay = 0.4f;
        r8.altFireRate = 150f / 60f;
        r8.altSpreadMultiplier = 4f;
        r8.firingMoveSpeedFactor = 180f / 250f;

        // SMGs
        Make("MAC-10",                 WeaponCategory.SMG, T,    1050, 600, 29, 80, 800, 15, 57.5f, 2.6f,  30, 100, 240, 114, 28, 35, 21, 0.6f,  1.5f, 0.85f);
        Make("UMP-45",                 WeaponCategory.SMG, Both, 1200, 600, 35, 76, 666, 11, 65,    3.5f,  25, 100, 230, 140, 35, 43, 25, 0.7f,  1.4f, 1.2f);
        Make("MP9",                    WeaponCategory.SMG, CT,   1250, 600, 26, 80, 857, 16, 60,    2.1f,  30, 120, 240, 104, 26, 32, 19, 0.6f,  1.3f, 0.9f);
        Make("PP-Bizon",               WeaponCategory.SMG, Both, 1400, 600, 27, 80, 750, 14, 57.5f, 2.4f,  64, 120, 240, 108, 27, 33, 20, 0.7f,  1.6f, 0.8f);
        Make("MP7",                    WeaponCategory.SMG, Both, 1500, 600, 29, 84, 800, 14, 62.5f, 3.1f,  30, 120, 220, 110, 28, 35, 21, 0.7f,  1.3f, 1.0f);
        WeaponData mp5 = Make("MP5-SD", WeaponCategory.SMG, Both, 1500, 600, 27, 85, 750, 15, 62.5f, 2.97f, 30, 120, 235, 107, 26, 33, 20, 0.7f,  1.2f, 0.9f);
        Make("P90",                    WeaponCategory.SMG, Both, 2350, 300, 26, 61, 857, 15, 69,    3.3f,  50, 100, 230, 103, 25, 32, 19, 0.75f, 1.5f, 1.0f);
        mp5.silenced = true;

        // shotguns: the damage and the zones are for one pellet; reload = the time for the whole magazine
        WeaponData nova   = Make("Nova",      WeaponCategory.Shotgun, Both, 1050, 900, 26, 4, 68,  3, 50, 3.7f, 8, 32, 220, 106, 26, 32, 19, 0.8f, 0.5f, 0.5f);
        WeaponData sawed  = Make("Sawed-Off", WeaponCategory.Shotgun, T,    1100, 900, 32, 3, 71,  2, 75, 3.2f, 7, 32, 210, 128, 32, 40, 24, 0.8f, 0.5f, 0.5f);
        WeaponData mag7   = Make("MAG-7",     WeaponCategory.Shotgun, CT,   1300, 900, 30, 4, 71,  5, 75, 2.4f, 5, 32, 225, 120, 30, 37, 22, 0.8f, 0.5f, 0.5f);
        WeaponData xm1014 = Make("XM1014",    WeaponCategory.Shotgun, Both, 2000, 900, 20, 4, 171, 3, 80, 2.8f, 7, 32, 215, 80,  20, 25, 15, 0.8f, 0.5f, 0.5f);
        // pellets, pellet spread in degrees, one shell at a time (0.4 s each) or a whole magazine (MAG-7)
        Shotgun(nova, 9, 3.5f, true);
        Shotgun(sawed, 8, 5f, true);
        Shotgun(mag7, 8, 3f, false);
        Shotgun(xm1014, 6, 4f, true);   // semi-automatic, 171 rpm; the others are pump action

        // machine guns
        Make("Negev", WeaponCategory.MachineGun, Both, 1700, 300, 35, 76, 800, 13, 75, 5.7f, 150, 300, 150, 140, 35, 43, 26, 1.3f, 1.8f, 2.2f);
        Make("M249",  WeaponCategory.MachineGun, Both, 5200, 300, 32, 73, 750, 16, 80, 5.7f, 100, 200, 195, 128, 32, 40, 24, 1.3f, 1.8f, 2.2f);

        // rifles
        Make("Galil AR",                   WeaponCategory.Rifle, T,  1800, 300, 30, 76, 666, 23, 77.5f, 3.0f, 35, 90, 215, 119, 29, 37, 22, 0.9f, 0.9f,  2.1f);
        WeaponData famas = Make("FAMAS",   WeaponCategory.Rifle, CT, 2050, 300, 30, 80, 666, 21, 70,    3.3f, 25, 90, 220, 120, 30, 37, 24, 0.9f, 0.9f,  2.0f);
        Make("AK-47",                      WeaponCategory.Rifle, T,  2700, 300, 36, 69, 600, 36, 77.5f, 2.5f, 30, 90, 215, 143, 35, 44, 26, 0.9f, 0.8f,  2.2f);
        WeaponData m4a1s = Make("M4A1-S",  WeaponCategory.Rifle, CT, 2900, 300, 33, 73, 600, 28, 70,    3.1f, 20, 80, 225, 132, 32, 41, 24, 1.0f, 0.7f,  2.0f);
        Make("M4A4",                       WeaponCategory.Rifle, CT, 2900, 300, 33, 76, 666, 28, 70,    3.1f, 30, 90, 225, 131, 32, 40, 24, 0.9f, 0.7f,  2.0f);
        WeaponData sg553 = Make("SG 553",  WeaponCategory.Rifle, T,  3000, 300, 30, 69, 666, 36, 100,   2.8f, 30, 90, 210, 120, 30, 37, 22, 1.0f, 0.5f,  2.4f);
        WeaponData aug   = Make("AUG",     WeaponCategory.Rifle, CT, 3300, 300, 28, 73, 666, 49, 90,    3.8f, 30, 90, 220, 112, 28, 35, 21, 1.0f, 0.45f, 2.3f);

        // FAMAS: automatic, right click switches to a 3 shot burst at 800 rpm
        Burst(famas, 3, 800f);
        m4a1s.silenced = true;
        // SG 553 and AUG: a small optic (no black mask); the spread above is the scoped one, without the scope it is x1.6
        Scope(sg553, new float[] { 45f }, 1.6f, 0f, 150f / 210f, 1.5f, true);
        Scope(aug, new float[] { 45f }, 1.6f, 0f, 150f / 220f, 1.5f, true);

        // snipers (the spread is the scoped one)
        WeaponData ssg   = Make("SSG 08",  WeaponCategory.Sniper, Both, 1700, 300, 88,  46, 48,  47, 85,    3.7f, 10, 90, 230, 352, 88,  110, 66, 1.2f, 0.05f, 2.8f);
        WeaponData awp   = Make("AWP",     WeaponCategory.Sniper, Both, 4750, 100, 115, 3,  41,  96, 97.5f, 3.6f, 5,  30, 200, 460, 115, 143, 86, 1.2f, 0.05f, 3.5f);
        WeaponData scar  = Make("SCAR-20", WeaponCategory.Sniper, CT,   5000, 300, 80,  65, 240, 66, 82.5f, 3.1f, 20, 90, 215, 319, 79,  99,  59, 1.3f, 0.05f, 3.0f);
        WeaponData g3sg1 = Make("G3SG1",   WeaponCategory.Sniper, T,    5000, 300, 80,  65, 240, 92, 82.5f, 4.7f, 20, 90, 215, 319, 79,  99,  59, 1.3f, 0.05f, 3.0f);

        ssg.boltAction = true;
        awp.boltAction = true;
        // zoom FOVs, spread x without the scope, smallest no-scope spread, speed x while scoped, spread while scoped and running
        Scope(ssg, new float[] { 40f, 15f }, 16f, 0.8f, 1f, 2f, false);      // light: good without the scope, no slowdown in it
        Scope(awp, new float[] { 40f, 10f }, 40f, 1.2f, 0.5f, 3f, false);
        Scope(scar, new float[] { 40f, 15f }, 30f, 1.2f, 0.56f, 3f, false);   // auto-snipers are semi-automatic
        Scope(g3sg1, new float[] { 40f, 15f }, 30f, 1.2f, 0.56f, 3f, false);
        g3sg1.firingMoveSpeedFactor = 150f / 250f;

        // knife: left slash 40, quick follow-up slashes 25 (90 in the back), 150 rpm = every 0.4 s;
        // right stab 65 (180 in the back) every 1 s
        WeaponData knife = Make("Knife", WeaponCategory.Melee, Both, 0, 1500, 40, 100, 150, 2, 85, 0, 0, 0, 250, 40, 40, 40, 40, 0.5f, 0f, 0f);
        knife.melee = true;
        knife.automatic = true;
        knife.recoil = 0f;
        knife.range = 2f;
        knife.meleeRange = 2f;
        knife.secondaryDamage = 65f;
        knife.secondaryInterval = 1f;
        knife.backstabDamage = 180f;
        knife.followUpDamage = 25f;

        // Zeus x27: one charge that kills (500) up to 2 m, it is charged again after 30 s
        WeaponData zeus = Make("Zeus x27", WeaponCategory.Gear, Both, 200, 100, 500, 100, 60, 2, 100, 0, 1, 0, 230, 500, 500, 500, 500, 0.6f, 0.3f, 0f);
        zeus.taser = true;
        zeus.rechargeTime = 30f;
        zeus.range = 2f;

        // The player's accuracy and recoil, like CS 2 (bots keep 'spread' from the rows above, see Weapon.BotSpread).
        // stand / crouch: the largest angle (degrees) of the first shot standing still / crouched, most shots land much
        // closer (CS 'accurate range' ~ 0.15 m / tan(stand): AK-47 30 m, M4A4 37 m, USP-S 33 m, MP9 13 m), for scoped
        // weapons the scoped value (x unscopedSpreadMultiplier without the scope); move: degrees at full run;
        // per shot: degrees every shot adds; recovery: s until that falls to 10%; pattern: the spray shape;
        // climb: degrees up of the 10th bullet = family size (AK 5, M4 4.2, SMG 3, MG 4, pistol / shotgun / sniper 6)
        // x the table's recoil multiplier
        //       name             stand   crouch  move  per shot recovery pattern                    climb
        Aim("Glock-18",           0.42f,  0.32f,  2.5f, 1.0f,  0.30f, RecoilPattern.Pistol,     3.94f);
        Aim("USP-S",              0.26f,  0.20f,  2.5f, 1.2f,  0.33f, RecoilPattern.Pistol,     5.38f);
        Aim("P2000",              0.30f,  0.23f,  2.5f, 1.2f,  0.33f, RecoilPattern.Pistol,     4.99f);
        Aim("P250",               0.48f,  0.37f,  2.8f, 1.5f,  0.35f, RecoilPattern.Pistol,     5.38f);
        Aim("Dual Berettas",      0.55f,  0.42f,  3.0f, 1.0f,  0.30f, RecoilPattern.Pistol,     5.38f);
        Aim("CZ75-Auto",          0.55f,  0.42f,  3.0f, 0.8f,  0.30f, RecoilPattern.Pistol,     5.76f);
        Aim("Tec-9",              0.50f,  0.38f,  2.5f, 1.2f,  0.30f, RecoilPattern.Pistol,     5.76f);
        Aim("Five-SeveN",         0.40f,  0.31f,  2.5f, 1.2f,  0.30f, RecoilPattern.Pistol,     5.76f);
        Aim("R8 Revolver",        0.60f,  0.46f,  3.5f, 2.5f,  0.60f, RecoilPattern.Pistol,     11.62f);   // hammer shot x0.5
        Aim("Desert Eagle",       0.30f,  0.23f,  4.0f, 2.5f,  0.55f, RecoilPattern.Pistol,     10.85f);
        Aim("MAC-10",             0.80f,  0.61f,  2.5f, 0.35f, 0.30f, RecoilPattern.SMG,        2.16f);
        Aim("UMP-45",             0.66f,  0.50f,  2.5f, 0.4f,  0.33f, RecoilPattern.SMG,        2.35f);
        Aim("MP9",                0.65f,  0.50f,  2.0f, 0.35f, 0.28f, RecoilPattern.SMG,        2.16f);
        Aim("PP-Bizon",           0.75f,  0.57f,  2.2f, 0.3f,  0.30f, RecoilPattern.SMG,        2.16f);
        Aim("MP7",                0.52f,  0.40f,  2.2f, 0.35f, 0.30f, RecoilPattern.SMG,        1.97f);
        Aim("MP5-SD",             0.50f,  0.38f,  2.2f, 0.33f, 0.30f, RecoilPattern.SMG,        1.92f);
        Aim("P90",                0.70f,  0.53f,  2.2f, 0.3f,  0.30f, RecoilPattern.SMG,        3.07f);
        Aim("Nova",               0.50f,  0.38f,  2.0f, 1.5f,  0.40f, RecoilPattern.Shotgun,    11.62f);   // the pellets spread around it
        Aim("Sawed-Off",          0.60f,  0.46f,  2.0f, 1.5f,  0.40f, RecoilPattern.Shotgun,    11.71f);
        Aim("MAG-7",              0.50f,  0.38f,  2.0f, 1.5f,  0.40f, RecoilPattern.Shotgun,    11.62f);
        Aim("XM1014",             0.60f,  0.46f,  2.0f, 1.0f,  0.35f, RecoilPattern.Shotgun,    11.62f);
        Aim("Negev",              0.95f,  0.72f,  7.0f, 0.25f, 0.50f, RecoilPattern.MachineGun, 3.14f);
        Aim("M249",               0.85f,  0.65f,  7.0f, 0.25f, 0.50f, RecoilPattern.MachineGun, 3.33f);
        Aim("Galil AR",           0.36f,  0.27f,  5.5f, 0.5f,  0.40f, RecoilPattern.AK,         3.92f);
        Aim("FAMAS",              0.36f,  0.27f,  5.5f, 0.45f, 0.40f, RecoilPattern.M4,         3.02f);
        Aim("AK-47",              0.29f,  0.22f,  6.0f, 0.45f, 0.43f, RecoilPattern.AK,         4.48f);
        Aim("M4A1-S",             0.21f,  0.16f,  5.0f, 0.4f,  0.42f, RecoilPattern.M4,         3.49f);
        Aim("M4A4",               0.23f,  0.175f, 5.0f, 0.4f,  0.42f, RecoilPattern.M4,         3.29f);
        Aim("SG 553",             0.18f,  0.14f,  6.0f, 0.45f, 0.45f, RecoilPattern.AK,         4.48f);    // scoped; x1.6 without
        Aim("AUG",                0.15f,  0.115f, 5.5f, 0.4f,  0.42f, RecoilPattern.M4,         3.49f);    // scoped; x1.6 without
        Aim("SSG 08",             0.075f, 0.056f, 3.5f, 1.0f,  0.35f, RecoilPattern.Sniper,     7.58f);    // scoped; x16 without
        Aim("AWP",                0.06f,  0.045f, 8.0f, 1.5f,  0.35f, RecoilPattern.Sniper,     11.71f);   // scoped; x40 without
        Aim("SCAR-20",            0.06f,  0.045f, 6.0f, 0.6f,  0.35f, RecoilPattern.Sniper,     5.76f);    // scoped; x30 without
        Aim("G3SG1",              0.06f,  0.045f, 6.0f, 0.6f,  0.35f, RecoilPattern.Sniper,     5.76f);
        Aim("Knife",              0f,     0f,     0f,   0f,    0.30f, RecoilPattern.None,       0f);
        Aim("Zeus x27",           0.30f,  0.30f,  1.0f, 0f,    0.30f, RecoilPattern.None,       0f);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log(count + " weapon assets are in " + Folder);
    }

    // one row of the table; everything that is not in the table is reset to the plain default,
    // the special parts (burst, shotgun, scope...) are set after it
    static WeaponData Make(string name, WeaponCategory category, int team, int price, int reward, float damage,
                           float recoilPercent, float rpm, float effectiveRange, float armorPercent, float reload,
                           int mag, int reserve, float speed, float head, float chest, float stomach, float legs,
                           float draw, float spread, float penetration)
    {
        string path = Folder + "/" + name + ".asset";
        WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(path);

        if (w == null)
        {
            w = ScriptableObject.CreateInstance<WeaponData>();
            AssetDatabase.CreateAsset(w, path);
        }

        w.weaponName = name;
        w.category = category;
        w.team = team;
        // viewModelPrefab is not touched: it may be set by hand

        w.price = price;
        w.killReward = reward;

        // zone multipliers = zone damage without armour / damage
        w.damage = damage;
        w.headMultiplier = head / damage;
        w.chestMultiplier = chest / damage;
        w.stomachMultiplier = stomach / damage;
        w.legMultiplier = legs / damage;
        w.armorPenetration = armorPercent / 100f;
        w.range = category == WeaponCategory.Sniper ? 300f : 250f;   // a bullet flies far, like CS (the desert diagonal is ~102 m)
        w.effectiveRange = effectiveRange;

        w.fireRate = rpm / 60f;
        w.automatic = category == WeaponCategory.SMG || category == WeaponCategory.Rifle || category == WeaponCategory.MachineGun;
        w.drawTime = draw;
        w.boltAction = false;
        w.silenced = false;
        w.dual = false;

        w.burstCount = 0;
        w.burstFireRate = 20f;

        w.pellets = 0;
        w.pelletSpread = 3f;
        w.shellReload = false;
        w.shellReloadTime = 0.4f;

        w.fireDelay = 0f;
        w.altFireRate = 0f;
        w.altSpreadMultiplier = 3f;

        w.taser = false;
        w.rechargeTime = 30f;

        w.spreadAngle = spread;
        // more 'recoil %' = better control = less kick: 100% -> 0.4, 0% -> 2.0
        w.recoil = 0.4f + 1.6f * (1f - recoilPercent / 100f);
        w.penetration = penetration;

        w.magazineSize = mag;
        w.reserveAmmo = reserve;
        w.reloadTime = reload;

        w.moveSpeedFactor = speed / 250f;
        w.firingMoveSpeedFactor = 0f;

        w.melee = false;
        w.meleeRange = 2f;
        w.secondaryDamage = 65f;
        w.secondaryInterval = 1f;
        w.backstabDamage = 180f;
        w.followUpDamage = 0f;

        w.scopeFov = new float[0];
        w.unscopedSpreadMultiplier = 1f;
        w.unscopedMinSpread = 1.2f;
        w.scopedMoveFactor = 0.5f;
        w.scopedMoveSpread = 3f;
        w.scopeIsOptic = false;

        EditorUtility.SetDirty(w);
        count++;
        return w;
    }

    // the player's accuracy and recoil pattern of a weapon made by Make above
    static void Aim(string name, float stand, float crouch, float move, float perShot, float recovery,
                    RecoilPattern pattern, float climb)
    {
        WeaponData w = AssetDatabase.LoadAssetAtPath<WeaponData>(Folder + "/" + name + ".asset");
        if (w == null)
        {
            Debug.LogWarning("Create Weapon Assets: no asset for " + name);
            return;
        }

        w.standInaccuracy = stand;
        w.crouchInaccuracy = crouch;
        w.moveInaccuracy = move;
        w.inaccuracyPerShot = perShot;
        w.recoveryTime = recovery;
        w.recoilPattern = pattern;
        w.recoilScale = climb;
        EditorUtility.SetDirty(w);
    }

    // right click switches to bursts of 'shots' at 'rpm' inside a burst
    static void Burst(WeaponData w, int shots, float rpm)
    {
        w.burstCount = shots;
        w.burstFireRate = rpm / 60f;
        EditorUtility.SetDirty(w);
    }

    static void Shotgun(WeaponData w, int pellets, float pelletSpread, bool shellByShell)
    {
        w.pellets = pellets;
        w.pelletSpread = pelletSpread;
        w.shellReload = shellByShell;
        w.shellReloadTime = 0.4f;
        EditorUtility.SetDirty(w);
    }

    static void Scope(WeaponData w, float[] fov, float unscopedMultiplier, float unscopedMinSpread,
                      float scopedMoveFactor, float scopedMoveSpread, bool optic)
    {
        w.scopeFov = fov;
        w.unscopedSpreadMultiplier = unscopedMultiplier;
        w.unscopedMinSpread = unscopedMinSpread;
        w.scopedMoveFactor = scopedMoveFactor;
        w.scopedMoveSpread = scopedMoveSpread;
        w.scopeIsOptic = optic;
        EditorUtility.SetDirty(w);
    }
}
