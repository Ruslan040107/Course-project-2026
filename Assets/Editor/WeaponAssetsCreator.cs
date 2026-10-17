using System.IO;
using UnityEditor;
using UnityEngine;

public static class WeaponAssetsCreator
{
    const string Folder = "Assets/Weapons";

    [MenuItem("Tools/Create Weapon Assets")]
    public static void Create()
    {
        Directory.CreateDirectory(Folder);

        // name, category, price, reward, damage, rate, auto, spread, recoil, mag, reserve, reload, move, draw
        Make("Glock-18", WeaponCategory.Pistol, 200, 300, 30f, 6.7f, false, 1.0f, 0.5f, 20, 120, 2.3f, 0.96f, 0.5f);
        Make("USP-S", WeaponCategory.Pistol, 200, 300, 35f, 5.9f, false, 0.6f, 0.6f, 12, 24, 2.2f, 0.96f, 0.5f);
        Make("Desert Eagle", WeaponCategory.Pistol, 700, 300, 63f, 4.5f, false, 0.8f, 2.0f, 7, 35, 2.2f, 0.92f, 0.6f);
        Make("MP9", WeaponCategory.SMG, 1250, 600, 26f, 14.3f, true, 1.2f, 0.6f, 30, 120, 2.1f, 0.96f, 0.6f);
        Make("AK-47", WeaponCategory.Rifle, 2700, 300, 36f, 10f, true, 0.8f, 1.2f, 30, 90, 2.4f, 0.86f, 0.9f);
        Make("M4A4", WeaponCategory.Rifle, 3100, 300, 33f, 11.1f, true, 0.7f, 0.9f, 30, 90, 3.1f, 0.90f, 0.9f);

        WeaponData awp = Make("AWP", WeaponCategory.Sniper, 4750, 100, 115f, 0.68f, false, 0.05f, 3.0f, 5, 30, 3.7f, 0.80f, 1.2f);
        awp.scopeFov = new float[] { 40f, 10f };
        awp.unscopedSpreadMultiplier = 40f;
        awp.scopedMoveFactor = 0.5f;
        EditorUtility.SetDirty(awp);

        WeaponData knife = Make("Knife", WeaponCategory.Melee, 0, 1500, 40f, 2f, true, 0f, 0f, 0, 0, 0f, 1.0f, 0.5f);
        knife.melee = true;
        knife.meleeRange = 2f;
        knife.secondaryDamage = 65f;
        knife.secondaryInterval = 1f;
        knife.backstabDamage = 180f;
        EditorUtility.SetDirty(knife);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Weapon assets are in " + Folder);
    }

    static WeaponData Make(string name, WeaponCategory category, int price, int reward, float damage,
                           float rate, bool automatic, float spread, float recoil,
                           int mag, int reserve, float reload, float move, float draw)
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
        w.price = price;
        w.killReward = reward;
        w.damage = damage;
        w.headMultiplier = 4f;
        w.legMultiplier = 0.75f;
        w.range = 100f;
        w.fireRate = rate;
        w.automatic = automatic;
        w.drawTime = draw;
        w.spreadAngle = spread;
        w.recoil = recoil;
        w.magazineSize = mag;
        w.reserveAmmo = reserve;
        w.reloadTime = reload;
        w.moveSpeedFactor = move;
        w.melee = false;
        w.scopeFov = new float[0];
        w.unscopedSpreadMultiplier = 1f;
        w.scopedMoveFactor = 0.5f;

        EditorUtility.SetDirty(w);
        return w;
    }
}