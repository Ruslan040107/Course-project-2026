using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Build Desert Map: makes Assets/Scenes/DesertMap.unity, a bigger map in the spirit of CS
// (three lanes, a tunnel on each side, mid doors, two yards with a platform, crates and containers).
// The scene is a copy of SampleScene, so the player, the rounds, the effects and the UI are the same;
// only the level geometry, the spawns and the map bounds change.
// The map is point symmetric: every object has a twin at (-x, -z), so both teams have the same chances.
// Textures: ambientCG (CC0), see CREDITS.md.
public static class DesertMapBuilder
{
    const string SourceScene = "Assets/Scenes/SampleScene.unity";
    const string ScenePath = "Assets/Scenes/DesertMap.unity";
    const string TexturePath = "Assets/Textures/Map";
    const string MaterialPath = "Assets/Materials/Desert";
    const string MeshPath = "Assets/Meshes/Desert";

    // map size: x from -40 to 40, z from -32 to 32
    const float HalfX = 40f;
    const float HalfZ = 32f;

    static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
    static Transform level;

    [MenuItem("Tools/Build Desert Map")]
    public static void Build()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        materials.Clear();
        meshes.Clear();
        Directory.CreateDirectory(MaterialPath);
        Directory.CreateDirectory(MeshPath);

        AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
        {
            Debug.LogError("Could not copy " + SourceScene);
            return;
        }

        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GameObject levelGo = GameObject.Find("Level");
        if (levelGo == null)
        {
            Debug.LogError("Object 'Level' not found in the scene.");
            return;
        }
        level = levelGo.transform;
        for (int i = level.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(level.GetChild(i).gameObject);

        BuildGeometry();
        PlaceSpawns();
        SetBounds();
        MoveExtras();
        BakeNavMesh(levelGo);

        EditorSceneManager.SaveScene(scene);
        AddToBuild();
        AssetDatabase.SaveAssets();
        Debug.Log("Desert map built: " + ScenePath);
    }

    // ---------------- layout ----------------

