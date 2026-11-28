using UnityEditor;
using UnityEngine;

// Tools > Create Grenade Assets: creates or updates the 6 CS2-style grenade ScriptableObjects
// in Assets/Resources/Grenades, preserving existing GUIDs.
public static class GrenadeAssetsCreator
{
    const string Folder = "Assets/Resources/Grenades";
    const string ModelsFolder = "Assets/Resources/GrenadeModels";
    const string AudioFolder = "Assets/Resources/Audio/Grenades";

    const int Both = -1, T = 0, CT = 1;

    [MenuItem("Tools/Create Grenade Assets")]
    public static void Create()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Resources")) AssetDatabase.CreateFolder("Assets", "Resources");
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Resources", "Grenades");

        Make(
            name: "HE Grenade",
            display: "Осколочная граната",
            type: GrenadeType.HE,
            team: Both,
            price: 300,
            killReward: 300,
            fuseTime: 1.6f,
            airFuseTime: 0f,
            radius: 9f,
            damage: 98f,
            duration: 0f,
            bounciness: 0.42f,
            detonateClip: "he_explode_0",
            loopClip: null,
            desc: "Взрывается через 1,6 с и наносит осколочный урон в радиусе. Стены и укрытия блокируют взрыв."
        );

        Make(
            name: "Flashbang",
            display: "Световая граната",
            type: GrenadeType.Flash,
            team: Both,
            price: 200,
            killReward: 0,
            fuseTime: 1.5f,
            airFuseTime: 0f,
            radius: 25f,
            damage: 0f,
            duration: 3.5f,
            bounciness: 0.45f,
            detonateClip: "flash_explode",
            loopClip: "flash_ring",
            desc: "Ослепляет всех в прямой видимости. Сила и длительность зависят от расстояния и угла взгляда."
        );

        Make(
            name: "Smoke Grenade",
            display: "Дымовая граната",
            type: GrenadeType.Smoke,
            team: Both,
            price: 300,
            killReward: 0,
            fuseTime: 2.0f,
            airFuseTime: 0f,
            radius: 4.2f,
            damage: 0f,
            duration: 18f,
            bounciness: 0.36f,
            detonateClip: "smoke_bloom",
            loopClip: null,
            desc: "Создаёт плотную дымовую завесу на 18 с, перекрывает обзор и тушит огонь."
        );

        Make(
            name: "Molotov",
            display: "Коктейль Молотова",
            type: GrenadeType.Fire,
            team: T,
            price: 400,
            killReward: 300,
            fuseTime: 0f,
            airFuseTime: 2.0f,
            radius: 3.6f,
            damage: 40f,
            duration: 7f,
            bounciness: 0.25f,
            detonateClip: "molotov_break",
            loopClip: "fire_loop",
            desc: "Разбивается при ударе о пол и поджигает область на 7 с. Ограничивается стенами и тушится дымом."
        );

        Make(
            name: "Incendiary",
            display: "Зажигательная граната",
            type: GrenadeType.Fire,
            team: CT,
            price: 500,
            killReward: 300,
            fuseTime: 0f,
            airFuseTime: 2.0f,
            radius: 3.6f,
            damage: 40f,
            duration: 7f,
            bounciness: 0.38f,
            detonateClip: "incendiary_detonate",
            loopClip: "fire_loop",
            desc: "Взрывается на земле и заливает участок термитным пламенем на 7 с. Тушится дымовой гранатой."
        );

        Make(
            name: "Decoy Grenade",
            display: "Ложная граната",
            type: GrenadeType.Decoy,
            team: Both,
            price: 50,
            killReward: 300,
            fuseTime: 1.5f,
            airFuseTime: 0f,
            radius: 40f,
            damage: 5f,
            duration: 15f,
            bounciness: 0.44f,
            detonateClip: null,
            loopClip: null,
            desc: "После остановки имитирует выстрелы и отметку на радаре, отвлекая врагов и ботов."
        );

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("6 grenade assets created/updated in " + Folder);
    }

    static void Make(string name, string display, GrenadeType type, int team, int price, int killReward,
                     float fuseTime, float airFuseTime, float radius, float damage, float duration,
                     float bounciness, string detonateClip, string loopClip, string desc)
    {
        string path = Folder + "/" + name + ".asset";
        GrenadeData g = AssetDatabase.LoadAssetAtPath<GrenadeData>(path);
        if (g == null)
        {
            g = ScriptableObject.CreateInstance<GrenadeData>();
            AssetDatabase.CreateAsset(g, path);
        }

        g.grenadeName = name;
        g.displayName = display;
        g.type = type;
        g.team = team;
        g.price = price;
        g.killReward = killReward;
        g.maxCarry = 1;
        g.description = desc;

        g.fuseTime = fuseTime;
        g.airFuseTime = airFuseTime;
        g.radius = radius;
        g.damage = damage;
        g.duration = duration;

        g.throwSpeedHigh = 18.5f;
        g.throwSpeedMid = 12.5f;
        g.throwSpeedLow = 7.2f;
        g.throwUpBoost = 3.2f;
        g.bounciness = bounciness;
        g.drag = 0.12f;
        g.moveSpeedFactor = 245f / 250f;
        g.drawTime = 0.45f;
        g.pinPullTime = 0.22f;

        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelsFolder + "/" + name + ".fbx");
        if (model != null) g.worldPrefab = model;

        g.throwSound = LoadClip("throw");
        g.bounceSound = LoadClip("bounce_0");
        g.detonateSound = string.IsNullOrEmpty(detonateClip) ? null : LoadClip(detonateClip);
        g.loopSound = string.IsNullOrEmpty(loopClip) ? null : LoadClip(loopClip);

        EditorUtility.SetDirty(g);
    }

    static AudioClip LoadClip(string name)
    {
        return AssetDatabase.LoadAssetAtPath<AudioClip>(AudioFolder + "/" + name + ".wav");
    }
}
