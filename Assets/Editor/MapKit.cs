using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Shared library of the map builders (Borgo, Porto): the same way of building as DesertMapBuilder, plus full Euler
// rotations, point-mirrored twins with their own texture, cylinders, stairs, Poly Haven props, roofs, facades,
// bombsites and a sky. A builder starts the scene, places the level and finishes it:
//
//   if (!MapKit.Begin("Borgo", "BorgoMap")) return;
//   var rows = MapKit.Table(@"Ground|box|(0, -0.25, 0)|(96, 0.5, 76)|0|PavingStones119|yes|100|walk|-|2.3
//                  House mid west|box|(-4.5, 4.25, 18.5)|(11, 8.5, 21)|0|PaintedPlaster018 [PaintedPlaster013]|yes|1.5|block|T|3");
//   MapKit.Roof(MapKit.RowNamed(rows, "House mid west"));
//   // visual steps; the invisible ramp collider is the table's "stairs collider" row, so collider = false
//   MapKit.Stairs("A stairs", new Vector3(33, 0, 32), new Vector3(36, 1.2f, 32), 4f, 5, "Concrete012", 3f, false, true);
//   MapKit.PropSolid("Bench", "painted_wooden_bench", new Vector3(-6, 0, 6.9f), 180f, new Vector3(1.16f, 0.9f, 0.5f), 0.5f, false, true);
//   MapKit.Spawns(ctPoints); MapKit.Bombsite("Bombsite A", centre, size); MapKit.SetBounds(48.5f, 38.5f, 45f, 35f);
//   MapKit.Overview(new Vector3(0, 80, 0)); MapKit.Extras(player, trainingTarget, target);
//   MapKit.Sky(MapKit.SkySettings.Borgo());
//   MapKit.Finish();    // NavMesh, save, build list
//
// Units are metres, Unity axes (+z north). A twin is the copy turned by 180 degrees around the map centre:
// centre (-x, y, -z), rotation (rx, ry + 180, rz), named "<name> a" / "<name> b" (as in the research JSON).
// Materials <Map>_<ID> keep the footstep rule of GameAudio.Surface: "wood" / "plank" = wood, "ground" / "sand" = sand,
// the rest = concrete. Textures: ambientCG (Assets/Textures/Map), props and skies: Poly Haven (CC0, see CREDITS.md).
public static class MapKit
{
    // what the NavMesh does with an object: Walk = baked as walkable, Block = Not Walkable (no islands on top of
    // buildings, containers, crates), None = left out of the bake (decals, decor)
    public enum Nav { Walk, Block, None }

    // a collider: none, a normal one, or an invisible one (renderer disabled, e.g. the ramp over stairs)
    public enum Col { No, Yes, Invisible }

    // faces of a box in its own axes: North +z, East +x, South -z, West -x
    [System.Flags]
    public enum Faces { None = 0, North = 1, East = 2, South = 4, West = 8, All = 15 }

    // one line of a builder table (see Table)
    public class Row
    {
        public string name, kind = "box", texture, twinTexture;
        public Vector3 center, size, euler;
        public Col col = Col.Yes;
        public float pen = Penetrable.Impenetrable, tile = 3f;
        public Nav nav = Nav.Block;
        public bool twin;
        public int count = 1;       // models in a "prop:<slug> xN" row
    }

    // sky, sun, ambient light and fog of a map (maps_report section 7)
    public class SkySettings
    {
        public string hdr;                      // file in Assets/Textures/Sky
        public float rotation;                  // Skybox/Panoramic _Rotation
        public float exposure = 1f;
        public Color tint = new Color(0.5f, 0.5f, 0.5f, 1f);
        public Vector3 sunEuler = new Vector3(50f, -30f, 0f);
        public Color sunColor = Color.white;
        public float sunIntensity = 2f;
        public Color ambientSky = new Color(0.42f, 0.48f, 0.58f);
        public Color ambientEquator = new Color(0.38f, 0.35f, 0.30f);
        public Color ambientGround = new Color(0.20f, 0.17f, 0.14f);
        public bool fog = true;
        public float fogStart = 70f, fogEnd = 400f;
        public Color fogColor = new Color(0.87f, 0.88f, 0.90f);

        // deep blue sky with cumulus, afternoon sun from the west-south-west
        public static SkySettings Borgo()
        {
            return new SkySettings
            {
                hdr = "kloofendal_48d_partly_cloudy_puresky_2k.hdr", rotation = 234.3f,
                sunEuler = new Vector3(47.8f, 70.3f, 0f), sunColor = new Color(1f, 0.94f, 0.84f), sunIntensity = 2.2f,
                ambientSky = new Color(0.42f, 0.48f, 0.58f), ambientEquator = new Color(0.38f, 0.35f, 0.30f), ambientGround = new Color(0.20f, 0.17f, 0.14f),
                fogStart = 70f, fogEnd = 400f, fogColor = new Color(0.87f, 0.88f, 0.90f),
            };
        }

        // hazy sky with more clouds, warm morning sun from the east-south-east
        public static SkySettings Porto()
        {
            return new SkySettings
            {
                hdr = "sunflowers_puresky_2k.hdr", rotation = 16f,
                sunEuler = new Vector3(43.1f, 290f, 0f), sunColor = new Color(1f, 0.91f, 0.78f), sunIntensity = 1.9f,
                ambientSky = new Color(0.38f, 0.42f, 0.48f), ambientEquator = new Color(0.34f, 0.34f, 0.33f), ambientGround = new Color(0.17f, 0.16f, 0.15f),
                fogStart = 60f, fogEnd = 300f, fogColor = new Color(0.78f, 0.80f, 0.82f),
            };
        }
    }

    const string SourceScene = "Assets/Scenes/SampleScene.unity";
    const string TexturePath = "Assets/Textures/Map";
    public const int NotWalkableArea = 1;
    public const int IgnoreRaycastLayer = 2;

    // the map being built
    public static string Map { get; private set; }
    public static string SceneName { get; private set; }
    public static string ScenePath { get; private set; }
    public static Transform Level { get; private set; }
    public static string MaterialPath { get { return "Assets/Materials/" + Map; } }
    public static string MeshPath { get { return "Assets/Meshes/" + Map; } }

    static readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    static readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
    static readonly Dictionary<string, Vector2> surfaces = new Dictionary<string, Vector2>();
    static readonly HashSet<string> used = new HashSet<string>();
    static readonly Dictionary<string, Bounds> propBounds = new Dictionary<string, Bounds>();

    // ---------------- scene ----------------

    // Assets/Scenes/<sceneName>.unity as a fresh copy of SampleScene (player, rounds, effects, UI are the same),
    // with the children of Level removed. false if something is missing (the error is in the log).
    public static bool Begin(string map, string sceneName)
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;

        Map = map;
        SceneName = sceneName;
        ScenePath = "Assets/Scenes/" + sceneName + ".unity";
        materials.Clear();
        meshes.Clear();
        surfaces.Clear();
        used.Clear();
        propBounds.Clear();
        Folder(MaterialPath);
        Folder(MeshPath);