    static void BuildGeometry()
    {
        // floor: sand, a paved road through mid and along the lanes
        Box("Ground", new Vector3(0f, -0.25f, 0f), new Vector3(HalfX * 2f, 0.5f, HalfZ * 2f), "Ground079S", 6f, 0f);
        Box("Road mid", new Vector3(0f, 0.01f, 0f), new Vector3(12f, 0.02f, HalfZ * 2f), "PavingStones150", 4f, 0f, false);
        Twin("Road lane", new Vector3(-32f, 0.01f, 0f), new Vector3(10f, 0.02f, HalfZ * 2f - 8f), "PavingStones150", 4f, 0f, false);

        // outer walls
        Box("Wall N", new Vector3(0f, 3f, HalfZ + 0.5f), new Vector3(HalfX * 2f + 2f, 6f, 1f), "Bricks084", 3f);
        Box("Wall S", new Vector3(0f, 3f, -HalfZ - 0.5f), new Vector3(HalfX * 2f + 2f, 6f, 1f), "Bricks084", 3f);
        Box("Wall E", new Vector3(HalfX + 0.5f, 3f, 0f), new Vector3(1f, 6f, HalfZ * 2f), "Bricks084", 3f);
        Box("Wall W", new Vector3(-HalfX - 0.5f, 3f, 0f), new Vector3(1f, 6f, HalfZ * 2f), "Bricks084", 3f);

        // two buildings on each side split the map into three lanes; the gap between them is a tunnel
        Twin("Building 1", new Vector3(-16f, 3f, -7f), new Vector3(16f, 6f, 18f), "Plaster007", 3f);
        Twin("Building 2", new Vector3(-16f, 3f, 11f), new Vector3(16f, 6f, 10f), "Plaster007", 3f);
        Twin("Building 1 base", new Vector3(-16f, 0.5f, -7f), new Vector3(16.2f, 1f, 18.2f), "Bricks084", 3f);
        Twin("Building 2 base", new Vector3(-16f, 0.5f, 11f), new Vector3(16.2f, 1f, 10.2f), "Bricks084", 3f);
        Twin("Tunnel roof", new Vector3(-16f, 3.65f, 4f), new Vector3(16f, 0.3f, 4f), "CorrugatedSteel005", 3f);
        Twin("Tunnel floor", new Vector3(-16f, 0.01f, 4f), new Vector3(16f, 0.02f, 4f), "Tiles143", 2f, 0f, false);

        // mid doors: a wall across mid with a 3 m doorway
        Twin("Mid wall", new Vector3(-4.75f, 2f, 0f), new Vector3(6.5f, 4f, 1f), "Concrete046", 3f);
        Box("Mid lintel", new Vector3(0f, 3.5f, 0f), new Vector3(3f, 1f, 1f), "Concrete046", 3f);

        // lane doors: a wall across each outer lane with a 5 m doorway
        Twin("Lane wall 1", new Vector3(-37f, 2f, -6f), new Vector3(6f, 4f, 1f), "Concrete046", 3f);
        Twin("Lane wall 2", new Vector3(-26.5f, 2f, -6f), new Vector3(5f, 4f, 1f), "Concrete046", 3f);
        Twin("Lane lintel", new Vector3(-31.5f, 3.5f, -6f), new Vector3(5f, 1f, 1f), "Concrete046", 3f);

        // yard A (north-west) and its twin B (south-east): a platform with a ramp, a container, crates
        Twin("Platform", new Vector3(-20f, 0.75f, 22f), new Vector3(6f, 1.5f, 4f), "Concrete046", 3f);
        float slope = Mathf.Atan2(1.5f, 3f) * Mathf.Rad2Deg;
        float thick = 0.3f;
        float len = Mathf.Sqrt(3f * 3f + 1.5f * 1.5f);
        Vector3 top = new Vector3(-24.5f, 0.75f, 22f);
        Vector3 normal = new Vector3(-Mathf.Sin(slope * Mathf.Deg2Rad), Mathf.Cos(slope * Mathf.Deg2Rad), 0f);
        Twin("Ramp", top - normal * (thick * 0.5f), new Vector3(len, thick, 4f), "Concrete046", 3f, slope);
        Twin("Container", new Vector3(-30f, 1.3f, 22f), new Vector3(6f, 2.6f, 2.6f), "CorrugatedSteel005", 3f);
        Crate(new Vector3(-35f, 0f, 26f), 1.5f);
        Crate(new Vector3(-35f, 1.5f, 26f), 1.5f);
        Crate(new Vector3(-33.4f, 0f, 26f), 1.5f);
        Crate(new Vector3(-27f, 0f, 28f), 1.2f);

        // yard C (north-east) and its twin D (south-west): low walls and crates
        Twin("Low wall yard", new Vector3(20f, 0.6f, 24f), new Vector3(6f, 1.2f, 0.6f), "Concrete046", 3f);
        Crate(new Vector3(30f, 0f, 26f), 1.5f);
        Crate(new Vector3(32f, 0f, 21f), 1.5f);
        Crate(new Vector3(32f, 1.5f, 21f), 1.2f);
        Twin("Container yard", new Vector3(36f, 1.3f, 18f), new Vector3(2.6f, 2.6f, 6f), "CorrugatedSteel005", 3f);

        // lanes: pillars and crates
        Twin("Pillar 1", new Vector3(-32f, 2f, 8f), new Vector3(1f, 4f, 1f), "Bricks084", 3f);
        Twin("Pillar 2", new Vector3(-28f, 2f, -14f), new Vector3(1f, 4f, 1f), "Bricks084", 3f);
        Crate(new Vector3(-36f, 0f, 1f), 1.5f);
        Crate(new Vector3(-26f, 0f, 12f), 1.2f);

        // mid: crates and low walls
        Crate(new Vector3(4f, 0f, -9f), 1.5f);
        Crate(new Vector3(5.6f, 0f, -9f), 1.2f);
        Twin("Low wall mid", new Vector3(-3f, 0.6f, -18f), new Vector3(4f, 1.2f, 0.6f), "Concrete046", 3f);

        // spawn cover
        Twin("Low wall spawn", new Vector3(-14f, 0.6f, -22f), new Vector3(6f, 1.2f, 0.6f), "Concrete046", 3f);
    }

    static void Crate(Vector3 bottomCenter, float size)
    {
        Twin("Crate", bottomCenter + Vector3.up * size * 0.5f, Vector3.one * size, "WoodFloor043", size);
    }

    // the object and its twin turned by 180 degrees around the map centre
    static void Twin(string name, Vector3 center, Vector3 size, string texture, float tile, float slopeZ = 0f, bool collider = true)
    {
        Box(name + " a", center, size, texture, tile, slopeZ, collider, 0f);
        Box(name + " b", new Vector3(-center.x, center.y, -center.z), size, texture, tile, slopeZ, collider, 180f);
    }

    // ---------------- spawns and bounds ----------------

    static void PlaceSpawns()
    {
        Place("T Spawn", -27f);
        Place("CT Spawn", 27f);
    }

