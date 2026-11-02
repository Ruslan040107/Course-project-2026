using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Tools > Build Porto Map: makes Assets/Scenes/PortoMap.unity with MapKit, an industrial port in the spirit of CS 2,
// 104 x 80 m (x -52..52, z -40..40; maps_report.md section 4). T spawn in the south, CT spawn in the north, both
// bombsites on the CT half: A is a loading dock with a steel catwalk, B a tank farm with a steel platform. Mid is a
// container yard with an offset gate, the outer lanes are roads with container stacks and gatehouses as chicanes.
// Point symmetric like the desert map: every object has a twin at (-x, -z) turned by 180 degrees; only the colours of
// some buildings and the painted signs differ.
// The layout table was checked for reachability, balance and sightlines (the longest is 55.5 m). Everything else here
// is decoration: no collider (or a thin one tight against a wall) and left out of the NavMesh, so the bots, the cover
// and the sightlines are those of the table.
// Textures: ambientCG; props and sky: Poly Haven (all CC0, see CREDITS.md).
public static class PortoMapBuilder
{
    // the layout (maps_report.md section 4; the road of each outer lane runs its whole length here, it is only a decal)
    const string Layout = @"
        Ground|box|(0, -0.25, 0)|(104, 0.5, 80)|0|Asphalt031|yes|100|walk|-|4
        Road lane E|box|(40, 0.01, 0)|(12, 0.02, 32)|0|Road006|no|100|none|T|7.5
        Yard slab|box|(0, 0.01, 0)|(24, 0.02, 16)|0|Concrete031|no|100|none|-|3
        Wall N|box|(0, 6, 40.5)|(106, 12, 1)|0|Concrete046|yes|100|block|T|3
        Wall E|box|(52.5, 6, 0)|(1, 12, 80)|0|CorrugatedSteel005|yes|100|block|T|3
        Shed W outer|box|(-49, 3.5, 8)|(6, 7, 16)|0|CorrugatedSteel006A [CorrugatedSteel007A]|yes|100|block|T|3
        Shed E outer|box|(49, 3.5, 8)|(6, 7, 16)|0|CorrugatedSteel007A [CorrugatedSteel006A]|yes|100|block|T|3
        Warehouse WC|box|(-23, 4.5, 8)|(22, 9, 16)|0|CorrugatedSteel007A [CorrugatedSteel004]|yes|100|block|T|3
        Block mid W low|box|(-8, 4, 12)|(8, 8, 8)|0|Concrete031|yes|100|block|T|3
        Block mid W high|box|(-15, 4.5, 24.5)|(22, 9, 7)|0|CorrugatedSteel004 [CorrugatedSteel007A]|yes|100|block|T|3
        Block CT-B|box|(-20, 4.5, 31)|(12, 9, 6)|0|Concrete031|yes|100|block|T|3
        Warehouse EC|box|(12, 5, 18)|(16, 10, 20)|0|CorrugatedSteel007A [CorrugatedSteel004]|yes|100|block|T|3
        Block CT-A|box|(17, 5, 31)|(6, 10, 6)|0|Concrete031|yes|100|block|T|3
        A short south|box|(23, 3.5, 1)|(22, 7, 2)|0|Concrete046|yes|100|block|T|3
        Dock warehouse|box|(30, 4, 9)|(8, 8, 14)|0|CorrugatedSteel004 [CorrugatedSteel007A]|yes|100|block|T|3
        CT office|box|(0, 3.75, 37)|(12, 7.5, 6)|0|Concrete046|yes|100|block|T|3
        A warehouse|box|(46, 5, 37)|(12, 10, 6)|0|CorrugatedSteel007A [CorrugatedSteel004]|yes|100|block|T|3
        A dock|box|(41, 0.7, 30)|(22, 1.4, 8)|0|Concrete044D|yes|100|walk|T|3
        A dock edge|box|(41, 1.31, 25.925)|(22, 0.22, 0.15)|0|PaintedMetal016|no|100|none|T|1
        A dock ramp|box|(27.034, 0.554, 30)|(6, 0.3, 6.161)|(-13.13, 90, 0)|Concrete044D|yes|100|walk|T|3
        A dock stairs collider|box|(48, 0.611, 24.645)|(3, 0.2, 3.13)|(-26.57, 0, 0)|Concrete031|yes, invisible|100|walk|T|3
        A catwalk|box|(35, 4.3, 38.5)|(10, 0.2, 3)|0|DiamondPlate008C|yes|0.5|walk|T|1.5
        A catwalk rail|box|(36.5, 4.9, 37)|(7, 1, 0.1)|0|Metal022|yes|0.5|block|T|1
        A catwalk stair support|box|(32, 0.7, 35.4)|(2, 1.4, 2.8)|0|Concrete044D|yes|100|block|T|3
        A catwalk stairs collider|box|(32, 2.809, 33.792)|(2, 0.2, 7.159)|(-24.78, 0, 0)|DiamondPlate008C|yes, invisible|100|walk|T|3
        A catwalk post 1|box|(30.2, 2.1, 39.75)|(0.3, 4.2, 0.3)|0|Metal022|yes|0.5|block|T|1
        A catwalk post 2|box|(34.8, 2.1, 39.75)|(0.3, 4.2, 0.3)|0|Metal022|yes|0.5|block|T|1
        A catwalk post 3|box|(39.8, 2.1, 39.75)|(0.3, 4.2, 0.3)|0|Metal022|yes|0.5|block|T|1
        A container 1 L1|box|(26, 1.295, 21)|(2.44, 2.59, 6.06)|0|CorrugatedSteel002|yes|0.5|block|T|2.59
        A container 2 L1|box|(38, 1.295, 19.5)|(6.06, 2.59, 2.44)|0|CorrugatedSteel006A|yes|0.5|block|T|2.59
        A container 2 L2|box|(38, 3.885, 19.5)|(6.06, 2.59, 2.44)|0|CorrugatedSteel006A|yes|0.5|block|T|2.59
        A crate 1|box|(44, 0.6, 21.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        A crate 2|box|(44, 1.8, 21.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        A crate 3|box|(45.3, 0.5, 21.6)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        A barrier|box|(22, 0.45, 32.8)|(3, 0.9, 0.6)|0|Concrete046|yes|2.5|block|T|1.5
        A dock crate 1|box|(36, 2, 31)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        A dock crate 2|box|(37.3, 1.9, 31)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        A pallet stack|box|(49.8, 1.9, 30.8)|(1.6, 1, 1.6)|0|WoodFloor043|yes|0.5|block|T|1
        B tank 1|cyl|(-44, 4.5, 30)|(8, 9, 8)|0|Metal022|yes|100|block|T|3
        B tank 2|cyl|(-33.5, 3, 37)|(5, 6, 5)|0|Metal022|yes|100|block|T|3
        B platform|box|(-32, 2.9, 24)|(8, 0.2, 4)|0|DiamondPlate008C|yes|0.5|walk|T|1.5
        B platform post 1|box|(-35.8, 1.4, 22.2)|(0.3, 2.8, 0.3)|0|Metal022|yes|0.5|block|T|1
        B platform post 2|box|(-28.2, 1.4, 22.2)|(0.3, 2.8, 0.3)|0|Metal022|yes|0.5|block|T|1
        B platform post 3|box|(-35.8, 1.4, 25.8)|(0.3, 2.8, 0.3)|0|Metal022|yes|0.5|block|T|1
        B platform post 4|box|(-28.2, 1.4, 25.8)|(0.3, 2.8, 0.3)|0|Metal022|yes|0.5|block|T|1
        B platform rail S|box|(-33, 3.5, 22.05)|(6, 1, 0.1)|0|Metal022|yes|0.5|block|T|1
        B platform rail W|box|(-35.95, 3.5, 24.05)|(0.1, 1, 3.9)|0|Metal022|yes|0.5|block|T|1
        B platform stairs collider|box|(-29, 1.414, 28.449)|(2, 0.2, 5.831)|(-30.96, 180, 0)|DiamondPlate008C|yes, invisible|100|walk|T|3
        B container 1 L1|box|(-48.5, 1.295, 19.5)|(6.06, 2.59, 2.44)|0|CorrugatedSteel007A|yes|0.5|block|T|2.59
        B crate 1|box|(-38.5, 0.6, 18)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        B crate 2|box|(-37.2, 0.5, 18.1)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        B barrel 1|cyl|(-30, 0.45, 33.5)|(0.6, 0.9, 0.6)|0|prop:Barrel_01|yes|0.5|block|T|3
        B barrel 2|cyl|(-29.3, 0.45, 33.9)|(0.6, 0.9, 0.6)|0|prop:Barrel_01|yes|0.5|block|T|3
        B pipe rack|box|(-51.7, 1.5, 28)|(0.6, 3, 12)|0|Metal022|yes|0.5|block|T|1.5
        Mid container|box|(0, 1.295, 0)|(6.06, 2.59, 2.44)|0|CorrugatedSteel004|yes|0.5|block|-|2.59
        Yard container L1|box|(-7.5, 1.295, 4)|(6.06, 2.59, 2.44)|0|CorrugatedSteel006A|yes|0.5|block|T|2.59
        Yard barrier|box|(8, 0.45, 4.8)|(3, 0.9, 0.6)|0|Concrete046|yes|2.5|block|T|1.5
        Mid gate wall W|box|(-1.5, 1.8, 23)|(5, 3.6, 0.6)|0|Concrete046|yes|2.5|block|T|2
        Mid gate wall E|box|(3.75, 1.8, 23)|(0.5, 3.6, 0.6)|0|Concrete046|yes|2.5|block|T|2
        Mid gate lintel|box|(2.25, 3.2, 23)|(2.5, 0.8, 0.6)|0|Concrete046|yes|2.5|block|T|2
        CT mid crate|box|(-2.6, 0.6, 12)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        East lane stack L1|box|(37.03, 1.295, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel002|yes|0.5|block|T|2.59
        East lane stack L2|box|(37.03, 3.885, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel002|yes|0.5|block|T|2.59
        East lane stack L3|box|(37.03, 6.475, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel002|yes|0.5|block|T|2.59
        West lane stack L1|box|(-42.97, 1.295, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel004|yes|0.5|block|T|2.59
        West lane stack L2|box|(-42.97, 3.885, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel004|yes|0.5|block|T|2.59
        West lane stack L3|box|(-42.97, 6.475, 7)|(6.06, 2.59, 2.44)|0|CorrugatedSteel004|yes|0.5|block|T|2.59
        East lane gatehouse|box|(43, 2.5, 14.5)|(6, 5, 3)|0|Concrete046|yes|2.5|block|T|2
        West lane gatehouse|box|(-37, 2.5, 14.5)|(6, 5, 3)|0|Concrete046|yes|2.5|block|T|2
        A short crate|box|(23, 0.6, 11)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        B link barrier|box|(-15.7, 0.5, 17.5)|(0.6, 1, 2.6)|0|Concrete046|yes|2.5|block|T|1.5";

    // CT spawn points (T: mirrored), one row along the front of the CT office
    static readonly Vector3[] CtSpawns =
    {
        new Vector3(-11f, 1f, 29.5f), new Vector3(-6.5f, 1f, 29.5f), new Vector3(-2f, 1f, 29.5f), new Vector3(2.5f, 1f, 29.5f),
        new Vector3(7f, 1f, 29.5f), new Vector3(11.5f, 1f, 29.5f), new Vector3(-11f, 1f, 32.6f), new Vector3(-6.5f, 1f, 32.6f),
        new Vector3(7f, 1f, 32.6f), new Vector3(11.5f, 1f, 32.6f),
    };

    // paint, glass and rubber (MapKit "color:" materials, their smoothness is set in Materials)
    const string Glass = "color:#20272d";
    const string WindowFrame = "color:#43474a";
    const string DoorPaint = "color:#5a676b";
    const string SteelGrey = "color:#5b5e5f";
    const string Rubber = "color:#1d1d1d";
    const string Seam = "color:#262626";
    const string YellowPaint = "color:#c9a83f";
    const string WhitePaint = "color:#bdbdb4";
    const string SignPaint = "color:#d6b24a";
    const string CranePaint = "color:#9b5a3c";
    const string CraneWhite = "color:#b5b5ad";

    static readonly float[] Sides = { -1f, 1f };

    [MenuItem("Tools/Build Porto Map")]
    public static void Build()
    {
        if (!MapKit.Begin("Porto", "PortoMap")) return;
        Materials();
        List<MapKit.Row> rows = MapKit.Table(Layout);

        // the visual steps; their ramp colliders (for the player) are the "stairs collider" rows of the table
        MapKit.Stairs("A dock stairs", new Vector3(48f, 0f, 23.2f), new Vector3(48f, 1.4f, 26f), 3f, 6, "Concrete031", 3f, false, true);
        MapKit.Stairs("A catwalk stairs", new Vector3(32f, 1.4f, 30.5f), new Vector3(32f, 4.4f, 37f), 2f, 12, "DiamondPlate008C", 1.5f, false, true);
        MapKit.Stairs("B platform stairs", new Vector3(-29f, 0f, 31f), new Vector3(-29f, 3f, 26f), 2f, 12, "DiamondPlate008C", 1.5f, false, true);

        foreach (MapKit.Row r in rows)
            if (r.name.Contains("container") || r.name.Contains("stack")) Container(r);
        Buildings(rows);
        OuterWalls();
        Ground();
        SiteA();
        SiteB();
        Middle();
        Props();
        Signs();
        Backdrop();

        MapKit.Spawns(CtSpawns);
        MapKit.Bombsite("Bombsite A", new Vector3(40f, 3f, 26f), new Vector3(20f, 6f, 16f));
        MapKit.Bombsite("Bombsite B", new Vector3(-40f, 3f, 27f), new Vector3(20f, 6f, 18f));
        MapKit.SetBounds(52.5f, 40.5f, 49f, 37f);
        MapKit.Overview(new Vector3(0f, 85f, 0f));
        MapKit.Extras(new Vector3(11f, 1f, -29.5f), new Vector3(0f, 1f, -14f), new Vector3(6f, 1f, 6f));
        MapKit.Sky(MapKit.SkySettings.Porto());
        MapKit.Finish();
    }

    static void Materials()
    {
        // the light concrete (#cccdb9) is the biggest surface of the map (outer walls, office, gatehouses, barriers):
        // a mid grey keeps the players readable in front of it
        Material concrete = MapKit.GetMaterial("Concrete046");
        concrete.SetColor("_BaseColor", new Color(0.72f, 0.72f, 0.72f, 1f));
        EditorUtility.SetDirty(concrete);
        MapKit.GetMaterial(Glass, 0.9f);
        MapKit.GetMaterial(WindowFrame, 0.35f);
        MapKit.GetMaterial(DoorPaint, 0.35f);
        MapKit.GetMaterial(SteelGrey, 0.4f);
        MapKit.GetMaterial(Rubber, 0.15f);
        MapKit.GetMaterial(Seam, 0.2f);
        MapKit.GetMaterial(YellowPaint, 0.3f);
        MapKit.GetMaterial(WhitePaint, 0.2f);
        MapKit.GetMaterial(SignPaint, 0.25f);
        MapKit.GetMaterial(CranePaint, 0.3f);
        MapKit.GetMaterial(CraneWhite, 0.3f);
    }

    // ---------------- frames: groups of parts and their twins ----------------

    // the place of a group of parts: an origin and a rotation of its own axes
    struct Frame
    {
        public Vector3 origin;
        public Quaternion rotation;
        public string suffix;
        public bool twin;       // the copy turned by 180 degrees round the map centre

        public Vector3 Point(Vector3 local) { return origin + rotation * local; }
        public Vector3 Euler(Vector3 local) { return (rotation * Quaternion.Euler(local)).eulerAngles; }
    }

    // the group at origin turned by yaw, and its twin (MapKit.Mirror: (-x, y, -z), yaw + 180)
    static Frame[] At(Vector3 origin, float yaw, bool twin = true)
    {
        var a = new Frame { origin = origin, rotation = Quaternion.Euler(0f, yaw, 0f), suffix = twin ? " a" : "" };
        if (!twin) return new[] { a };
        var b = new Frame { origin = MapKit.Mirror(origin), rotation = Quaternion.Euler(0f, yaw + 180f, 0f), suffix = " b", twin = true };
        return new[] { a, b };
    }

    // world axes of the CT half and the turned copy: local coordinates of these frames are world coordinates
    static readonly Frame[] Both = At(Vector3.zero, 0f);

    // a frame on a wall: the origin at "point" (world, CT half), z out of the wall along the normal, x along the wall
    static Frame[] OnWall(Vector3 point, Vector3 normal, bool twin = true)
    {
        return At(point, Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg, twin);
    }

    // decoration: a box in the frame's axes, no collider, left out of the NavMesh
    static GameObject Deco(Frame f, string name, Vector3 local, Vector3 size, string texture, float tile, Vector3 euler, bool shadow = true)
    {
        GameObject go = MapKit.Box(name + f.suffix, f.Point(local), size, f.Euler(euler), texture, tile,
                                   MapKit.Col.No, Penetrable.Impenetrable, MapKit.Nav.None);
        if (!shadow) NoShadow(go);
        return go;
    }

    static GameObject Deco(Frame f, string name, Vector3 local, Vector3 size, string texture, float tile, bool shadow = true)
    {
        return Deco(f, name, local, size, texture, tile, Vector3.zero, shadow);
    }

    // a box with a collider (Penetrable pen) that the bots walk round
    static GameObject Solid(Frame f, string name, Vector3 local, Vector3 size, string texture, float tile, float pen)
    {
        return MapKit.Box(name + f.suffix, f.Point(local), size, f.Euler(Vector3.zero), texture, tile, MapKit.Col.Yes, pen, MapKit.Nav.Block);
    }

    static GameObject DecoCylinder(Frame f, string name, Vector3 local, Vector3 size, string texture, float tile, Vector3 euler, bool shadow = true)
    {
        GameObject go = MapKit.Cylinder(name + f.suffix, f.Point(local), size, f.Euler(euler), texture, tile,
                                        MapKit.Col.No, Penetrable.Impenetrable, MapKit.Nav.None);
        if (!shadow) NoShadow(go);
        return go;
    }

    // a pipe from a to b (frame axes); solid = with a collider (where a player could walk into it)
    static GameObject Pipe(Frame f, string name, Vector3 a, Vector3 b, float diameter, string texture, bool solid = false)
    {
        Vector3 axis = b - a;
        Vector3 euler = Quaternion.FromToRotation(Vector3.up, axis).eulerAngles;
        Vector3 size = new Vector3(diameter, axis.magnitude, diameter);
        if (!solid) return DecoCylinder(f, name, (a + b) * 0.5f, size, texture, 1f, euler);
        return MapKit.Cylinder(name + f.suffix, f.Point((a + b) * 0.5f), size, f.Euler(euler), texture, 1f,
                               MapKit.Col.Yes, Penetrable.Impenetrable, MapKit.Nav.Block);
    }

    // a beam (box) from a to b (frame axes), w x h in cross-section
    static GameObject Beam(Frame f, string name, Vector3 a, Vector3 b, float w, float h, string texture, bool shadow = true)
    {
        Vector3 axis = b - a;
        return Deco(f, name, (a + b) * 0.5f, new Vector3(w, h, axis.magnitude), texture, 1f, Quaternion.LookRotation(axis).eulerAngles, shadow);
    }

    static void NoShadow(GameObject go)
    {
        foreach (MeshRenderer r in go.GetComponentsInChildren<MeshRenderer>()) r.shadowCastingMode = ShadowCastingMode.Off;
    }

    // a prop on a wall (lamp, lifebuoy): its back against the wall, its front (+z of the model) along the normal;
    // point = on the wall where the bottom of the model is
    static void WallProp(string name, string slug, Vector3 point, Vector3 normal, bool twin = true)
    {
        float depth = MapKit.PropSize(slug).z;
        MapKit.Prop(name, slug, point + normal * (depth * 0.5f + 0.01f), Mathf.Atan2(normal.x, normal.z) * Mathf.Rad2Deg, twin);
    }

    // an invisible collider that keeps the bots away, and its twin
    static void Blockers(string name, Vector3 center, Vector3 size, float pen, bool cylinder)
    {
        MapKit.Blocker(name + " a", center, size, Vector3.zero, pen, cylinder);
        MapKit.Blocker(name + " b", MapKit.Mirror(center), size, new Vector3(0f, 180f, 0f), pen, cylinder);
    }

    // ---------------- containers ----------------

    // an ISO container of the table: corner posts, top and bottom rails, the doors with lock bars at one end
    static void Container(MapKit.Row r)
    {
        bool alongX = r.size.x >= r.size.z;
        float l = Mathf.Max(r.size.x, r.size.z), w = Mathf.Min(r.size.x, r.size.z), h = r.size.y;
        // own axes: the long side along x, the doors at +x
        foreach (Frame f in At(r.center, alongX ? 0f : 90f, r.twin))
        {
            string tex = f.twin && r.twinTexture != null ? r.twinTexture : r.texture;
            foreach (float sx in Sides)
                foreach (float sz in Sides)
                    Deco(f, r.name + " post", new Vector3(sx * (l * 0.5f - 0.07f), 0f, sz * (w * 0.5f - 0.07f)),
                         new Vector3(0.18f, h + 0.02f, 0.18f), tex, r.tile, false);
            foreach (float sz in Sides)
                foreach (float sy in Sides)
                    Deco(f, r.name + " rail", new Vector3(0f, sy * (h * 0.5f - 0.085f), sz * w * 0.5f),
                         new Vector3(l - 0.36f, 0.15f, 0.04f), tex, r.tile, false);
            foreach (float k in new[] { -0.38f, -0.13f, 0.13f, 0.38f })
                Deco(f, r.name + " lock bar", new Vector3(l * 0.5f + 0.03f, 0f, k * w), new Vector3(0.05f, h - 0.45f, 0.05f), SteelGrey, 1f, false);
            Deco(f, r.name + " door gap", new Vector3(l * 0.5f + 0.006f, 0f, 0f), new Vector3(0.012f, h - 0.3f, 0.025f), Seam, 1f, false);
        }
    }

    // ---------------- buildings ----------------

    static void Buildings(List<MapKit.Row> rows)
    {
        // corrugated warehouses and sheds: a concrete footing and a low metal roof; concrete blocks: a coping
        foreach (string n in new[] { "Shed W outer", "Shed E outer", "Warehouse WC", "Block mid W high", "Warehouse EC", "Dock warehouse", "A warehouse" })
            Warehouse(MapKit.RowNamed(rows, n));
        foreach (string n in new[] { "Block mid W low", "Block CT-B", "Block CT-A", "A short south", "CT office" })
            Coping(MapKit.RowNamed(rows, n), 0.08f);
        Coping(MapKit.RowNamed(rows, "East lane gatehouse"), 0.35f);
        Coping(MapKit.RowNamed(rows, "West lane gatehouse"), 0.35f);

        // windows under the eaves of the warehouses: the middle of the face at the floor, its normal, its length
        // (CT half, the twins get the same); windows of the concrete blocks only above head height, so the band
        // behind the players stays plain
        float[] eaves7 = { 5f }, eaves8 = { 6f }, eaves9 = { 7f }, eaves10 = { 8f };
        Strip("Shed W outer", new Vector3(-46f, 0f, 8f), Vector3.right, 16f, eaves7);
        Strip("Shed W outer", new Vector3(-49f, 0f, 16f), Vector3.forward, 6f, eaves7);
        Strip("Shed E outer", new Vector3(46f, 0f, 8f), Vector3.left, 16f, eaves7);
        Strip("Shed E outer", new Vector3(49f, 0f, 16f), Vector3.forward, 6f, eaves7);
        Strip("Warehouse WC", new Vector3(-34f, 0f, 8f), Vector3.left, 16f, eaves9);
        Strip("Warehouse WC", new Vector3(-23f, 0f, 16f), Vector3.forward, 22f, eaves9);
        Strip("Warehouse WC", new Vector3(-12f, 0f, 4f), Vector3.right, 8f, eaves9);
        Strip("Block mid W high", new Vector3(-15f, 0f, 21f), Vector3.back, 22f, eaves9);
        Strip("Block mid W high", new Vector3(-4f, 0f, 24.5f), Vector3.right, 7f, eaves9);
        Strip("Block mid W high", new Vector3(-26f, 0f, 24.5f), Vector3.left, 7f, eaves9);
        Strip("Block mid W high", new Vector3(-9f, 0f, 28f), Vector3.forward, 10f, eaves9);
        Strip("Warehouse EC", new Vector3(4f, 0f, 18f), Vector3.left, 20f, eaves10);
        Strip("Warehouse EC", new Vector3(12f, 0f, 8f), Vector3.back, 16f, eaves10);
        Strip("Warehouse EC", new Vector3(20f, 0f, 18f), Vector3.right, 20f, eaves10);
        Strip("Warehouse EC", new Vector3(9f, 0f, 28f), Vector3.forward, 10f, eaves10);
        Strip("Dock warehouse", new Vector3(34f, 0f, 9f), Vector3.right, 14f, eaves8);
        Strip("Dock warehouse", new Vector3(30f, 0f, 16f), Vector3.forward, 8f, eaves8);
        Strip("Dock warehouse", new Vector3(26f, 0f, 9f), Vector3.left, 14f, eaves8);
        Strip("A warehouse", new Vector3(46f, 0f, 34f), Vector3.back, 12f, eaves10);
        Strip("A warehouse", new Vector3(40f, 0f, 37f), Vector3.left, 6f, eaves10);

        float[] two8 = { 4.6f, 6.6f }, two9 = { 4.6f, 7.2f }, two10 = { 4.6f, 7.4f }, office = { 4.8f };
        Rows("Block mid W low", new Vector3(-8f, 0f, 8f), Vector3.back, 8f, two8);
        Rows("Block mid W low", new Vector3(-4f, 0f, 12f), Vector3.right, 8f, two8);
        Rows("Block mid W low", new Vector3(-8f, 0f, 16f), Vector3.forward, 8f, two8);
        Rows("Block CT-B", new Vector3(-20f, 0f, 34f), Vector3.forward, 12f, two9);
        Rows("Block CT-B", new Vector3(-14f, 0f, 31f), Vector3.right, 6f, two9);
        Rows("Block CT-A", new Vector3(20f, 0f, 31f), Vector3.right, 6f, two10);
        Rows("Block CT-A", new Vector3(17f, 0f, 34f), Vector3.forward, 6f, two10);
        Rows("Block CT-A", new Vector3(14f, 0f, 31f), Vector3.left, 6f, two10);
        Rows("CT office", new Vector3(0f, 0f, 34f), Vector3.back, 12f, office);
        Rows("CT office", new Vector3(6f, 0f, 37f), Vector3.right, 6f, office);
        Rows("CT office", new Vector3(-6f, 0f, 37f), Vector3.left, 6f, office);
        // the gatehouses: one window on each free side, a door beside the window on the lane side
        float[] booth = { 2.7f };
        Windows("East lane gatehouse", new Vector3(41.5f, 0f, 13f), Vector3.back, 0f, booth, 1.6f, 1.0f, 0f);
        Windows("East lane gatehouse", new Vector3(40f, 0f, 14.5f), Vector3.left, 0f, booth, 1.6f, 1.0f, 0f);
        Windows("East lane gatehouse", new Vector3(43f, 0f, 16f), Vector3.forward, 0f, booth, 1.6f, 1.0f, 0f);
        Windows("West lane gatehouse", new Vector3(-38.5f, 0f, 13f), Vector3.back, 0f, booth, 1.6f, 1.0f, 0f);
        Windows("West lane gatehouse", new Vector3(-40f, 0f, 14.5f), Vector3.left, 0f, booth, 1.6f, 1.0f, 0f);
        Windows("West lane gatehouse", new Vector3(-37f, 0f, 16f), Vector3.forward, 0f, booth, 1.6f, 1.0f, 0f);

        // steel doors (with a lamp above most of them) and roller shutters
        Door("Shed E outer", new Vector3(46f, 0f, 5.5f), Vector3.left, true);
        Door("Warehouse WC", new Vector3(-16f, 0f, 16f), Vector3.forward, true);
        Door("Block mid W low", new Vector3(-4f, 0f, 14.5f), Vector3.right, false);
        Door("Block mid W high", new Vector3(-10f, 0f, 21f), Vector3.back, true);
        Door("Block CT-B", new Vector3(-14f, 0f, 32.5f), Vector3.right, false);
        Door("Warehouse EC", new Vector3(20f, 0f, 12f), Vector3.right, true);
        Door("Block CT-A", new Vector3(14f, 0f, 32.5f), Vector3.left, false);
        Door("Dock warehouse", new Vector3(30f, 0f, 16f), Vector3.forward, true);
        Door("CT office", new Vector3(0f, 0f, 34f), Vector3.back, true, 1.8f, 2.3f);
        Door("A warehouse", new Vector3(40f, 0f, 35.4f), Vector3.left, false);
        Door("East lane gatehouse", new Vector3(44.5f, 0f, 13f), Vector3.back, false);
        Door("West lane gatehouse", new Vector3(-35.5f, 0f, 13f), Vector3.back, false);
        WallProp("East lane gatehouse lamp", "industrial_wall_lamp", new Vector3(43f, 3.0f, 13f), Vector3.back);
        WallProp("West lane gatehouse lamp", "industrial_wall_lamp", new Vector3(-37f, 3.0f, 13f), Vector3.back);

        Shutter("Shed W outer", new Vector3(-46f, 0f, 8f), Vector3.right, 3.2f, 3.4f);
        Shutter("Warehouse WC W", new Vector3(-34f, 0f, 10f), Vector3.left, 3.6f, 4f);
        Shutter("Warehouse WC N", new Vector3(-30f, 0f, 16f), Vector3.forward, 3.6f, 4f);
        Shutter("Block mid W high", new Vector3(-26f, 0f, 24.5f), Vector3.left, 3.2f, 3.6f);
        Shutter("Warehouse EC W", new Vector3(4f, 0f, 14f), Vector3.left, 3.6f, 4f);
        Shutter("Warehouse EC E", new Vector3(20f, 0f, 22f), Vector3.right, 3.6f, 4f);
        Shutter("Dock warehouse", new Vector3(34f, 0f, 9f), Vector3.right, 3.2f, 3.6f);
        Shutter("A warehouse 1", new Vector3(44f, 0f, 34f), Vector3.back, 3f, 3.2f, 1.4f);
        Shutter("A warehouse 2", new Vector3(49f, 0f, 34f), Vector3.back, 3f, 3.2f, 1.4f);

        // downpipes at the corners of the warehouses: a point on the wall, the normal, the height of the eaves
        foreach (var p in new[] {
                     (new Vector3(-46f, 0f, 15.7f), Vector3.right, 7f), (new Vector3(46f, 0f, 15.7f), Vector3.left, 7f),
                     (new Vector3(-34f, 0f, 15.7f), Vector3.left, 9f), (new Vector3(-12.3f, 0f, 16f), Vector3.forward, 9f),
                     (new Vector3(-25.7f, 0f, 21f), Vector3.back, 9f), (new Vector3(-4.3f, 0f, 21f), Vector3.back, 9f),
                     (new Vector3(4f, 0f, 27.7f), Vector3.left, 10f), (new Vector3(4f, 0f, 8.3f), Vector3.left, 10f),
                     (new Vector3(20f, 0f, 8.3f), Vector3.right, 10f), (new Vector3(34f, 0f, 15.7f), Vector3.right, 8f),
                     (new Vector3(26f, 0f, 15.7f), Vector3.left, 8f), (new Vector3(40f, 0f, 34.3f), Vector3.left, 10f) })
            foreach (Frame f in OnWall(p.Item1, p.Item2))
                DecoCylinder(f, "Downpipe", new Vector3(0f, (0.9f + p.Item3 - 0.15f) * 0.5f, 0.1f), new Vector3(0.12f, p.Item3 - 1.05f, 0.12f),
                             SteelGrey, 1f, Vector3.zero);

        // A short: buttresses on the long concrete wall (tight against it, with colliders)
        foreach (Frame f in Both)
            foreach (float x in new[] { 14f, 18.5f, 23f })
                Solid(f, "A short buttress", new Vector3(x, 3.5f, 2.075f), new Vector3(0.5f, 7f, 0.15f), "Concrete046", 3f, Penetrable.Impenetrable);
    }

    // footing and roof of a corrugated warehouse of the table
    static void Warehouse(MapKit.Row r)
    {
        foreach (Frame f in At(r.center, 0f, r.twin))
            Deco(f, r.name + " footing", new Vector3(0f, 0.45f - r.center.y, 0f), new Vector3(r.size.x + 0.12f, 0.9f, r.size.z + 0.12f), "Concrete031", 3f);
        MapKit.Roof(r, "CorrugatedSteel005", 3f, 8f, 0.3f);
    }

    // a concrete band on top of a block, "over" metres out from the walls
    static void Coping(MapKit.Row r, float over)
    {
        foreach (Frame f in At(r.center, 0f, r.twin))
            Deco(f, r.name + " coping", new Vector3(0f, r.size.y * 0.5f, 0f), new Vector3(r.size.x + 2f * over, 0.3f, r.size.z + 2f * over), "Concrete012", 3f);
    }

    static void Strip(string name, Vector3 face, Vector3 normal, float length, float[] heights)
    {
        Windows(name, face, normal, length, heights, 1.6f, 1.0f, 4.5f);
    }

    static void Rows(string name, Vector3 face, Vector3 normal, float length, float[] heights)
    {
        Windows(name, face, normal, length, heights, 1.4f, 1.2f, 3.5f);
    }

    // windows (dark glass, a mullion, a concrete sill) every "spacing" metres along a face, centred on "face",
    // at least 0.8 m from its ends; length 0 = one window at "face"
    static void Windows(string name, Vector3 face, Vector3 normal, float length, float[] heights, float w, float h, float spacing)
    {
        int n = length <= 0f ? 1 : Mathf.FloorToInt((length - w - 1.6f) / spacing) + 1;
        foreach (Frame f in OnWall(face, normal))
            foreach (float y in heights)
                for (int k = 0; k < n; k++)
                {
                    float u = (k - (n - 1) * 0.5f) * spacing;
                    Deco(f, name + " window", new Vector3(u, y, 0.02f), new Vector3(w, h, 0.04f), Glass, 1f, false);
                    Deco(f, name + " mullion", new Vector3(u, y, 0.03f), new Vector3(0.06f, h, 0.06f), WindowFrame, 1f, false);
                    Deco(f, name + " sill", new Vector3(u, y - h * 0.5f - 0.04f, 0.06f), new Vector3(w + 0.2f, 0.08f, 0.12f), "Concrete012", 1f, false);
                }
    }

    // a steel door in its frame (point = the middle of its bottom on the wall), a lamp above it if asked
    static void Door(string name, Vector3 point, Vector3 normal, bool lamp, float width = 1.1f, float height = 2.2f)
    {
        foreach (Frame f in OnWall(point, normal))
        {
            Deco(f, name + " door frame", new Vector3(0f, height * 0.5f + 0.03f, 0.03f), new Vector3(width + 0.2f, height + 0.1f, 0.06f), WindowFrame, 1f, false);
            Deco(f, name + " door", new Vector3(0f, height * 0.5f, 0.05f), new Vector3(width, height, 0.04f), DoorPaint, 1f, false);
            Deco(f, name + " door handle", new Vector3(width * 0.35f, 1.05f, 0.09f), new Vector3(0.12f, 0.03f, 0.04f), SteelGrey, 1f, false);
        }
        if (lamp) WallProp(name + " door lamp", "industrial_wall_lamp", point + Vector3.up * (height + 0.25f), normal);
    }

    // a roller shutter: horizontal slats (the corrugated sheet turned by 90 degrees), the box of the roll above it
    // and two guide rails; floor = the height of its bottom
    static void Shutter(string name, Vector3 point, Vector3 normal, float width, float height, float floor = 0f)
    {
        foreach (Frame f in OnWall(point, normal))
        {
            Deco(f, name + " shutter", new Vector3(0f, floor + height * 0.5f, 0.03f), new Vector3(height, width, 0.04f),
                 "CorrugatedSteel005", 0.8f, new Vector3(0f, 0f, 90f), false);
            Deco(f, name + " shutter box", new Vector3(0f, floor + height + 0.22f, 0.18f), new Vector3(width + 0.3f, 0.44f, 0.36f), SteelGrey, 1f);
            foreach (float s in Sides)
                Deco(f, name + " shutter guide", new Vector3(s * (width * 0.5f + 0.06f), floor + height * 0.5f, 0.05f),
                     new Vector3(0.12f, height, 0.1f), SteelGrey, 1f, false);
        }
    }

    // ---------------- outer walls, ground ----------------

    static void OuterWalls()
    {
        // concrete pilasters on the north wall and steel posts on the corrugated east wall (not where buildings, the
        // catwalk or the pipe rack stand against them); thin colliders, tight against the walls
        foreach (Frame f in Both)
        {
            foreach (float x in new[] { -48f, -40f, -32f, -24f, -16f, -8f, 8f, 16f, 24f })
                Solid(f, "Wall N pilaster", new Vector3(x, 6f, 39.85f), new Vector3(0.7f, 12f, 0.3f), "Concrete046", 3f, Penetrable.Impenetrable);
            foreach (float z in new[] { -36f, -20f, 20f, 28f })
                Solid(f, "Wall E post", new Vector3(51.85f, 6f, z), new Vector3(0.3f, 12f, 0.35f), "Metal022", 1.5f, Penetrable.Impenetrable);
            Deco(f, "Wall N coping", new Vector3(0f, 12.1f, 40.5f), new Vector3(106f, 0.2f, 1.2f), "Concrete012", 3f);
        }
    }

    static void Ground()
    {
        foreach (Frame f in Both)
        {
            // the spawn yard in front of the office: concrete slabs
            Deco(f, "Spawn slab", new Vector3(0f, 0.01f, 31f), new Vector3(28f, 0.02f, 6f), "Concrete031", 3f);
            // the mid road: a dashed centre line
            foreach (float z in new[] { 10f, 14f, 18f, 25.6f })
                Deco(f, "Mid road line", new Vector3(0f, 0.006f, z), new Vector3(0.15f, 0.012f, 2f), WhitePaint, 1f, false);
            // A: truck bays in front of the dock
            foreach (float x in new[] { 31f, 35f, 39f, 43f })
                Deco(f, "A bay line", new Vector3(x, 0.006f, 23.3f), new Vector3(0.12f, 0.012f, 4.6f), YellowPaint, 1f, false);
            // the parking place of the car at the spawn
            Deco(f, "Parking line 1", new Vector3(-10f, 0.006f, 35.7f), new Vector3(6f, 0.012f, 0.12f), WhitePaint, 1f, false);
            Deco(f, "Parking line 2", new Vector3(-10f, 0.006f, 38.3f), new Vector3(6f, 0.012f, 0.12f), WhitePaint, 1f, false);
            Deco(f, "Parking line 3", new Vector3(-13f, 0.006f, 37f), new Vector3(0.12f, 0.012f, 2.72f), WhitePaint, 1f, false);
        }
    }

    // ---------------- sites ----------------

    static void SiteA()
    {
        // the solid concrete under the dock ramp (the ramp row is a 0.3 m slab), 2 cm under its top
        Quaternion ramp = Quaternion.Euler(-13.13f, 90f, 0f);
        Vector3 fill = new Vector3(27.034f, 0.554f, 30f) - ramp * Vector3.up * 0.67f;
        foreach (Frame f in Both)
        {
            Deco(f, "A dock ramp fill", fill, new Vector3(5.98f, 1.6f, 6.161f), "Concrete044D", 3f, ramp.eulerAngles);
            // rubber bumpers on the face of the dock
            foreach (float x in new[] { 31.5f, 34.5f, 37.5f, 40.5f, 43.5f, 51f })
                Deco(f, "A dock bumper", new Vector3(x, 0.75f, 25.92f), new Vector3(0.45f, 0.5f, 0.16f), Rubber, 1f);
            // catwalk: knee braces from the wall to the front of the deck, a hazard band on its edge, a handrail on the rail
            foreach (float x in new[] { 30.2f, 34.8f, 39.8f })
                Beam(f, "A catwalk brace", new Vector3(x, 2.9f, 39.6f), new Vector3(x, 4.15f, 37.15f), 0.12f, 0.14f, "Metal022");
            Deco(f, "A catwalk edge", new Vector3(35f, 4.3f, 36.985f), new Vector3(10f, 0.2f, 0.03f), "PaintedMetal016", 1f, false);
            Deco(f, "A catwalk handrail", new Vector3(36.5f, 5.43f, 37f), new Vector3(7.1f, 0.06f, 0.16f), YellowPaint, 1f);
        }
        StairRails("A dock stairs", new Vector3(48f, 0f, 23.2f), new Vector3(48f, 1.4f, 26f), 3f, false);
        StairRails("A catwalk stairs", new Vector3(32f, 1.4f, 30.5f), new Vector3(32f, 4.4f, 37f), 2f, true);
    }

    static void SiteB()
    {
        foreach (var t in new[] { ("B tank 1", new Vector3(-44f, 0f, 30f), 8f, 9f), ("B tank 2", new Vector3(-33.5f, 0f, 37f), 5f, 6f) })
        {
            string n = t.Item1;
            Vector3 c = t.Item2;
            float d = t.Item3, h = t.Item4;
            // a concrete base ring the player can step on (a collider: the feet must not sink into it)
            MapKit.TwinCylinder(n + " base", c + Vector3.up * 0.15f, new Vector3(d + 0.6f, 0.3f, d + 0.6f), Vector3.zero, "Concrete031", 3f,
                                MapKit.Col.Yes, Penetrable.Impenetrable, MapKit.Nav.Block);
            foreach (Frame f in Both)
            {
                foreach (float y in new[] { h / 3f, h * 2f / 3f })
                    DecoCylinder(f, n + " ring", c + Vector3.up * y, new Vector3(d + 0.12f, 0.16f, d + 0.12f), "Metal022", 3f, Vector3.zero);
                DecoCylinder(f, n + " cap", c + Vector3.up * (h + 0.11f), new Vector3(d + 0.2f, 0.22f, d + 0.2f), SteelGrey, 1f, Vector3.zero);
                DecoCylinder(f, n + " dome", c + Vector3.up * (h + 0.47f), new Vector3(d * 0.55f, 0.5f, d * 0.55f), "Metal022", 3f, Vector3.zero);
            }
        }
        foreach (Frame f in Both)
        {
            // a pipe from tank 1 up and over to the pipe rack, and the pipes on the rack
            Pipe(f, "B tank pipe up", new Vector3(-48f, 0.3f, 31f), new Vector3(-48f, 4.75f, 31f), 0.3f, SteelGrey);
            Pipe(f, "B tank pipe over", new Vector3(-47.9f, 4.6f, 31f), new Vector3(-51.75f, 4.6f, 31f), 0.3f, SteelGrey);
            Pipe(f, "B tank pipe down", new Vector3(-51.6f, 4.75f, 31f), new Vector3(-51.6f, 3.2f, 31f), 0.3f, SteelGrey);
            Pipe(f, "B rack pipe 1", new Vector3(-51.86f, 3.14f, 21.7f), new Vector3(-51.86f, 3.14f, 34.3f), 0.28f, "Metal022");
            Pipe(f, "B rack pipe 2", new Vector3(-51.54f, 3.14f, 21.7f), new Vector3(-51.54f, 3.14f, 34.3f), 0.28f, SteelGrey);
            Pipe(f, "B rack pipe 3", new Vector3(-51.7f, 3.42f, 21.7f), new Vector3(-51.7f, 3.42f, 34.3f), 0.2f, YellowPaint);
            Pipe(f, "B rack pipe drop", new Vector3(-51.7f, 3.14f, 21.85f), new Vector3(-51.7f, 0f, 21.85f), 0.22f, SteelGrey);
            // platform: hazard bands on the open edges, handrails on the steel rails, beams under the deck
            Deco(f, "B platform edge N", new Vector3(-33f, 2.9f, 26.015f), new Vector3(6f, 0.2f, 0.03f), "PaintedMetal016", 1f, false);
            Deco(f, "B platform edge E", new Vector3(-27.985f, 2.9f, 24f), new Vector3(0.03f, 0.2f, 4f), "PaintedMetal016", 1f, false);
            Deco(f, "B platform handrail S", new Vector3(-33f, 4.03f, 22.05f), new Vector3(6.1f, 0.06f, 0.16f), YellowPaint, 1f);
            Deco(f, "B platform handrail W", new Vector3(-35.95f, 4.03f, 24.05f), new Vector3(0.16f, 0.06f, 4f), YellowPaint, 1f);
            foreach (float z in new[] { 22.2f, 25.8f })
                Deco(f, "B platform beam", new Vector3(-32f, 2.71f, z), new Vector3(7.6f, 0.18f, 0.14f), "Metal022", 1f);
        }
        StairRails("B platform stairs", new Vector3(-29f, 0f, 31f), new Vector3(-29f, 3f, 26f), 2f, true);
    }

    // stringers (steel stairs) and yellow handrails on both sides of a staircase made by MapKit.Stairs
    static void StairRails(string name, Vector3 low, Vector3 high, float width, bool stringers)
    {
        Vector3 d = high - low;
        float run = new Vector2(d.x, d.z).magnitude, rise = d.y, length = d.magnitude;
        Vector3 slope = new Vector3(-Mathf.Atan2(rise, run) * Mathf.Rad2Deg, 0f, 0f);
        // own axes: up the stairs along +z from the low point
        foreach (Frame f in At(low, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg))
            foreach (float s in Sides)
            {
                float x = s * (width * 0.5f + 0.04f);
                if (stringers)
                    Deco(f, name + " stringer", new Vector3(x, rise * 0.5f + 0.06f, run * 0.5f), new Vector3(0.08f, 0.42f, length + 0.1f), "Metal022", 1f, slope);
                Deco(f, name + " handrail", new Vector3(x, rise * 0.5f + 1f, run * 0.5f), new Vector3(0.05f, 0.05f, length), YellowPaint, 1f, slope);
                foreach (float t in new[] { 0.06f, 0.5f, 0.94f })
                    Deco(f, name + " handrail post", new Vector3(x, rise * t + 0.55f, run * t), new Vector3(0.05f, 0.9f, 0.05f), YellowPaint, 1f);
            }
    }

    static void Middle()
    {
        foreach (Frame f in Both)
        {
            // the gate to the mid road: a sliding steel gate pushed open in front of its wall
            Deco(f, "Mid gate leaf", new Vector3(-0.25f, 1.35f, 22.655f), new Vector3(2.5f, 2.7f, 0.05f), "Metal022", 1.5f);
            Deco(f, "Mid gate track", new Vector3(0.5f, 2.78f, 22.64f), new Vector3(5.2f, 0.08f, 0.08f), SteelGrey, 1f, false);
        }
        WallProp("Mid gate lamp", "industrial_wall_lamp", new Vector3(2.25f, 2.86f, 22.7f), Vector3.back);
    }

    // ---------------- props ----------------

    static void Props()
    {
        // mid yard: drums and a stack of tyres in the corners
        MapKit.PropSolid("Mid drum 1", "Barrel_01", new Vector3(9.5f, 0f, -6.8f), 0f, new Vector3(0.6f, 0.9f, 0.6f), 0.5f, true, true);
        MapKit.PropSolid("Mid drum 2", "Barrel_01", new Vector3(10.1f, 0f, -6.4f), 40f, new Vector3(0.6f, 0.9f, 0.6f), 0.5f, true, true);
        for (int i = 0; i < 4; i++)
            MapKit.Prop("Mid tyre " + (i + 1), "old_tyre", new Vector3(-10.5f, 0.165f * i, 7.2f), new Vector3(90f, 35f * i, 0f), true);
        Blockers("Mid tyres collider", new Vector3(-10.5f, 0.33f, 7.2f), new Vector3(0.62f, 0.66f, 0.62f), 0.5f, true);

        // A short and the east lane
        MapKit.PropSolid("A short bins", "metal_trash_can", new Vector3(17f, 0f, 2.35f), 0f, new Vector3(1.85f, 0.91f, 0.56f), 0.5f, false, true);
        MapKit.PropSolid("East lane drum 1", "barrel_03", new Vector3(45.2f, 0f, 2.5f), 0f, new Vector3(0.64f, 0.93f, 0.64f), 0.5f, true, true);
        MapKit.PropSolid("East lane drum 2", "barrel_03", new Vector3(45.2f, 0f, 3.2f), 70f, new Vector3(0.64f, 0.93f, 0.64f), 0.5f, true, true);

        // A: the dock, the back under the catwalk
        MapKit.PropSolid("A dock crate", "wooden_military_crate", new Vector3(44f, 1.4f, 32.8f), 0f, new Vector3(1.24f, 0.47f, 0.52f), 0.5f, false, true);
        MapKit.PropSolid("A buoy", "ocean_buoy", new Vector3(51f, 1.4f, 27f), 0f, new Vector3(1.0f, 2.66f, 0.95f), 1.2f, true, true);
        MapKit.Prop("A crate on the stack", "wooden_crate_01", new Vector3(44f, 2.4f, 21.5f), 15f, true);
        WallProp("A lifebuoy", "lifebuoy", new Vector3(52f, 2f, 22f), Vector3.left);
        MapKit.PropSolid("A rack", "worn_metal_rack", new Vector3(37.5f, 0f, 39.68f), 180f, new Vector3(0.92f, 1.9f, 0.6f), 0.5f, false, true);
        MapKit.Prop("A catwalk lamp", "hanging_industrial_lamp", new Vector3(35f, 2.84f, 38.5f), 0f, true);
        MapKit.PropSolid("A back crates", "old_military_crate", new Vector3(25f, 0f, 38.5f), 0f, new Vector3(1.82f, 0.31f, 0.98f), 0.5f, false, true);

        // B: tank farm
        MapKit.PropSolid("B generator", "portable_generator", new Vector3(-37.5f, 0f, 33f), 20f, new Vector3(0.82f, 0.58f, 0.56f), 0.8f, false, true);
        Vector3[] propane = { new Vector3(-30.5f, 0f, 38.8f), new Vector3(-30.05f, 0f, 39.35f), new Vector3(-30.95f, 0f, 39.4f) };
        for (int i = 0; i < propane.Length; i++)
            MapKit.PropSolid("B propane " + (i + 1), "propane_tank", propane[i], 30f * i, new Vector3(0.34f, 0.56f, 0.34f), 0.5f, true, true);
        MapKit.Prop("B pipes on the wall", "modular_industrial_pipes_01", new Vector3(-20f, 2.6f, 16.17f), 0f, true);
        MapKit.Prop("A short N pipes", "modular_industrial_pipes_01", new Vector3(25.83f, 1.2f, 12.5f), -90f, true);

        // CT spawn
        MapKit.PropSolid("CT car", "covered_car", new Vector3(-10f, 0f, 37f), 90f, new Vector3(1.8f, 1.45f, 4.4f), 0.8f, false, true);
        MapKit.PropSolid("CT utility box", "utility_box_02", new Vector3(-12f, 0f, 39.75f), 180f, new Vector3(0.92f, 1.12f, 0.43f), 0.5f, false, true);
        MapKit.PropSolid("CT drum 1", "Barrel_02", new Vector3(12.6f, 0f, 39.62f), 0f, new Vector3(0.5f, 0.88f, 0.5f), 0.5f, true, true);
        MapKit.PropSolid("CT drum 2", "Barrel_02", new Vector3(13.2f, 0f, 39.5f), 50f, new Vector3(0.5f, 0.88f, 0.5f), 0.5f, true, true);

        // floodlights high on the walls
        WallProp("A warehouse floodlight", "security_light", new Vector3(41.5f, 6f, 34f), Vector3.back);
        WallProp("EC floodlight W", "security_light", new Vector3(4f, 6f, 18f), Vector3.left);
        WallProp("EC floodlight S", "security_light", new Vector3(8f, 6.2f, 8f), Vector3.back);
        WallProp("Office floodlight", "security_light", new Vector3(1.75f, 5.9f, 34f), Vector3.back);
        WallProp("Mid low floodlight", "security_light", new Vector3(-8f, 5.4f, 8f), Vector3.back);
    }

    // ---------------- painted signs (not twinned: the sites are on the CT half) ----------------

    // strokes of the letters in a cell 1 m high: (u0, v0, u1, v1) each, u to the right of the viewer
    static readonly Dictionary<char, float[]> Glyphs = new Dictionary<char, float[]>
    {
        { 'A', new[] { 0f, 0f, 0.4f, 1f, 0.8f, 0f, 0.4f, 1f, 0.2f, 0.38f, 0.6f, 0.38f } },
        { 'B', new[] { 0f, 0f, 0f, 1f, 0f, 1f, 0.48f, 1f, 0.48f, 1f, 0.62f, 0.86f, 0.62f, 0.86f, 0.62f, 0.66f, 0.62f, 0.66f, 0.49f, 0.53f,
                       0f, 0.53f, 0.5f, 0.53f, 0.5f, 0.53f, 0.68f, 0.38f, 0.68f, 0.38f, 0.68f, 0.14f, 0.68f, 0.14f, 0.54f, 0f, 0.54f, 0f, 0f, 0f } },
        { '>', new[] { 0f, 0.5f, 0.86f, 0.5f, 0.9f, 0.5f, 0.6f, 0.8f, 0.9f, 0.5f, 0.6f, 0.2f } },
        { '<', new[] { 0.04f, 0.5f, 0.9f, 0.5f, 0f, 0.5f, 0.3f, 0.8f, 0f, 0.5f, 0.3f, 0.2f } },
    };

    static void Signs()
    {
        Sign("Sign A", new Vector3(46.5f, 6f, 34f), Vector3.back, "A", 2.8f);
        Sign("Sign B", new Vector3(-26f, 4.5f, 31f), Vector3.left, "B", 3.2f);
        // at the spawns: which way the sites are (the CT look south, the T north)
        Sign("CT sign B", new Vector3(-9f, 4f, 28f), Vector3.forward, "B>", 1.6f);
        Sign("CT sign A", new Vector3(9f, 4f, 28f), Vector3.forward, "<A", 1.6f);
        Sign("T sign B", new Vector3(-9f, 4f, -28f), Vector3.back, "<B", 1.6f);
        Sign("T sign A", new Vector3(9f, 4f, -28f), Vector3.back, "A>", 1.6f);
    }

    static float GlyphWidth(char c)
    {
        float w = 0f;
        float[] s = Glyphs[c];
        for (int i = 0; i < s.Length; i += 2) w = Mathf.Max(w, s[i]);
        return w;
    }

    // painted text on a wall, "size" metres high, centred on "centre" (on the wall)
    static void Sign(string name, Vector3 centre, Vector3 normal, string text, float size)
    {
        const float stroke = 0.13f, gap = 0.28f;
        float total = -gap;
        foreach (char c in text) total += GlyphWidth(c) + gap;
        foreach (Frame f in OnWall(centre, normal, false))
        {
            float u0 = -total * 0.5f;
            foreach (char c in text)
            {
                float[] s = Glyphs[c];
                for (int i = 0; i < s.Length; i += 4)
                {
                    float du = s[i + 2] - s[i], dv = s[i + 3] - s[i + 1];
                    float len = Mathf.Sqrt(du * du + dv * dv);
                    float u = u0 + (s[i] + s[i + 2]) * 0.5f, v = (s[i + 1] + s[i + 3]) * 0.5f - 0.5f;
                    // the frame's z points out of the wall, so the viewer's right is its -x
                    Deco(f, name + " " + c, new Vector3(-u * size, v * size, 0.012f), new Vector3(stroke * size, (len + stroke) * size, 0.02f),
                         SignPaint, 1f, new Vector3(0f, 0f, Mathf.Atan2(du, dv) * Mathf.Rad2Deg), false);
                }
                u0 += GlyphWidth(c) + gap;
            }
        }
    }

    // ---------------- backdrop outside the walls (no shadows, nothing to touch) ----------------

    static void Backdrop()
    {
        // container cranes on the quays east and west, grain silos north and south
        foreach (Vector3 p in new[] { new Vector3(66f, 0f, 14f), new Vector3(66f, 0f, -14f) })
            foreach (Frame f in At(p, 0f))
                Crane(f);
        foreach (Frame f in Both)
        {
            foreach (float x in new[] { -20f, -10.5f, -1f })
                DecoCylinder(f, "Silo", new Vector3(x, 15f, 58f), new Vector3(9f, 30f, 9f), "Concrete012", 4f, Vector3.zero, false);
            Deco(f, "Silo gallery", new Vector3(-10.5f, 31.4f, 58f), new Vector3(28f, 2.8f, 3.5f), "CorrugatedSteel005", 3f, false);
            Deco(f, "Silo tower", new Vector3(7f, 21f, 58f), new Vector3(5f, 42f, 5f), "Concrete012", 4f, false);
        }
    }

    // a container crane in its own axes: the boom along +x (out over the water), the rails along z
    static void Crane(Frame f)
    {
        foreach (float sx in Sides)
        {
            foreach (float sz in Sides)
                Deco(f, "Crane leg", new Vector3(sx * 6f, 15f, sz * 7f), new Vector3(1.2f, 30f, 1.2f), CranePaint, 4f, false);
            Deco(f, "Crane portal", new Vector3(sx * 6f, 12f, 0f), new Vector3(1f, 1f, 14f), CranePaint, 4f, false);
            Deco(f, "Crane sill", new Vector3(sx * 6f, 30.8f, 0f), new Vector3(1.4f, 1.6f, 15.4f), CranePaint, 4f, false);
            Deco(f, "Crane girder", new Vector3(0f, 30.8f, sx * 7f), new Vector3(13.2f, 1.6f, 1.4f), CranePaint, 4f, false);
            Deco(f, "Crane apex", new Vector3(-4f, 41f, sx * 1.4f), new Vector3(1f, 10f, 1f), CranePaint, 4f, false);
        }
        Deco(f, "Crane boom", new Vector3(20f, 35.2f, 0f), new Vector3(60f, 2.2f, 3f), CranePaint, 4f, false);
        Beam(f, "Crane forestay", new Vector3(-4f, 46f, 0f), new Vector3(40f, 36.3f, 0f), 0.6f, 0.6f, CranePaint, false);
        Beam(f, "Crane backstay", new Vector3(-4f, 46f, 0f), new Vector3(-9.5f, 36.3f, 0f), 0.6f, 0.6f, CranePaint, false);
        Deco(f, "Crane house", new Vector3(-6f, 38.5f, 0f), new Vector3(8f, 4.6f, 5.2f), CraneWhite, 4f, false);
        Deco(f, "Crane cab", new Vector3(8f, 33.1f, 0f), new Vector3(2.2f, 2f, 2.2f), CraneWhite, 4f, false);
    }
}