        AssetDatabase.DeleteAsset(ScenePath);
        if (!AssetDatabase.CopyAsset(SourceScene, ScenePath))
        {
            Debug.LogError("MapKit: could not copy " + SourceScene);
            return false;
        }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GameObject levelGo = GameObject.Find("Level");
        if (levelGo == null)
        {
            Debug.LogError("MapKit: object 'Level' not found in the scene.");
            return false;
        }
        Level = levelGo.transform;
        for (int i = Level.childCount - 1; i >= 0; i--)
            Object.DestroyImmediate(Level.GetChild(i).gameObject);
        return true;
    }

    // NavMesh, save the scene, add it to the build list; meshes and materials of this map that were not used any more
    // are deleted from Assets/Meshes/<Map> and Assets/Materials/<Map>
    public static void Finish()
    {
        BakeNavMesh();
        Save();
        Prune(MeshPath);
        Prune(MaterialPath);
        AssetDatabase.SaveAssets();
        int colliders = 0, renderers = 0;
        foreach (Collider c in Level.GetComponentsInChildren<Collider>(true)) colliders++;
        foreach (Renderer r in Level.GetComponentsInChildren<Renderer>(true)) renderers++;
        Debug.Log("MapKit: " + Map + " built: " + ScenePath + ", " + Level.childCount + " objects, "
                  + renderers + " renderers, " + colliders + " colliders");
    }

    public static void Save()
    {
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
        AddToBuild();
    }

    public static void AddToBuild()
    {
        var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in list)
            if (s.path == ScenePath) return;
        list.Add(new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = list.ToArray();
    }

    // NavMeshSurface on Level, collects the render meshes of its children (agent radius 0.5, height 2, slope 45,
    // climb 0.75 from the project settings) -> Assets/Scenes/<Scene>_NavMesh.asset
    public static void BakeNavMesh()
    {
        NavMeshSurface surface = Level.GetComponent<NavMeshSurface>();
        if (surface == null) surface = Level.gameObject.AddComponent<NavMeshSurface>();
        surface.collectObjects = CollectObjects.Children;
        surface.useGeometry = UnityEngine.AI.NavMeshCollectGeometry.RenderMeshes;
        surface.BuildNavMesh();

        string path = "Assets/Scenes/" + SceneName + "_NavMesh.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(surface.navMeshData, path);
        EditorUtility.SetDirty(surface);
    }

    // ---------------- boxes and cylinders ----------------

    // a static box under Level: its own mesh with the texture repeating every "tile" metres on each face,
    // rotation Euler(x, y, z) (Unity order: a ramp rising towards +z is (-angle, 0, 0)).
    // texture: an ambientCG id ("PaintedPlaster018"), "color:#rrggbb", or "prop:<slug>[/<part>][ xN]" (see PropBox).
    // A collider gets Penetrable(pen) (bullet damage lost per metre; 100 = never through).
    public static GameObject Box(string name, Vector3 center, Vector3 size, Vector3 euler, string texture, float tile,
                                 Col col = Col.Yes, float pen = Penetrable.Impenetrable, Nav nav = Nav.Block)
    {
        if (IsProp(texture, out string slug, out string part, out int count))
            return PropBox(name, slug, center, size, euler.y, pen, false, count, nav, part);

        GameObject go = Make(name, center, Quaternion.Euler(euler));
        go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size, tile);
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetMaterial(texture);
        if (col != Col.No)
        {
            go.AddComponent<BoxCollider>().size = size;
            go.AddComponent<Penetrable>().strength = pen;
        }
        Invisible(go, col);
        SetNav(go, col == Col.Invisible ? Nav.None : nav);
        return go;
    }

    // the box and its twin; twinTexture (if given) is the texture of the copy
    public static GameObject[] Twin(string name, Vector3 center, Vector3 size, Vector3 euler, string texture, float tile,
                                    Col col = Col.Yes, float pen = Penetrable.Impenetrable, Nav nav = Nav.Block, string twinTexture = null)
    {
        return new[]
        {
            Box(name + " a", center, size, euler, texture, tile, col, pen, nav),
            Box(name + " b", Mirror(center), size, MirrorEuler(euler), twinTexture ?? texture, tile, col, pen, nav),
        };
    }

    // an upright cylinder (24 sides) of diameter size.x by size.z and height size.y, the texture wrapped round it
    // (u = arc length / tile, v = height / tile); the collider is a convex MeshCollider of the same mesh
    public static GameObject Cylinder(string name, Vector3 center, Vector3 size, Vector3 euler, string texture, float tile,
                                      Col col = Col.Yes, float pen = Penetrable.Impenetrable, Nav nav = Nav.Block)
    {
        if (IsProp(texture, out string slug, out string part, out int count))
            return PropBox(name, slug, center, size, euler.y, pen, true, count, nav, part);

        GameObject go = Make(name, center, Quaternion.Euler(euler));
        Mesh mesh = CylinderMesh(size, tile);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = GetMaterial(texture);
        if (col != Col.No)
        {
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;
            go.AddComponent<Penetrable>().strength = pen;
        }
        Invisible(go, col);
        SetNav(go, col == Col.Invisible ? Nav.None : nav);
        return go;
    }

    public static GameObject[] TwinCylinder(string name, Vector3 center, Vector3 size, Vector3 euler, string texture, float tile,
                                            Col col = Col.Yes, float pen = Penetrable.Impenetrable, Nav nav = Nav.Block, string twinTexture = null)
    {
        return new[]
        {
            Cylinder(name + " a", center, size, euler, texture, tile, col, pen, nav),
            Cylinder(name + " b", Mirror(center), size, MirrorEuler(euler), twinTexture ?? texture, tile, col, pen, nav),
        };
    }

    // An invisible collider alone (no renderer): e.g. the collider of a prop row, or of decor placed with Prop();
    // cylinder = convex cylinder of diameter size.x / size.z. nav Block marks the NavMesh under it Not Walkable with a
    // NavMeshModifierVolume 0.5 m (the agent radius) wider on each side, the same clearance a visible box gets;
    // Walk / None leave the NavMesh as it is.
    public static GameObject Blocker(string name, Vector3 center, Vector3 size, Vector3 euler, float pen, bool cylinder = false,
                                     Nav nav = Nav.Block)
    {
        GameObject go = Make(name, center, Quaternion.Euler(euler));
        if (cylinder)
        {
            MeshCollider mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = CylinderMesh(size, 1f);
            mc.convex = true;
        }
        else go.AddComponent<BoxCollider>().size = size;
        go.AddComponent<Penetrable>().strength = pen;
        if (nav == Nav.Block)
        {
            NavMeshModifierVolume v = go.AddComponent<NavMeshModifierVolume>();
            v.size = size + new Vector3(1f, 0.4f, 1f);
            v.center = new Vector3(0f, -0.2f, 0f);
            v.area = NotWalkableArea;
        }
        return go;
    }

    public static Vector3 Mirror(Vector3 p)
    {
        return new Vector3(-p.x, p.y, -p.z);
    }

    public static Vector3 MirrorEuler(Vector3 euler)
    {
        return new Vector3(euler.x, euler.y + 180f, euler.z);
    }

    // ---------------- builder tables ----------------

    // Builds the lines of a builder table in the format of maps_report.md:
    //   name | kind | centre (x y z) | size (x y z) | rotation | texture [texture of the twin] | collider | penetrable | nav | twin | tile
    //   kind: box | cyl;  rotation: 0 or (x, y, z);  texture: <ID> | color:#rrggbb | prop:<slug> | prop:<slug> x2 |
    //   prop:<slug>/<part> (one variant of a model, see Prop)
    //   collider: yes | no | yes, invisible;  nav: walk | block | none;  twin: T | -
    // Empty lines and lines starting with "//" are skipped. Returns the parsed rows (e.g. to put roofs on the blocks).
    public static List<Row> Table(string table)
    {
        List<Row> rows = ParseTable(table);
        foreach (Row r in rows) Build(r);
        return rows;
    }

    public static List<Row> ParseTable(string table)
    {
        var rows = new List<Row>();
        foreach (string raw in table.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            string[] f = line.Split('|');
            if (f.Length < 11)
            {
                Debug.LogError("MapKit: table line with " + f.Length + " columns instead of 11: " + line);
                continue;
            }
            for (int i = 0; i < f.Length; i++) f[i] = f[i].Trim();
            var r = new Row
            {
                name = f[0],
                kind = f[1].ToLowerInvariant(),
                center = Vec(f[2]),
                size = Vec(f[3]),
                euler = f[4] == "0" ? Vector3.zero : Vec(f[4]),
                pen = Num(f[7]),
                twin = f[9].ToUpperInvariant() == "T",
                tile = Num(f[10]),
            };
            string tex = f[5];
            int open = tex.IndexOf('[');
            if (open >= 0)
            {
                r.twinTexture = tex.Substring(open + 1).TrimEnd(']').Trim();
                tex = tex.Substring(0, open).Trim();
            }
            r.texture = tex;
            IsProp(tex, out _, out _, out r.count);
            string col = f[6].ToLowerInvariant();
            r.col = col.StartsWith("no") ? Col.No : col.Contains("invisible") ? Col.Invisible : Col.Yes;
            string nav = f[8].ToLowerInvariant();
            r.nav = nav == "walk" ? Nav.Walk : nav == "none" ? Nav.None : Nav.Block;
            rows.Add(r);
        }
        return rows;
    }

    // one row (and its twin when the row says T)
    public static GameObject[] Build(Row r)
    {
        bool cyl = r.kind.StartsWith("cyl");
        if (!r.twin)
            return new[] { cyl ? Cylinder(r.name, r.center, r.size, r.euler, r.texture, r.tile, r.col, r.pen, r.nav)
                               : Box(r.name, r.center, r.size, r.euler, r.texture, r.tile, r.col, r.pen, r.nav) };
        return cyl ? TwinCylinder(r.name, r.center, r.size, r.euler, r.texture, r.tile, r.col, r.pen, r.nav, r.twinTexture)
                   : Twin(r.name, r.center, r.size, r.euler, r.texture, r.tile, r.col, r.pen, r.nav, r.twinTexture);
    }

    // "(1.5, -2, 3)" or "1.5 -2 3"
    static Vector3 Vec(string s)
    {
        string[] p = s.Trim('(', ')', ' ').Split(new[] { ',', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3)
        {
            Debug.LogError("MapKit: not a vector: " + s);
            return Vector3.zero;
        }
        return new Vector3(Num(p[0]), Num(p[1]), Num(p[2]));
    }

    static float Num(string s)
    {
        return float.Parse(s.Trim().Replace('\u2212', '-'), CultureInfo.InvariantCulture);
    }

    // "prop:<slug>[/<part>][ xN]"
    static bool IsProp(string texture, out string slug, out string part, out int count)
    {
        slug = null;
        part = null;
        count = 1;
        if (texture == null || !texture.StartsWith("prop:")) return false;
        string[] p = texture.Substring(5).Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        slug = p[0];
        int slash = slug.IndexOf('/');
        if (slash >= 0)
        {
            part = slug.Substring(slash + 1);
            slug = slug.Substring(0, slash);
        }
        if (p.Length > 1 && p[1].StartsWith("x")) int.TryParse(p[1].Substring(1), out count);
        count = Mathf.Max(1, count);
        return true;
    }

    // ---------------- stairs ----------------

    // Stairs from the low point to the high point of the walking surface (centre line), "width" wide:
    // - visual steps in one mesh, no collider; the NavMesh is baked from them (rise 0.23-0.25 < climb 0.75), so bots
    //   walk them. Step i (1..n): size (width, rise * i, run), standing at the low point's height.
    // - collider = true: one invisible ramp collider 0.2 thick whose top runs from the low to the high point, for the
    //   player (CharacterController stepOffset 0.3 is too small for clean stairs). Its renderer is disabled but keeps
    //   the step material, so GameAudio plays wood footsteps on wooden stairs; it is left out of the NavMesh bake.
    //   Use collider = false when the table already has the "stairs collider" row (the same box).
    // twin = true builds the mirrored copy too ("<name> a" / "<name> b").
    public static void Stairs(string name, Vector3 low, Vector3 high, float width, int steps, string texture, float tile,
                              bool collider = true, bool twin = false)
    {
        if (twin)
        {
            Stairs(name + " a", low, high, width, steps, texture, tile, collider, false);
            Stairs(name + " b", Mirror(low), Mirror(high), width, steps, texture, tile, collider, false);
            return;
        }

        Vector3 flat = new Vector3(high.x - low.x, 0f, high.z - low.z);
        float length = flat.magnitude;
        float rise = high.y - low.y;
        if (length < 0.01f || steps < 1)
        {
            Debug.LogError("MapKit: stairs " + name + " have no length");
            return;
        }
        float yaw = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        float run = length / steps, step = rise / steps;

        // the steps in the stairs' own axes: +z up the stairs, origin at the low point
        var mb = new MeshBuild();
        for (int i = 1; i <= steps; i++)
            mb.Box(new Vector3(0f, step * i * 0.5f, run * (i - 0.5f)), new Vector3(width, step * i, run), Quaternion.identity, tile);
        string key = Key("steps", new Vector3(width, rise, length), tile) + "_n" + steps;
        GameObject go = Make(name + " steps", low, turn);
        go.AddComponent<MeshFilter>().sharedMesh = mb.Mesh(key);
        go.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial(texture);
        SetNav(go, Nav.Walk);

        if (!collider) return;
        float angle = Mathf.Atan2(rise, length) * Mathf.Rad2Deg;
        Quaternion rot = Quaternion.Euler(-angle, yaw, 0f);
        Vector3 mid = (low + high) * 0.5f - rot * Vector3.up * 0.1f;
        Box(name + " collider", mid, new Vector3(width, 0.2f, Mathf.Sqrt(length * length + rise * rise)),
            rot.eulerAngles, texture, tile, Col.Invisible, Penetrable.Impenetrable, Nav.None);
    }

    // ---------------- props (Poly Haven models in Assets/Models/Props/<slug>) ----------------

    // Decor: the model only, no collider, left out of the NavMesh. "bottom" is where the bottom centre of the model's
    // bounds stands (for a wall lamp: the bottom of the lamp). The front of a model faces +z at yaw 0.
    // part: some models hold two variants side by side; keep only the parts (child objects) whose name contains this
    // text, or with a leading "!" those whose name does not: rollershutter_door "graffiti" / "!graffiti",
    // metal_trash_can "rust" / "!rust", old_military_crate "_a" / "_b". The bounds are those of the kept parts.
    public static GameObject Prop(string name, string slug, Vector3 bottom, float yaw = 0f, bool twin = false, float scale = 1f,
                                  string part = null)
    {
        return Prop(name, slug, bottom, new Vector3(0f, yaw, 0f), twin, scale, part);
    }

    // the same with a full rotation, e.g. (90, 0, 0) lays a standing tyre flat
    public static GameObject Prop(string name, string slug, Vector3 bottom, Vector3 euler, bool twin = false, float scale = 1f,
                                  string part = null)
    {
        if (twin) Prop(name + " b", slug, Mirror(bottom), MirrorEuler(euler), false, scale, part);
        return PlaceModel(twin ? name + " a" : name, slug, bottom, Quaternion.Euler(euler), scale, Nav.None, part);
    }

    // A prop that is cover: the model plus an invisible collider of size (x, height, z) standing on "bottom", turned by
    // yaw, with Penetrable(pen); cylinder = a convex cylinder collider. The model is baked into the NavMesh as
    // Not Walkable, so bots walk round it.
    public static GameObject PropSolid(string name, string slug, Vector3 bottom, float yaw, Vector3 collider, float pen,
                                       bool cylinder = false, bool twin = false, float scale = 1f, string part = null)
    {
        if (twin) PropSolid(name + " b", slug, Mirror(bottom), yaw + 180f, collider, pen, cylinder, false, scale, part);
        string n = twin ? name + " a" : name;
        GameObject model = PlaceModel(n, slug, bottom, Quaternion.Euler(0f, yaw, 0f), scale, Nav.Block, part);
        Blocker(n + " collider", bottom + Vector3.up * collider.y * 0.5f, collider, new Vector3(0f, yaw, 0f), pen, cylinder);
        return model;
    }

    // A "prop:<slug>" table row: the invisible collider of the row (box, or cylinder) with Penetrable(pen) and "count"
    // models fitted into it in a row along its long side: uniform scale (the smallest of the three ratios), turned by
    // 90 degrees when the long side of the model does not match, standing on the bottom of the box.
    // A scale far from 1 is reported in the log (the box was probably not meant for this model).
    public static GameObject PropBox(string name, string slug, Vector3 center, Vector3 size, float yaw, float pen,
                                     bool cylinder = false, int count = 1, Nav nav = Nav.Block, string part = null)
    {
        GameObject col = Blocker(name, center, size, new Vector3(0f, yaw, 0f), pen, cylinder, nav);
        Bounds b = ModelBounds(slug, part);
        if (b.size == Vector3.zero) return col;

        bool alongZ = size.z >= size.x;
        Vector3 cell = alongZ ? new Vector3(size.x, size.y, size.z / count) : new Vector3(size.x / count, size.y, size.z);
        // turn the model when its long side lies across the cell's long side
        bool modelAlongZ = b.size.z >= b.size.x;
        bool cellAlongZ = cell.z >= cell.x;
        float extra = modelAlongZ == cellAlongZ ? 0f : 90f;
        Vector3 m = extra == 0f ? b.size : new Vector3(b.size.z, b.size.y, b.size.x);
        float scale = Mathf.Min(cell.x / m.x, Mathf.Min(cell.y / m.y, cell.z / m.z));
        if (scale < 0.85f || scale > 1.15f)
            Debug.LogWarning("MapKit: " + name + ": " + slug + " scaled by " + scale.ToString("F2") + " to fit " + size);

        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        Vector3 bottom = center - turn * Vector3.up * (size.y * 0.5f);
        for (int i = 0; i < count; i++)
        {
            float along = (i - (count - 1) * 0.5f) * (alongZ ? cell.z : cell.x);
            Vector3 offset = alongZ ? new Vector3(0f, 0f, along) : new Vector3(along, 0f, 0f);
            GameObject model = PlaceModel(name + (count > 1 ? " model " + (i + 1) : " model"), slug, bottom + turn * offset,
                                          Quaternion.Euler(0f, yaw + extra, 0f), scale, nav == Nav.None ? Nav.None : Nav.Block, part);
            model.transform.SetParent(col.transform, true);
        }
        return col;
    }

    // the size of a prop model (or of its kept parts, see Prop) at scale 1 and yaw 0 (x, height, z) in metres
    public static Vector3 PropSize(string slug, string part = null)
    {
        return ModelBounds(slug, part).size;
    }

    // the names of the parts (child objects) of a prop model, for the "part" filter
    public static string[] PropParts(string slug)
    {
        GameObject asset = PropImport.Model(slug);
        if (asset == null) return new string[0];
        var names = new string[asset.transform.childCount];
        for (int i = 0; i < names.Length; i++) names[i] = asset.transform.GetChild(i).name;
        return names;
    }

    static GameObject PlaceModel(string name, string slug, Vector3 bottom, Quaternion rotation, float scale, Nav nav, string part)
    {
        GameObject asset = PropImport.Model(slug);
        if (asset == null)
        {
            Debug.LogError("MapKit: prop " + slug + " is missing (Assets/Models/Props/" + slug + ")");
            return Make(name + " (missing " + slug + ")", bottom, rotation);
        }
        GameObject go = (GameObject)PrefabUtility.InstantiatePrefab(asset, SceneManager.GetActiveScene());
        go.name = name;
        KeepParts(go, slug, part);
        go.transform.SetParent(Level, false);
        go.transform.localScale = Vector3.one * scale;
        go.transform.rotation = rotation;
        go.transform.position = Vector3.zero;
        // the bounds of the turned and scaled model put its bottom centre on the given point
        Bounds b = WorldBounds(go);
        go.transform.position = bottom - new Vector3(b.center.x, b.min.y, b.center.z);
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = true;
        SetNav(go, nav);
        return go;
    }

    static Bounds ModelBounds(string slug, string part = null)
    {
        string key = slug + "/" + part;
        if (propBounds.TryGetValue(key, out Bounds cached)) return cached;
        GameObject asset = PropImport.Model(slug);
        Bounds b = new Bounds();
        if (asset != null)
        {
            GameObject tmp = (GameObject)Object.Instantiate(asset);
            KeepParts(tmp, slug, part);
            tmp.transform.position = Vector3.zero;
            tmp.transform.rotation = Quaternion.identity;
            tmp.transform.localScale = Vector3.one;
            b = WorldBounds(tmp);
            Object.DestroyImmediate(tmp);
        }
        propBounds[key] = b;
        return b;
    }

    // removes the child objects of a placed model that the part filter does not keep (a prefab instance is unpacked
    // first: its children cannot be deleted otherwise; the meshes and materials stay those of the FBX)
    static void KeepParts(GameObject go, string slug, string part)
    {
        if (string.IsNullOrEmpty(part)) return;
        bool not = part.StartsWith("!");
        string text = not ? part.Substring(1) : part;
        if (PrefabUtility.IsPartOfPrefabInstance(go))
            PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        int kept = 0;
        for (int i = go.transform.childCount - 1; i >= 0; i--)
        {
            Transform c = go.transform.GetChild(i);
            if (c.name.Contains(text) != not) kept++;
            else Object.DestroyImmediate(c.gameObject);
        }
        if (kept == 0)
            Debug.LogError("MapKit: no part of " + slug + " matches \"" + part + "\" (parts: " + string.Join(", ", PropParts(slug)) + ")");
    }

    static Bounds WorldBounds(GameObject go)
    {
        bool any = false;
        Bounds b = new Bounds(go.transform.position, Vector3.zero);
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (!any) b = r.bounds;
            else b.Encapsulate(r.bounds);
            any = true;
        }
        return b;
    }

    // ---------------- Borgo rules: roofs, plinths, facades ----------------

    // A tiled hipped roof on a block (centre / size of the block, axis-aligned in its yaw): the slopes rise at "pitch"
    // degrees from eaves that stand "overhang" metres out from the walls (a 0.12 m fascia under them) to a ridge along
    // the long side; maxRise cuts the roof flat at that height above the block (0.76 = the low eave band of the research
    // rule: four 25 degree slabs and a flat cap). One convex mesh, the same mesh as a convex collider, Penetrable 100,
    // Not Walkable. For every building (not the outer walls); the twin gets the same roof.
    public static GameObject[] Roof(string name, Vector3 center, Vector3 size, float yaw = 0f, bool twin = false,
                                    string texture = "RoofingTiles014A", float tile = 2.9f,
                                    float pitch = 25f, float overhang = 0.45f, float maxRise = 100f)
    {
        if (twin)
            return new[]
            {
                Roof(name + " a", center, size, yaw, false, texture, tile, pitch, overhang, maxRise)[0],
                Roof(name + " b", Mirror(center), size, yaw + 180f, false, texture, tile, pitch, overhang, maxRise)[0],
            };

        const float fascia = 0.12f;
        float ax = size.x * 0.5f + overhang, az = size.z * 0.5f + overhang;
        float slope = Mathf.Tan(pitch * Mathf.Deg2Rad);
        float inset = Mathf.Min(Mathf.Min(ax, az), maxRise / slope);    // how far the top edge stands in from the eaves
        float tx = ax - inset, tz = az - inset, h = inset * slope;
        float run = inset / Mathf.Cos(pitch * Mathf.Deg2Rad);           // eave to top edge along the slope

        var mb = new MeshBuild();
        // the south slope and fascia turned to each side: "a" = half length of the eave, "b" = half depth,
        // ta / tb the same at the top; seen from outside the corners go bottom left, bottom right, top right, top left
        void Side(float turnY, float a, float b, float ta, float tb)
        {
            Quaternion q = Quaternion.Euler(0f, turnY, 0f);
            Vector3 P(float x, float y, float z) { return q * new Vector3(x, y, z); }
            mb.Quad(P(-a, 0f, -b), P(a, 0f, -b), P(ta, h, -tb), P(-ta, h, -tb),
                    new Vector2(0f, 0f), new Vector2(2f * a / tile, 0f), new Vector2((a + ta) / tile, run / tile), new Vector2((a - ta) / tile, run / tile));
            mb.Quad(P(-a, -fascia, -b), P(a, -fascia, -b), P(a, 0f, -b), P(-a, 0f, -b),
                    new Vector2(0f, -fascia / tile), new Vector2(2f * a / tile, -fascia / tile), new Vector2(2f * a / tile, 0f), new Vector2(0f, 0f));
        }
        Side(0f, ax, az, tx, tz);
        Side(90f, az, ax, tz, tx);
        Side(180f, ax, az, tx, tz);
        Side(270f, az, ax, tz, tx);
        if (tx > 0.001f && tz > 0.001f)
            mb.Quad(new Vector3(-tx, h, -tz), new Vector3(tx, h, -tz), new Vector3(tx, h, tz), new Vector3(-tx, h, tz),
                    new Vector2(-tx, -tz) / tile, new Vector2(tx, -tz) / tile, new Vector2(tx, tz) / tile, new Vector2(-tx, tz) / tile);
        mb.Quad(new Vector3(-ax, -fascia, az), new Vector3(ax, -fascia, az), new Vector3(ax, -fascia, -az), new Vector3(-ax, -fascia, -az),
                new Vector2(-ax, az) / tile, new Vector2(ax, az) / tile, new Vector2(ax, -az) / tile, new Vector2(-ax, -az) / tile);

        string key = string.Format(CultureInfo.InvariantCulture, "{0}_p{1:0.#}_o{2:0.##}_r{3:0.##}", Key("roof", size, tile), pitch, overhang, Mathf.Min(maxRise, 99f));
        Mesh mesh = mb.Mesh(key);
        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        GameObject go = Make(name + " roof", center + turn * new Vector3(0f, size.y * 0.5f, 0f), turn);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial(texture);
        MeshCollider mc = go.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;
        mc.convex = true;
        go.AddComponent<Penetrable>().strength = Penetrable.Impenetrable;
        SetNav(go, Nav.Block);
        return new[] { go };
    }

    // the roof of a table row (a building): its centre, size, yaw and twin
    public static GameObject[] Roof(Row r, string texture = "RoofingTiles014A", float tile = 2.9f,
                                    float pitch = 25f, float overhang = 0.45f, float maxRise = 100f)
    {
        return Roof(r.name, r.center, r.size, r.euler.y, r.twin, texture, tile, pitch, overhang, maxRise);
    }

    // a 0.6 m plinth under a block, its footprint 0.1 m larger on each side
    public static GameObject[] Plinth(string name, Vector3 center, Vector3 size, bool twin = false,
                                      string texture = "Concrete012", float tile = 3f, float pen = 2.5f)
    {
        Vector3 c = new Vector3(center.x, center.y - size.y * 0.5f + 0.3f, center.z);
        Vector3 s = new Vector3(size.x + 0.2f, 0.6f, size.z + 0.2f);
        if (twin) return Twin(name + " plinth", c, s, Vector3.zero, texture, tile, Col.Yes, pen, Nav.Block);
        return new[] { Box(name + " plinth", c, s, Vector3.zero, texture, tile, Col.Yes, pen, Nav.Block) };
    }

    public static GameObject[] Plinth(Row r, string texture = "Concrete012", float tile = 3f, float pen = 2.5f)
    {
        return Plinth(r.name, r.center, r.size, r.twin, texture, tile, pen);
    }

    // the facade of a table row (a building): faces in the row's own axes (the twin gets the same faces of its copy,
    // which is turned by 180 degrees, so they look the other way in the world)
    public static void Facade(Row r, Faces faces, string shutterTexture, string twinShutterTexture = null,
                              float[] heights = null, float spacing = 3f)
    {
        Facade(r.name, r.center, r.size, faces, shutterTexture, r.euler.y, r.twin, twinShutterTexture, heights, spacing);
    }

    // the row of a parsed table by name (null and an error in the log if there is none)
    public static Row RowNamed(List<Row> rows, string name)
    {
        foreach (Row r in rows)
            if (r.name == name) return r;
        Debug.LogError("MapKit: no table row " + name);
        return null;
    }

    // Windows with open shutters on the given faces of a block (decor: no collider, not in the NavMesh): every
    // "spacing" metres along the face, rows centred at "heights" (default 4.2 and 7.0 m above the ground) where they fit
    // under the top; a dark window 0.9 x 1.3 m flush with the wall and two shutters 0.45 x 1.3 m beside it.
    // Two objects per facade ("<name> windows", "<name> shutters"), each one mesh.
    public static void Facade(string name, Vector3 center, Vector3 size, Faces faces, string shutterTexture,
                              float yaw = 0f, bool twin = false, string twinShutterTexture = null,
                              float[] heights = null, float spacing = 3f)
    {
        if (twin)
        {
            Facade(name + " a", center, size, faces, shutterTexture, yaw, false, null, heights, spacing);
            Facade(name + " b", Mirror(center), size, faces, twinShutterTexture ?? shutterTexture, yaw + 180f, false, null, heights, spacing);
            return;
        }
        if (heights == null) heights = new[] { 4.2f, 7.0f };
        var windows = new MeshBuild();
        var shutters = new MeshBuild();
        float bottom = center.y - size.y * 0.5f, top = center.y + size.y * 0.5f;
        string key = string.Format(CultureInfo.InvariantCulture, "_{0}_{1:0.##}", (int)faces, spacing);
        foreach (float y in heights) key += "_" + y.ToString("0.##", CultureInfo.InvariantCulture);

        void Face(Vector3 normal, Vector3 along, float faceLength, float depth)
        {
            int n = faceLength < 2.4f ? 0 : Mathf.FloorToInt((faceLength - 2.4f) / spacing) + 1;
            Quaternion look = Quaternion.LookRotation(normal);
            foreach (float y in heights)
            {
                if (y + 0.65f + 0.25f > top || y - 0.65f < bottom + 0.4f) continue;
                float ly = y - center.y;
                for (int k = 0; k < n; k++)
                {
                    float a = (k - (n - 1) * 0.5f) * spacing;
                    Vector3 c = normal * depth + along * a + Vector3.up * ly;
                    windows.Box(c, new Vector3(0.9f, 1.3f, 0.05f), look, 1.3f);
                    shutters.Box(c + normal * 0.03f + along * 0.675f, new Vector3(0.45f, 1.3f, 0.06f), look, 1.3f);
                    shutters.Box(c + normal * 0.03f - along * 0.675f, new Vector3(0.45f, 1.3f, 0.06f), look, 1.3f);
                }
            }
        }
        if ((faces & Faces.North) != 0) Face(Vector3.forward, Vector3.left, size.x, size.z * 0.5f);
        if ((faces & Faces.South) != 0) Face(Vector3.back, Vector3.right, size.x, size.z * 0.5f);
        if ((faces & Faces.East) != 0) Face(Vector3.right, Vector3.forward, size.z, size.x * 0.5f);
        if ((faces & Faces.West) != 0) Face(Vector3.left, Vector3.back, size.z, size.x * 0.5f);
        if (windows.Empty) return;

        Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
        string sizeKey = Key("facade", size, 0f) + key;
        GameObject w = Make(name + " windows", center, turn);
        w.AddComponent<MeshFilter>().sharedMesh = windows.Mesh(sizeKey + "_w");
        w.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial("color:#1c1c1c", 0.6f);
        SetNav(w, Nav.None);
        GameObject s = Make(name + " shutters", center, turn);
        s.AddComponent<MeshFilter>().sharedMesh = shutters.Mesh(sizeKey + "_s");
        s.AddComponent<MeshRenderer>().sharedMaterial = GetMaterial(shutterTexture);
        SetNav(s, Nav.None);
    }

    // ---------------- spawns, bombsites, bounds, cameras ----------------

    // the spawn points of both teams: CT at ct (rotation 180, facing -z), T at the mirrored points (rotation 0).
    // Each root gets exactly as many children as points (RoundManager uses them in order; 10 = 10 x 10 without extra places).
    public static void Spawns(Vector3[] ct)
    {
        var t = new Vector3[ct.Length];
        for (int i = 0; i < ct.Length; i++) t[i] = Mirror(ct[i]);
        SpawnPoints("CT Spawn", ct, 180f);
        SpawnPoints("T Spawn", t, 0f);
    }

    public static void SpawnPoints(string rootName, Vector3[] points, float yaw)
    {
        GameObject root = Find(rootName);
        if (root == null)
        {
            Debug.LogError("MapKit: " + rootName + " not found");
            return;
        }
        Transform r = root.transform;
        for (int i = r.childCount; i < points.Length; i++)
        {
            GameObject p = r.childCount > 0 ? Object.Instantiate(r.GetChild(0).gameObject, r) : new GameObject();
            p.transform.SetParent(r, false);
            p.name = "Spawn " + (i + 1);
        }
        for (int i = r.childCount - 1; i >= points.Length; i--)
            Object.DestroyImmediate(r.GetChild(i).gameObject);
        for (int i = 0; i < points.Length; i++)
        {
            Transform p = r.GetChild(i);
            p.position = points[i];
            p.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    // a bombsite volume under the root "Bombsites": trigger box on the Ignore Raycast layer (2), no renderer, no
    // Penetrable, so it never blocks bullets or the bots' view. For the bomb mode test positions with
    // GetComponent<BoxCollider>().bounds.Contains(p) (bots have no Rigidbody, trigger events would not fire for them).
    public static GameObject Bombsite(string name, Vector3 center, Vector3 size)
    {
        GameObject root = GameObject.Find("Bombsites");
        if (root == null) root = new GameObject("Bombsites");
        GameObject go = new GameObject(name);
        go.transform.SetParent(root.transform, false);
        go.transform.position = center;
        go.layer = IgnoreRaycastLayer;
        BoxCollider bc = go.AddComponent<BoxCollider>();
        bc.size = size;
        bc.isTrigger = true;
        return go;
    }

    // MapBounds: players outside +-half (or below minY / above maxY) die after the grace time; bots wander inside +-wander
    public static void SetBounds(float halfX, float halfZ, float wanderX, float wanderZ, float minY = -3f, float maxY = 30f)
    {
        MapBounds b = Object.FindAnyObjectByType<MapBounds>();
        if (b == null)
        {
            Debug.LogError("MapKit: no MapBounds in the scene");
            return;
        }
        b.halfSizeX = halfX;
        b.halfSizeZ = halfZ;
        b.wanderHalfX = wanderX;
        b.wanderHalfZ = wanderZ;
        b.minY = minY;
        b.maxY = maxY;
        EditorUtility.SetDirty(b);
    }

    // the camera that looks straight down on the map (rotation 90, 0, 0)
    public static void Overview(Vector3 position)
    {
        foreach (Camera c in Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
            if (c.name == "OverviewCamera")
            {
                c.transform.position = position;
                c.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                EditorUtility.SetDirty(c.transform);
            }
    }

    // the scene objects of the arena that must not stand inside the new buildings
    public static void Extras(Vector3 player, Vector3 trainingTarget, Vector3 target)
    {
        Move("Player", player);
        Move("Training Target", trainingTarget);
        Move("Target", target);
    }

    static void Move(string name, Vector3 position)
    {
        GameObject go = Find(name);
        if (go != null)
        {
            go.transform.position = position;
            EditorUtility.SetDirty(go.transform);
        }
        else Debug.LogWarning("MapKit: " + name + " not found");
    }

    // an object of the open scene by name, inactive ones too (GameObject.Find skips them: Target is inactive in SampleScene)
    public static GameObject Find(string name)
    {
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t.gameObject;
        return null;
    }

    // ---------------- sky, sun, ambient light, fog ----------------

    public static void Sky(SkySettings s)
    {
        string hdrPath = PropImport.SkyFolder + "/" + s.hdr;
        Texture2D hdr = PropImport.Sky(hdrPath);
        if (hdr == null) Debug.LogError("MapKit: no sky " + hdrPath);

        Material sky = Asset(MaterialPath + "/" + Map + "_Sky.mat", "Skybox/Panoramic");
        sky.SetTexture("_MainTex", hdr);
        sky.SetFloat("_Mapping", 1f);       // latitude-longitude
        sky.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
        sky.SetFloat("_ImageType", 0f);     // 360 degrees
        sky.SetFloat("_Rotation", s.rotation);
        sky.SetFloat("_Exposure", s.exposure);
        sky.SetColor("_Tint", s.tint);
        EditorUtility.SetDirty(sky);
        RenderSettings.skybox = sky;

        GameObject lightGo = Find("Directional Light");
        Light sun = lightGo != null ? lightGo.GetComponent<Light>() : null;
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(s.sunEuler);
            sun.color = s.sunColor;
            sun.intensity = s.sunIntensity;
            sun.shadows = LightShadows.Soft;
            RenderSettings.sun = sun;
            EditorUtility.SetDirty(sun);
        }
        else Debug.LogError("MapKit: no Directional Light");

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = s.ambientSky;
        RenderSettings.ambientEquatorColor = s.ambientEquator;
        RenderSettings.ambientGroundColor = s.ambientGround;
        RenderSettings.fog = s.fog;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogStartDistance = s.fogStart;
        RenderSettings.fogEndDistance = s.fogEnd;
        RenderSettings.fogColor = s.fogColor;
        SkyReflection();
        DynamicGI.UpdateEnvironment();
    }

    // The sky as the reflection of every material: the scenes have no baked lighting, so without it metal (the props,
    // DiamondPlate, Metal022) reflects black. A probe that renders nothing but the skybox is baked once into
    // Assets/Materials/<Map>/<Map>_SkyReflection.exr (128 px cubemap) and set as the scene's custom reflection.
    static void SkyReflection()
    {
        string path = MaterialPath + "/" + Map + "_SkyReflection.exr";
        used.Add(path);
        GameObject go = new GameObject("Sky reflection probe");
        go.transform.position = new Vector3(0f, 30f, 0f);
        ReflectionProbe probe = go.AddComponent<ReflectionProbe>();
        probe.mode = ReflectionProbeMode.Baked;
        probe.cullingMask = 0;
        probe.clearFlags = ReflectionProbeClearFlags.Skybox;
        probe.resolution = 128;
        probe.hdr = true;
        bool ok = Lightmapping.BakeReflectionProbe(probe, path);
        Object.DestroyImmediate(go);
        Cubemap cube = ok ? AssetDatabase.LoadAssetAtPath<Cubemap>(path) : null;
        if (cube == null)
        {
            Debug.LogError("MapKit: the sky reflection was not baked (" + path + ")");
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
            return;
        }
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = cube;
        RenderSettings.reflectionIntensity = 1f;
    }

    // The rotation of the sun light for a sun seen in the sky image at (elevation, azimuth; azimuth 0 = +z, 90 = +x)
    // when the sky is turned by skyRotation (Skybox/Panoramic _Rotation): world azimuth = image azimuth - rotation.
    public static Vector3 SunEuler(float elevation, float imageAzimuth, float skyRotation)
    {
        float az = (imageAzimuth - skyRotation) * Mathf.Deg2Rad, el = elevation * Mathf.Deg2Rad;
        Vector3 toSun = new Vector3(Mathf.Sin(az) * Mathf.Cos(el), Mathf.Sin(el), Mathf.Cos(az) * Mathf.Cos(el));
        return Quaternion.LookRotation(-toSun).eulerAngles;
    }

    // ---------------- materials ----------------

    // smoothness / metallic of a texture (maps_report section 2.8); call before the first use of that texture
    public static void SetSurface(string texture, float smoothness, float metallic)
    {
        surfaces[texture] = new Vector2(smoothness, metallic);
    }

    static Vector2 SurfaceOf(string id)
    {
        if (surfaces.TryGetValue(id, out Vector2 s)) return s;
        if (id.StartsWith("PaintedPlaster") || id.StartsWith("Plaster")) return new Vector2(0.08f, 0f);
        if (id.StartsWith("Planks") || id.StartsWith("WoodFloor") || id.StartsWith("PaintedWood")) return new Vector2(0.1f, 0f);
        if (id.StartsWith("RoofingTiles")) return new Vector2(0.2f, 0f);
        if (id.StartsWith("Asphalt") || id.StartsWith("Road")) return new Vector2(0.15f, 0f);
        // painted containers: paint is not metal (metallic looks dark without a baked reflection)
        if (id.StartsWith("CorrugatedSteel") || id.StartsWith("PaintedMetal")) return new Vector2(0.3f, 0f);
        if (id.StartsWith("Metal")) return new Vector2(0.3f, 0.2f);
        if (id.StartsWith("DiamondPlate")) return new Vector2(0.45f, 0.7f);
        if (id.StartsWith("Ground")) return new Vector2(0.05f, 0f);
        return new Vector2(0.12f, 0f);      // stone, concrete, bricks, paving
    }

    // the material of a texture id (Assets/Materials/<Map>/<Map>_<ID>.mat, URP Lit with the colour and GL normal map;
    // textures that are not square, like Planks039 and WoodFloor043 (1024 x 512), repeat twice as often along v),
    // or of "color:#rrggbb" (<Map>_Color_rrggbb, smoothness 0.9 unless given)
    public static Material GetMaterial(string texture, float colorSmoothness = 0.9f)
    {
        if (materials.TryGetValue(texture, out Material cached)) return cached;
        Material mat;
        if (texture.StartsWith("color:"))
        {
            string hex = texture.Substring(6).TrimStart('#');
            mat = Asset(MaterialPath + "/" + Map + "_Color_" + hex + ".mat", "Universal Render Pipeline/Lit");
            ColorUtility.TryParseHtmlString("#" + hex, out Color c);
            mat.SetTexture("_BaseMap", null);
            mat.SetTexture("_BumpMap", null);
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Smoothness", colorSmoothness);
            mat.SetFloat("_Metallic", 0f);
        }
        else
        {
            mat = Asset(MaterialPath + "/" + Map + "_" + texture + ".mat", "Universal Render Pipeline/Lit");
            string dir = TexturePath + "/" + texture + "/" + texture;
            Texture2D color = AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "_Color.jpg");
            if (color == null) Debug.LogError("MapKit: texture not found " + dir + "_Color.jpg");
            Texture2D normal = PropImport.NormalMap(dir + "_NormalGL.jpg") ? AssetDatabase.LoadAssetAtPath<Texture2D>(dir + "_NormalGL.jpg") : null;
            Vector2 surface = SurfaceOf(texture);
            mat.SetTexture("_BaseMap", color);
            mat.SetTextureScale("_BaseMap", TextureScale(dir + "_Color.jpg"));
            mat.SetColor("_BaseColor", Color.white);
            mat.SetFloat("_Smoothness", surface.x);
            mat.SetFloat("_Metallic", surface.y);
            mat.SetTexture("_BumpMap", normal);
            mat.SetFloat("_BumpScale", 1f);
        }
        BaseShaderGUI.SetMaterialKeywords(mat, LitGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(mat);
        materials[texture] = mat;
        return mat;
    }

    // (1, 2) for a 2:1 texture: the UVs are in metres / tile, so v has to repeat twice as often
    static Vector2 TextureScale(string path)
    {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return Vector2.one;
        ti.GetSourceTextureWidthAndHeight(out int w, out int h);
        if (w <= 0 || h <= 0 || w == h) return Vector2.one;
        return w > h ? new Vector2(1f, (float)w / h) : new Vector2((float)h / w, 1f);
    }

    static Material Asset(string path, string shader)
    {
        used.Add(path);
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader.name != shader) mat.shader = Shader.Find(shader);
        return mat;
    }

    // ---------------- meshes ----------------

    static string Key(string kind, Vector3 size, float tile)
    {
        return string.Format(CultureInfo.InvariantCulture, "{0}_{1:0.###}x{2:0.###}x{3:0.###}_t{4:0.##}", kind, size.x, size.y, size.z, tile);
    }

    // a box of the given size whose texture repeats every "tile" metres on each face
    static Mesh BoxMesh(Vector3 size, float tile)
    {
        string key = Key("box", size, tile);
        if (meshes.TryGetValue(key, out Mesh cached)) return cached;
        var mb = new MeshBuild();
        mb.Box(Vector3.zero, size, Quaternion.identity, tile);
        return mb.Mesh(key);
    }

    static Mesh CylinderMesh(Vector3 size, float tile)
    {
        string key = Key("cyl", size, tile);
        if (meshes.TryGetValue(key, out Mesh cached)) return cached;
        var mb = new MeshBuild();
        mb.Cylinder(size, tile, 24);
        return mb.Mesh(key);
    }

    // vertices of boxes and cylinders, saved as one mesh asset in Assets/Meshes/<Map>/<key>.asset (an existing asset
    // is reused, so its GUID does not change between builds)
    class MeshBuild
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector3> norms = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> tris = new List<int>();

        public bool Empty { get { return verts.Count == 0; } }

        // a box at "center" turned by "rot", each face with UV = face size / tile
        public void Box(Vector3 center, Vector3 size, Quaternion rot, float tile)
        {
            Vector3 h = size * 0.5f;
            void Face(Vector3 n, Vector3 u, Vector3 v, float su, float sv)
            {
                int start = verts.Count;
                Vector3 c = Vector3.Scale(n, h);
                Vector3 hu = u * su * 0.5f, hv = v * sv * 0.5f;
                verts.Add(center + rot * (c - hu - hv)); verts.Add(center + rot * (c + hu - hv));
                verts.Add(center + rot * (c + hu + hv)); verts.Add(center + rot * (c - hu + hv));
                for (int i = 0; i < 4; i++) norms.Add(rot * n);
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
        }

        // a flat quad, corners counter-clockwise as seen from its front (Unity draws clockwise triangles); two corners
        // may be the same point (a triangle, e.g. the hip end of a roof)
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
        {
            int start = verts.Count;
            Vector3 n = Vector3.Cross(d - a, b - a).normalized;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            for (int i = 0; i < 4; i++) norms.Add(n);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 1);
            tris.Add(start); tris.Add(start + 3); tris.Add(start + 2);
        }

        // an upright cylinder centred on the origin: smooth sides (u = arc length / tile, v = height / tile) and two caps
        public void Cylinder(Vector3 size, float tile, int sides)
        {
            float rx = size.x * 0.5f, rz = size.z * 0.5f, hy = size.y * 0.5f;
            float arc = 0f;
            int start = verts.Count;
            for (int i = 0; i <= sides; i++)
            {
                float a = 2f * Mathf.PI * i / sides;
                Vector3 p = new Vector3(rx * Mathf.Cos(a), 0f, rz * Mathf.Sin(a));
                if (i > 0)
                {
                    float b = 2f * Mathf.PI * (i - 1) / sides;
                    arc += Vector3.Distance(p, new Vector3(rx * Mathf.Cos(b), 0f, rz * Mathf.Sin(b)));
                }
                Vector3 n = new Vector3(Mathf.Cos(a) / Mathf.Max(rx, 0.001f), 0f, Mathf.Sin(a) / Mathf.Max(rz, 0.001f)).normalized;
                verts.Add(p + Vector3.down * hy); norms.Add(n); uvs.Add(new Vector2(arc / tile, 0f));
                verts.Add(p + Vector3.up * hy); norms.Add(n); uvs.Add(new Vector2(arc / tile, size.y / tile));
            }
            // seen from outside, a growing angle goes to the right: (0, 2, 1) is clockwise = front
            for (int i = 0; i < sides; i++)
            {
                int b0 = start + i * 2, b1 = b0 + 2;
                tris.Add(b0); tris.Add(b1 + 1); tris.Add(b1);
                tris.Add(b0); tris.Add(b0 + 1); tris.Add(b1 + 1);
            }
            foreach (float y in new[] { hy, -hy })
            {
                int c = verts.Count;
                Vector3 n = y > 0f ? Vector3.up : Vector3.down;
                verts.Add(new Vector3(0f, y, 0f)); norms.Add(n); uvs.Add(Vector2.zero);
                for (int i = 0; i < sides; i++)
                {
                    float a = 2f * Mathf.PI * i / sides;
                    Vector3 p = new Vector3(rx * Mathf.Cos(a), y, rz * Mathf.Sin(a));
                    verts.Add(p); norms.Add(n); uvs.Add(new Vector2(p.x / tile, p.z / tile));
                }
                for (int i = 0; i < sides; i++)
                {
                    int p0 = c + 1 + i, p1 = c + 1 + (i + 1) % sides;
                    if (y > 0f) { tris.Add(c); tris.Add(p1); tris.Add(p0); }
                    else { tris.Add(c); tris.Add(p0); tris.Add(p1); }
                }
            }
        }

        public Mesh Mesh(string key)
        {
            key = key.Replace(' ', '_');
            if (meshes.TryGetValue(key, out Mesh cached)) return cached;
            string path = MeshPath + "/" + key + ".asset";
            used.Add(path);
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            bool create = mesh == null;
            if (create) mesh = new Mesh();
            else mesh.Clear();
            mesh.name = key;
            mesh.indexFormat = verts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(verts);
            mesh.SetNormals(norms);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            if (create) AssetDatabase.CreateAsset(mesh, path);
            else EditorUtility.SetDirty(mesh);
            meshes[key] = mesh;
            return mesh;
        }
    }

    // ---------------- helpers ----------------

    static GameObject Make(string name, Vector3 position, Quaternion rotation)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(Level, false);
        go.transform.position = position;
        go.transform.rotation = rotation;
        go.isStatic = true;
        return go;
    }

    // the renderer of an invisible collider stays (disabled): GameAudio reads the footstep surface from its material
    static void Invisible(GameObject go, Col col)
    {
        if (col != Col.Invisible) return;
        MeshRenderer mr = go.GetComponent<MeshRenderer>();
        if (mr != null) mr.enabled = false;
    }

    static void SetNav(GameObject go, Nav nav)
    {
        if (nav == Nav.Walk) return;
        NavMeshModifier m = go.GetComponent<NavMeshModifier>();
        if (m == null) m = go.AddComponent<NavMeshModifier>();
        m.applyToChildren = true;
        if (nav == Nav.None) m.ignoreFromBuild = true;
        else
        {
            m.overrideArea = true;
            m.area = NotWalkableArea;
        }
    }

    static void Folder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        Folder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }

    // deletes the assets of a folder of this map that the last build did not use
    static void Prune(string folder)
    {
        if (!AssetDatabase.IsValidFolder(folder)) return;
        foreach (string guid in AssetDatabase.FindAssets("", new[] { folder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(path) || used.Contains(path)) continue;
            if (Path.GetDirectoryName(path).Replace('\\', '/') != folder) continue;
            AssetDatabase.DeleteAsset(path);
        }
    }
}