    static void Place(string rootName, float z)
    {
        GameObject root = GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogWarning(rootName + " not found");
            return;
        }
        int n = root.transform.childCount;
        for (int i = 0; i < n; i++)
        {
            float x = -8f + 16f * i / Mathf.Max(1, n - 1);
            Transform p = root.transform.GetChild(i);
            p.position = new Vector3(z < 0f ? x : -x, 1f, z);
            p.rotation = Quaternion.Euler(0f, z < 0f ? 0f : 180f, 0f);
        }
    }

    static void SetBounds()
    {
        MapBounds b = Object.FindAnyObjectByType<MapBounds>();
        if (b == null) return;
        b.halfSizeX = HalfX + 0.5f;
        b.halfSizeZ = HalfZ + 0.5f;
        b.wanderHalfX = HalfX - 3f;
        b.wanderHalfZ = HalfZ - 3f;
        EditorUtility.SetDirty(b);
    }

    // objects of the old arena that would stand inside the new buildings
    static void MoveExtras()
    {
        GameObject training = GameObject.Find("Training Target");
        if (training != null) training.transform.position = new Vector3(-20f, 1f, -27f);
        GameObject target = GameObject.Find("Target");
        if (target != null) target.transform.position = new Vector3(0f, 1f, 8f);
        GameObject player = GameObject.Find("Player");
        if (player != null) player.transform.position = new Vector3(-8f, 1f, -27f);

        Camera overview = null;
        foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            if (c.name == "OverviewCamera") overview = c;
        if (overview != null) overview.transform.position = new Vector3(0f, 70f, 0f);
    }

    static void BakeNavMesh(GameObject levelGo)
    {
        NavMeshSurface surface = levelGo.GetComponent<NavMeshSurface>();
        if (surface == null) surface = levelGo.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.Children;
        surface.BuildNavMesh();

        string path = "Assets/Scenes/DesertMap_NavMesh.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(surface.navMeshData, path);
        EditorUtility.SetDirty(surface);
    }

    static void AddToBuild()
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in list)
            if (s.path == ScenePath) return;
        list.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // ---------------- boxes, meshes, materials ----------------

    static GameObject Box(string name, Vector3 center, Vector3 size, string texture, float tile,
                          float slopeZ = 0f, bool collider = true, float turnY = 0f)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(level, false);
        go.transform.position = center;
        go.transform.rotation = Quaternion.Euler(0f, turnY, slopeZ);
        go.isStatic = true;

        go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size, tile);
        go.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial(texture);
        if (collider) go.AddComponent<BoxCollider>().size = size;
        go.AddComponent<Penetrable>().strength = PenetrationStrength(texture);
        return go;
    }

    // how hard it is to shoot through a box (see Penetrable): damage loss per meter
    static float PenetrationStrength(string texture)
    {
        switch (texture)
        {
            case "WoodFloor043": return 0.5f;          // crates
            case "CorrugatedSteel005": return 0.5f;    // hollow containers, thin roof
            case "Plaster007": return 1.5f;
            case "Concrete046": return 2.5f;
            case "Bricks084": return 3f;
            case "Ground079S":
            case "PavingStones150":
            case "Tiles143": return Penetrable.Impenetrable;   // floors
            default: return 2f;
        }
    }

    // a box of the given size whose texture repeats every "tile" meters on each face
    static Mesh BoxMesh(Vector3 size, float tile)
    {
        string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "box_{0:0.###}x{1:0.###}x{2:0.###}_t{3:0.##}", size.x, size.y, size.z, tile);
        if (meshes.TryGetValue(key, out Mesh cached)) return cached;

        string path = MeshPath + "/" + key + ".asset";
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        if (mesh == null)
        {
            Vector3 h = size * 0.5f;
            var verts = new List<Vector3>();
            var norms = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            void Face(Vector3 n, Vector3 u, Vector3 v, float su, float sv)
            {
                int start = verts.Count;
                Vector3 c = Vector3.Scale(n, h);
                Vector3 hu = u * su * 0.5f, hv = v * sv * 0.5f;
                verts.Add(c - hu - hv); verts.Add(c + hu - hv); verts.Add(c + hu + hv); verts.Add(c - hu + hv);
                for (int i = 0; i < 4; i++) norms.Add(n);
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(su / tile, 0f));
                uvs.Add(new Vector2(su / tile, sv / tile)); uvs.Add(new Vector2(0f, sv / tile));
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
                tris.Add(start); tris.Add(start + 3); tris.Add(start + 2);
            }

            Face(Vector3.forward, Vector3.left, Vector3.up, size.x, size.y);
            Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y);
            Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y);
            Face(Vector3.left, Vector3.back, Vector3.up, size.z, size.y);
            Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z);
            Face(Vector3.down, Vector3.right, Vector3.back, size.x, size.z);

            mesh = new Mesh { name = key };
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            AssetDatabase.CreateAsset(mesh, path);
        }
        meshes[key] = mesh;
        return mesh;
    }

    static Material GetMaterial(string texture)
    {
        if (materials.TryGetValue(texture, out Material cached)) return cached;

        // the footstep sound looks at the material name: Ground = sand, Wood = wood, the rest = concrete
        string name = texture.StartsWith("WoodFloor") ? "Desert_Wood" : texture.StartsWith("Ground") ? "Desert_Ground" : "Desert_" + texture;
        string path = MaterialPath + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }

        string dir = TexturePath + "/" + texture + "/" + texture;
        Texture2D color = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "_Color.jpg");
        Texture2D normal = LoadNormalMap(dir + "_NormalGL.jpg");

        mat.SetTexture("_BaseMap", color);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", texture.StartsWith("Corrugated") ? 0.35f : 0.12f);
        mat.SetFloat("_Metallic", texture.StartsWith("Corrugated") ? 0.6f : 0f);
        if (normal != null)
        {
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
            mat.EnableKeyword("_NORMALMAP");
        }
        EditorUtility.SetDirty(mat);
        materials[texture] = mat;
        return mat;
    }

    static Texture2D LoadNormalMap(string path)
    {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return null;
        if (ti.textureType != TextureImporterType.NormalMap)
        {
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
