using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Tools > Build Borgo Map: makes Assets/Scenes/BorgoMap.unity, a sunny Italian town of 96 x 76 m in the spirit of
// CS 2 (maps_report section 3): a fountain plaza in mid, "short A" and the "B passage" from the plaza, the outer lanes
// "long A" and "B lane", bombsite A on a stone terrace with a ramp and stairs, bombsite B with a well and a wooden
// balcony, a church at each spawn. The layout was checked for reachability, balance and sightlines (longest 56.5 m).
// Built with MapKit: the table below, then stairs, roofs, plinths, windows with shutters, doors, lamps and props.
// The map is point symmetric: every row with T has a twin at (-x, -z), so both teams have the same chances; only the
// colours of the twins differ. T spawn is south (-z), CT spawn north (+z); both bombsites are on the CT half.
// Textures: ambientCG; props and sky: Poly Haven (all CC0, see CREDITS.md).
public static class BorgoMapBuilder
{
    // window grid of the facades (MapKit.Facade): one window every 3 m, rows at these heights where they fit
    const float WindowSpacing = 3f;
    static readonly float[] HouseFloors = { 4.2f, 7.0f };
    static readonly float[] WallFloors = { 4.2f, 7.0f, 9.8f, 12.6f };

    // name | kind | centre | size | rotation | texture [texture of the twin] | collider | penetrable | nav | twin | tile
    // (maps_report section 3; the outer walls are cut into houses of different heights, see below)
    const string Layout = @"
        Ground|box|(0, -0.25, 0)|(96, 0.5, 76)|0|PavingStones119|yes|100|walk|-|2.3
        Plaza paving|box|(0, 0.01, 0)|(26, 0.02, 16)|0|PavingStones151|no|100|none|-|5.4

        // outer walls: a row of houses 11-13.5 m high and 6 m deep instead of one 11 m wall; the inner faces stay where
        // the walls were (z = 38, x = 48 and their twins), so the layout does not change
        Wall N 1|box|(-43.5, 6, 41)|(11, 12, 6)|0|PaintedPlaster006 [PaintedPlaster018]|yes|100|block|T|3
        Wall N 2|box|(-32, 5.5, 41)|(12, 11, 6)|0|PaintedPlaster018 [PaintedPlaster013]|yes|100|block|T|3
        Wall N 3|box|(-18, 6.5, 41)|(16, 13, 6)|0|PaintedPlaster006 [PaintedPlaster018]|yes|100|block|T|3
        Wall N 4|box|(-5, 5.75, 41)|(10, 11.5, 6)|0|PaintedPlaster013 [PaintedPlaster006]|yes|100|block|T|3
        Wall N 5|box|(5, 6.25, 41)|(10, 12.5, 6)|0|PaintedPlaster018 [PaintedPlaster013]|yes|100|block|T|3
        Wall N 6|box|(18, 6.75, 41)|(16, 13.5, 6)|0|PaintedPlaster006 [PaintedPlaster013]|yes|100|block|T|3
        Wall N 7|box|(31.5, 5.5, 41)|(11, 11, 6)|0|PaintedPlaster013 [PaintedPlaster018]|yes|100|block|T|3
        Wall N 8|box|(43, 6.25, 41)|(12, 12.5, 6)|0|PaintedPlaster018 [PaintedPlaster006]|yes|100|block|T|3
        Wall E 1|box|(51, 6, 32)|(6, 12, 12)|0|PaintedPlaster006 [PaintedPlaster013]|yes|100|block|T|3
        Wall E 2|box|(51, 6.75, 21)|(6, 13.5, 10)|0|PaintedPlaster013 [PaintedPlaster018]|yes|100|block|T|3
        Wall E 3|box|(51, 5.75, 10)|(6, 11.5, 12)|0|PaintedPlaster018 [PaintedPlaster006]|yes|100|block|T|3
        Wall E 4|box|(51, 6.25, 0)|(6, 12.5, 8)|0|PaintedPlaster006 [PaintedPlaster013]|yes|100|block|T|3
        Wall E 5|box|(51, 5.5, -10)|(6, 11, 12)|0|PaintedPlaster013 [PaintedPlaster006]|yes|100|block|T|3
        Wall E 6|box|(51, 6.5, -21.5)|(6, 13, 11)|0|PaintedPlaster018 [PaintedPlaster013]|yes|100|block|T|3
        Wall E 7|box|(51, 5.75, -32.5)|(6, 11.5, 11)|0|PaintedPlaster006 [PaintedPlaster018]|yes|100|block|T|3

        House mid west|box|(-4.5, 4.25, 18.5)|(11, 8.5, 21)|0|PaintedPlaster018 [PaintedPlaster013]|yes|1.5|block|T|3
        House mid east|box|(8.5, 3.5, 18.5)|(3, 7, 21)|0|PaintedPlaster013 [PaintedPlaster018]|yes|1.5|block|T|3
        House west lane|box|(-42.5, 4.5, 10)|(11, 9, 12)|0|PaintedPlaster006 [PaintedPlaster018]|yes|1.5|block|T|3
        House B lane|box|(-21, 4, 10)|(16, 8, 12)|0|PaintedPlaster013 [PaintedPlaster006]|yes|1.5|block|T|3
        Palazzo east|box|(26.5, 4.5, 10)|(27, 9, 12)|0|PaintedPlaster018 [PaintedPlaster013]|yes|1.5|block|T|3
        Palazzo B|box|(-18, 4.75, 26)|(16, 9.5, 12)|0|PaintedPlaster006 [PaintedPlaster013]|yes|1.5|block|T|3
        Palazzo A|box|(18, 4.75, 26)|(16, 9.5, 12)|0|PaintedPlaster013 [PaintedPlaster006]|yes|1.5|block|T|3
        Row CT-A|box|(18, 5, 37)|(16, 10, 2)|0|PaintedPlaster018 [PaintedPlaster013]|yes|1.5|block|T|3
        Row CT-B|box|(-18, 5, 37)|(16, 10, 2)|0|PaintedPlaster013 [PaintedPlaster018]|yes|1.5|block|T|3
        House centre|box|(21, 4, 0)|(16, 8, 8)|0|Concrete012|yes|1.5|block|T|3
        CT church|box|(0, 5.5, 35)|(7.2, 11, 6)|0|Plaster007|yes|1.5|block|T|3
        Church tower|box|(-2.1, 13.5, 36.5)|(3, 5, 3)|0|Plaster007|yes|100|block|T|3

        // bombsite A: the stone terrace +1.2 m with its ramp, stairs (visual steps below) and parapets
        A terrace|box|(42, 0.6, 32)|(12, 1.2, 12)|0|Concrete012|yes|2.5|walk|T|3
        A ramp|box|(43, 0.453, 23.029)|(6, 0.3, 6.119)|(-11.31, 0, 0)|Concrete012|yes|100|walk|T|3
        A stairs collider|box|(34.537, 0.507, 32)|(4, 0.2, 3.231)|(-21.8, 90, 0)|Concrete012|yes, invisible|100|walk|T|3
        A parapet S1|box|(38, 1.65, 26.2)|(4, 0.9, 0.4)|0|Concrete012|yes|2.5|block|T|1.5
        A parapet S2|box|(47, 1.65, 26.2)|(2, 0.9, 0.4)|0|Concrete012|yes|2.5|block|T|1.5
        A parapet W1|box|(36.2, 1.65, 28.2)|(0.4, 0.9, 3.6)|0|Concrete012|yes|2.5|block|T|1.5
        A parapet W2|box|(36.2, 1.65, 36)|(0.4, 0.9, 4)|0|Concrete012|yes|2.5|block|T|1.5

        // bombsite B: the wooden balcony +3 m on the west face of Palazzo B, its stairs of stone (a solid flight)
        B balcony|box|(-27.5, 2.875, 24)|(3, 0.25, 8)|0|Planks039|yes|0.5|walk|T|1.5
        B balcony post 1|box|(-28.85, 1.375, 20.15)|(0.3, 2.75, 0.3)|0|Planks039|yes|0.5|block|T|1.5
        B balcony post 2|box|(-28.85, 1.375, 25.85)|(0.3, 2.75, 0.3)|0|Planks039|yes|0.5|block|T|1.5
        B balcony rail W|box|(-28.925, 3.5, 23)|(0.15, 1, 6)|0|Planks039|yes|0.5|block|T|1
        B balcony rail S|box|(-27.5, 3.5, 20.075)|(3, 1, 0.15)|0|Planks039|yes|0.5|block|T|1
        B balcony stairs collider|box|(-31.702, 1.412, 27)|(2, 0.2, 6.265)|(-28.61, 90, 0)|Concrete012|yes, invisible|100|walk|T|3

        Fountain basin|cyl|(0, 0.4, 0)|(6, 0.8, 6)|0|Concrete012|yes|2.5|block|-|3
        Fountain column|cyl|(0, 2.1, 0)|(1.8, 2.6, 1.8)|0|Concrete012|yes|2.5|block|-|3
        Fountain water|cyl|(0, 0.73, 0)|(5.5, 0.06, 5.5)|0|color:#3b6f86|no|100|none|-|3

        // mid doors in each mid alley, arches into the sites, the lintel of short A and the vault of the B passage
        Mid door wall W|box|(1.75, 1.7, 19)|(1.5, 3.4, 0.6)|0|PaintedPlaster006|yes|1.5|block|T|2
        Mid door wall E|box|(6.25, 1.7, 19)|(1.5, 3.4, 0.6)|0|PaintedPlaster006|yes|1.5|block|T|2
        Mid door lintel|box|(4, 3, 19)|(3, 0.8, 0.6)|0|PaintedPlaster006|yes|1.5|block|T|2
        B arch pillar 1|box|(-36, 2.3, 16)|(2, 4.6, 1)|0|Bricks084|yes|3|block|T|2
        B arch pillar 2|box|(-30, 2.3, 16)|(2, 4.6, 1)|0|Bricks084|yes|3|block|T|2
        B arch lintel|box|(-33, 4, 16)|(4, 1.2, 1)|0|Bricks084|yes|3|block|T|2
        Long A arch pillar 1|box|(41, 2.3, 16)|(2, 4.6, 1)|0|Bricks084|yes|3|block|T|2
        Long A arch pillar 2|box|(47, 2.3, 16)|(2, 4.6, 1)|0|Bricks084|yes|3|block|T|2
        Long A arch lintel|box|(44, 4, 16)|(4, 1.2, 1)|0|Bricks084|yes|3|block|T|2
        Short A lintel|box|(26, 3.5, 18)|(1, 1, 4)|0|Bricks084|yes|3|block|T|2
        B passage vault|box|(-17.5, 3.4, 18)|(9, 0.4, 4)|0|Concrete012|yes|2.5|block|T|2

        // cover
        A crate 1|box|(30.5, 0.6, 22.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        A crate 2|box|(30.5, 1.8, 22.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        A crate 3|box|(31.8, 0.5, 22.4)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        A low wall|box|(30.5, 0.55, 28)|(5, 1.1, 0.8)|0|Concrete012|yes|2.5|block|T|1.5
        A cart|box|(31, 0.65, 36.2)|(2.4, 1.3, 1.2)|0|Planks039|yes|0.5|block|T|1.2
        A terrace crate 1|box|(45, 1.7, 35)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        A terrace crate 2|box|(46.1, 1.7, 35)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        A planter|box|(38, 1.6, 28.5)|(1.6, 0.8, 0.8)|0|Concrete012|yes|2.5|block|T|1
        B well|cyl|(-40, 0.5, 27)|(2.4, 1, 2.4)|0|Concrete012|yes|2.5|block|T|1.2
        B crate 1|box|(-45.5, 0.6, 19.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        B crate 2|box|(-45.5, 1.8, 19.5)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        B crate 3|box|(-44.2, 0.5, 19.6)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        B crate 4|box|(-31.5, 0.75, 35.5)|(1.5, 1.5, 1.5)|0|WoodFloor043|yes|0.5|block|T|1.5
        B low wall|box|(-42, 0.55, 34)|(4, 1.1, 0.8)|0|Concrete012|yes|2.5|block|T|1.5
        B barrels|box|(-47, 0.45, 30.5)|(1.2, 0.9, 2.2)|0|prop:wine_barrel_01 x2|yes|0.5|block|T|1
        Long A crate 1|box|(46.4, 0.6, 9)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        Long A crate 2|box|(46.6, 0.5, 10.2)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
        B lane cart|box|(-35, 0.65, 12)|(2.4, 1.3, 1.2)|0|Planks039|yes|0.5|block|T|1.2
        B lane barrels|box|(-29.8, 0.45, 9)|(0.8, 0.9, 2)|0|prop:wine_barrel_01 x2|yes|0.5|block|T|1
        Junction crate|box|(-31, 0.6, 2.6)|(1.2, 1.2, 1.2)|0|WoodFloor043|yes|0.5|block|T|1.2
        Market stall|box|(-11, 0.45, 3)|(3, 0.9, 4)|0|Planks039|yes|0.5|block|T|1.2
        CT mid car|box|(4, 0.725, 13)|(1.8, 1.45, 4.4)|0|prop:covered_car|yes|0.8|block|T|1
        Short A crate|box|(20, 0.5, 19.2)|(1, 1, 1)|0|WoodFloor043|yes|0.5|block|T|1
    ";

    // the buildings that get a roof, a plinth and windows: name, shutter texture, shutter texture of the twin
    static readonly string[,] Houses =
    {
        { "House mid west", "PaintedWood009A", "PaintedWood008A" },
        { "House mid east", "PaintedWood008A", "PaintedWood009A" },
        { "House west lane", "PaintedWood009A", "PaintedWood008A" },
        { "House B lane", "PaintedWood008A", "PaintedWood009A" },
        { "Palazzo east", "PaintedWood009A", "PaintedWood008A" },
        { "Palazzo B", "PaintedWood008A", "PaintedWood009A" },
        { "Palazzo A", "PaintedWood009A", "PaintedWood008A" },
        { "Row CT-A", "PaintedWood008A", "PaintedWood009A" },
        { "Row CT-B", "PaintedWood009A", "PaintedWood008A" },
        { "House centre", "PaintedWood008A", "PaintedWood009A" },
    };

    // the CT spawn points (T: the mirrored points); the look target, the midpoint of both spawns, is the plaza
    static readonly Vector3[] CtSpawns =
    {
        new Vector3(-8f, 1f, 30.5f), new Vector3(-4.5f, 1f, 30.5f), new Vector3(0f, 1f, 30.5f), new Vector3(4.5f, 1f, 30.5f),
        new Vector3(8f, 1f, 30.5f), new Vector3(-8f, 1f, 34f), new Vector3(-5.5f, 1f, 34f), new Vector3(5.5f, 1f, 34f),
        new Vector3(8f, 1f, 34f), new Vector3(-7f, 1f, 36.6f),
    };

    static List<MapKit.Row> rows;
    static readonly List<Bounds> solids = new List<Bounds>();

    [MenuItem("Tools/Build Borgo Map")]
    public static void Build()
    {
        if (!MapKit.Begin("Borgo", "BorgoMap")) return;

        Palette();
        rows = MapKit.Table(Layout);
        solids.Clear();
        foreach (MapKit.Row r in rows)
            if (r.col != MapKit.Col.No && r.euler == Vector3.zero) solids.Add(new Bounds(r.center, r.size));

        // visual steps (the invisible ramps for the player are the "stairs collider" rows of the table)
        MapKit.Stairs("A stairs", new Vector3(33f, 0f, 32f), new Vector3(36f, 1.2f, 32f), 4f, 5, "Concrete012", 1.5f, false, true);
        MapKit.Stairs("B balcony stairs", new Vector3(-34.5f, 0f, 27f), new Vector3(-29f, 3f, 27f), 2f, 12, "Concrete012", 1.5f, false, true);

        Buildings();
        Doors();
        Lamps();
        Props();
        Details();

        MapKit.Spawns(CtSpawns);
        MapKit.Bombsite("Bombsite A", new Vector3(39f, 2.25f, 29f), new Vector3(18f, 4.5f, 18f));
        MapKit.Bombsite("Bombsite B", new Vector3(-38f, 2.25f, 28f), new Vector3(16f, 4.5f, 16f));
        MapKit.SetBounds(48.5f, 38.5f, 45f, 35f);
        MapKit.Overview(new Vector3(0f, 80f, 0f));
        MapKit.Extras(new Vector3(8f, 1f, -30.5f), new Vector3(-4f, 1f, -20f), new Vector3(4f, 1f, 6f));
        MapKit.Sky(MapKit.SkySettings.Borgo());
        MapKit.Finish();
    }

    // ---------------- colours ----------------

    // Large surfaces in mid values and low saturation, so the players stand out (the T models are red-ish, the CT models
    // dark blue): the terracotta and ochre plasters are toned down, the white plaster a little darker.
    static void Palette()
    {
        Tint("PaintedPlaster013", new Color(0.74f, 0.80f, 0.86f));
        Tint("PaintedPlaster018", new Color(0.90f, 0.91f, 0.98f));
        Tint("PaintedPlaster006", new Color(0.88f, 0.88f, 0.88f));
        Tint("Plaster007", new Color(0.92f, 0.92f, 0.92f));
        Tint("RoofingTiles014A", new Color(0.90f, 0.92f, 0.95f));
        Tint("Tiles143", new Color(0.70f, 0.69f, 0.68f));
    }

    static void Tint(string texture, Color c)
    {
        Material m = MapKit.GetMaterial(texture);
        m.SetColor("_BaseColor", c);
        EditorUtility.SetDirty(m);
    }

    // ---------------- buildings: roofs, plinths, windows ----------------

    static void Buildings()
    {
        for (int i = 0; i < Houses.GetLength(0); i++)
        {
            MapKit.Row r = MapKit.RowNamed(rows, Houses[i, 0]);
            MapKit.Roof(r);
            MapKit.Plinth(r);
            Bands(r, Windows(r, MapKit.Faces.All, Houses[i, 1], Houses[i, 2], HouseFloors));
        }

        // the outer houses: windows on the inner face only (the others are outside the map)
        string[] shutters = { "PaintedWood008A", "PaintedWood009A" };
        int n = 0;
        foreach (MapKit.Row r in rows)
        {
            if (!r.name.StartsWith("Wall ")) continue;
            MapKit.Roof(r);
            MapKit.Plinth(r);
            MapKit.Faces inner = r.name.StartsWith("Wall N") ? MapKit.Faces.South : MapKit.Faces.West;
            Bands(r, Windows(r, inner, shutters[n % 2], shutters[(n + 1) % 2], WallFloors));
            n++;
        }

        // the church: plain walls, a roof and a steeper roof on the tower
        MapKit.Row church = MapKit.RowNamed(rows, "CT church");
        MapKit.Roof(church, "RoofingTiles014A", 2.9f, 30f);
        MapKit.Plinth(church);
        MapKit.Roof(MapKit.RowNamed(rows, "Church tower"), "RoofingTiles014A", 2.9f, 45f, 0.3f);
    }

    // Windows with shutters on the faces of a building wherever nothing stands in front of them (another building, an
    // outer house, a lintel, the balcony): the window grid of MapKit.Facade (one every 3 m, centred on the face) is tested
    // window by window, and every run of free windows becomes one MapKit.Facade call on a part of the face, so no window
    // is ever cut by a neighbouring wall. The twin gets the same windows (the map is point symmetric).
    // Returns the faces that got windows.
    static MapKit.Faces Windows(MapKit.Row b, MapKit.Faces faces, string shutters, string twinShutters, float[] floors)
    {
        MapKit.Faces done = MapKit.Faces.None;
        foreach (MapKit.Faces face in new[] { MapKit.Faces.North, MapKit.Faces.East, MapKit.Faces.South, MapKit.Faces.West })
        {
            if ((faces & face) == 0) continue;
            bool alongX = face == MapKit.Faces.North || face == MapKit.Faces.South;
            Vector3 normal = Normal(face);
            Vector3 along = alongX ? Vector3.right : Vector3.forward;
            float length = alongX ? b.size.x : b.size.z;
            float depth = alongX ? b.size.z * 0.5f : b.size.x * 0.5f;
            float top = b.center.y + b.size.y * 0.5f, bottom = b.center.y - b.size.y * 0.5f;
            int n = length < 2.4f ? 0 : Mathf.FloorToInt((length - 2.4f) / WindowSpacing) + 1;

            // which floors each window of the grid has (bit j = floors[j])
            int[] mask = new int[n];
            for (int k = 0; k < n; k++)
            {
                float a = (k - (n - 1) * 0.5f) * WindowSpacing;
                for (int j = 0; j < floors.Length; j++)
                {
                    float y = floors[j];
                    if (y + 0.65f + 0.25f > top || y - 0.65f < bottom + 0.4f) continue;     // the rule of MapKit.Facade
                    Vector3 c = b.center + normal * depth + along * a;
                    // window and shutters: 1.8 m wide, 1.3 m high; in front of the face up to 0.3 m (a vault top
                    // 0.1 m over the sill does not count)
                    Vector3 lo = new Vector3(c.x, y - 0.55f, c.z) - along * 0.9f + normal * 0.02f;
                    Vector3 hi = new Vector3(c.x, y + 0.65f, c.z) + along * 0.9f + normal * 0.3f;
                    if (!Covered(Vector3.Min(lo, hi), Vector3.Max(lo, hi))) mask[k] |= 1 << j;
                }
            }

            // runs of neighbouring windows with the same floors
            int run = 0;
            for (int k0 = 0; k0 < n;)
            {
                int k1 = k0;
                while (k1 + 1 < n && mask[k1 + 1] == mask[k0]) k1++;
                if (mask[k0] != 0)
                {
                    var hs = new List<float>();
                    for (int j = 0; j < floors.Length; j++)
                        if ((mask[k0] & (1 << j)) != 0) hs.Add(floors[j]);
                    float mid = ((k0 + k1) * 0.5f - (n - 1) * 0.5f) * WindowSpacing;
                    float len = 2.4f + WindowSpacing * (k1 - k0);
                    // a part of the building standing on the ground like the building itself (Facade heights are
                    // measured from y = 0), as long as the run and as deep as the building
                    Vector3 size = alongX ? new Vector3(len, b.size.y, b.size.z) : new Vector3(b.size.x, b.size.y, len);
                    MapKit.Facade(b.name + " " + face + " " + (++run), b.center + along * mid, size, face, shutters,
                                  0f, b.twin, twinShutters, hs.ToArray(), WindowSpacing);
                    done |= face;
                }
                k0 = k1 + 1;
            }
        }
        return done;
    }

    // a stone string course 0.25 m high over the ground floor, 0.06 m out of the wall, on the faces that have windows
    // (where a neighbour stands against the face it runs into the neighbour's wall); north and south courses are longer
    // by their depth at both ends, so the corners are closed
    static void Bands(MapKit.Row b, MapKit.Faces faces)
    {
        const float depth = 0.06f, height = 0.25f, y = 3.12f;
        foreach (MapKit.Faces face in new[] { MapKit.Faces.North, MapKit.Faces.East, MapKit.Faces.South, MapKit.Faces.West })
        {
            if ((faces & face) == 0) continue;
            bool alongX = face == MapKit.Faces.North || face == MapKit.Faces.South;
            Vector3 c = OnFace(b, face, alongX ? b.center.x : b.center.z, y, depth * 0.5f);
            Vector3 size = alongX ? new Vector3(b.size.x + 2f * depth, height, depth) : new Vector3(depth, height, b.size.z);
            if (b.twin) MapKit.Twin(b.name + " band " + face, c, size, Vector3.zero, "Concrete012", 1.5f, MapKit.Col.No, Penetrable.Impenetrable, MapKit.Nav.None);
            else MapKit.Box(b.name + " band " + face, c, size, Vector3.zero, "Concrete012", 1.5f, MapKit.Col.No, Penetrable.Impenetrable, MapKit.Nav.None);
        }
    }

    // does a solid box of the table reach into this box (min, max)?
    static bool Covered(Vector3 min, Vector3 max)
    {
        foreach (Bounds s in solids)
            if (s.min.x < max.x && s.max.x > min.x && s.min.y < max.y && s.max.y > min.y && s.min.z < max.z && s.max.z > min.z)
                return true;
        return false;
    }

    static Vector3 Normal(MapKit.Faces face)
    {
        switch (face)
        {
            case MapKit.Faces.North: return Vector3.forward;
            case MapKit.Faces.East: return Vector3.right;
            case MapKit.Faces.South: return Vector3.back;
            default: return Vector3.left;
        }
    }

    // the yaw of a model whose front (+z) looks out of this face
    static float Yaw(MapKit.Faces face)
    {
        switch (face)
        {
            case MapKit.Faces.North: return 0f;
            case MapKit.Faces.East: return 90f;
            case MapKit.Faces.South: return 180f;
            default: return -90f;
        }
    }

    // a point "gap" metres in front of a face of a building, at "at" along the face (x for north / south faces, z for
    // east / west faces) and at height y
    static Vector3 OnFace(MapKit.Row b, MapKit.Faces face, float at, float y, float gap)
    {
        Vector3 n = Normal(face);
        Vector3 p = b.center + Vector3.Scale(n, b.size * 0.5f) + n * gap;
        if (face == MapKit.Faces.North || face == MapKit.Faces.South) p.x = at;
        else p.z = at;
        p.y = y;
        return p;
    }

    // the place between two windows of the facade grid nearest to "at" (wall lamps must not cover a window)
    static float BetweenWindows(MapKit.Row b, MapKit.Faces face, float at)
    {
        bool alongX = face == MapKit.Faces.North || face == MapKit.Faces.South;
        float length = alongX ? b.size.x : b.size.z, centre = alongX ? b.center.x : b.center.z;
        int n = length < 2.4f ? 0 : Mathf.FloorToInt((length - 2.4f) / WindowSpacing) + 1;
        if (n == 0) return at;
        float best = at, bestD = float.MaxValue;
        for (int k = -1; k < n; k++)
        {
            float p = centre + (k + 0.5f - (n - 1) * 0.5f) * WindowSpacing;
            if (Mathf.Abs(p - centre) > length * 0.5f - 0.3f) continue;
            if (Mathf.Abs(p - at) < bestD) { bestD = Mathf.Abs(p - at); best = p; }
        }
        return best;
    }

    // ---------------- doors, lamps, props ----------------

    // wooden doors on the ground floor (decor, 0.05 m in front of the wall: the model is 0.34 m deep around its centre)
    static void Doors()
    {
        Door("CT church", MapKit.Faces.South, 0f);
        Door("Palazzo A", MapKit.Faces.East, 26f);
        Door("House mid west", MapKit.Faces.South, -2.5f);
        Door("Palazzo east", MapKit.Faces.North, 32f);
        Door("Palazzo B", MapKit.Faces.South, -24f);
        Door("House B lane", MapKit.Faces.North, -17.5f);     // under the vault of the B passage
        Door("House west lane", MapKit.Faces.East, 7f);
        Door("Palazzo east", MapKit.Faces.East, 7f);
        Door("Row CT-A", MapKit.Faces.South, 19f);
        Door("Wall N 2", MapKit.Faces.South, -35f);
        Door("Wall E 2", MapKit.Faces.West, 21f);
    }

    static void Door(string house, MapKit.Faces face, float at)
    {
        MapKit.Row b = MapKit.RowNamed(rows, house);
        MapKit.Prop(house + " door " + face, "large_castle_door", OnFace(b, face, at, 0f, 0.05f), Yaw(face), true);
    }

    static void Lamps()
    {
        // street lamps along the plaza walls (0.25 m collider, they hardly change the plaza)
        MapKit.PropSolid("Plaza lamp W", "street_lamp_01", new Vector3(-9f, 0f, 7.55f), 0f, new Vector3(0.25f, 3.9f, 0.25f), 100f, false, true);
        MapKit.PropSolid("Plaza lamp E", "street_lamp_01", new Vector3(8.5f, 0f, 7.55f), 0f, new Vector3(0.25f, 3.9f, 0.25f), 100f, false, true);

        // wall lanterns between the windows
        Lantern("CT church", MapKit.Faces.South, -2.5f, 3.0f, false);
        Lantern("CT church", MapKit.Faces.South, 2.5f, 3.0f, false);
        Lantern("Palazzo A", MapKit.Faces.South, 18f, 3.2f);
        Lantern("House B lane", MapKit.Faces.North, -25.5f, 3.2f);
        Lantern("Palazzo B", MapKit.Faces.West, 29f, 3.2f);
        Lantern("Palazzo east", MapKit.Faces.North, 22f, 3.2f);
        Lantern("Palazzo east", MapKit.Faces.East, 10f, 3.2f);
        Lantern("House west lane", MapKit.Faces.East, 10f, 3.2f);
        Lantern("House mid west", MapKit.Faces.South, -6f, 3.2f);
        Lantern("Wall E 1", MapKit.Faces.West, 30f, 3.4f);
        Lantern("Wall N 2", MapKit.Faces.South, -38f, 3.4f);

        // a lantern hanging under the balcony
        MapKit.Prop("B balcony lantern", "wooden_lantern_01", new Vector3(-27.5f, 2.2f, 24f), 0f, true);
    }

    // a wall lantern (street_lamp_02, its arm 0.81 m long) on a face, between two windows (snap = false: a wall
    // without windows, the lantern stays at "at")
    static void Lantern(string house, MapKit.Faces face, float at, float y, bool snap = true)
    {
        MapKit.Row b = MapKit.RowNamed(rows, house);
        float p = snap ? BetweenWindows(b, face, at) : at;
        MapKit.Prop(house + " lantern " + face + " " + p.ToString("0.#"), "street_lamp_02", OnFace(b, face, p, y, 0.404f), Yaw(face), true);
    }

    static void Props()
    {
        // cover props (an invisible collider with Penetrable; the model is Not Walkable)
        MapKit.PropSolid("Plaza bench", "painted_wooden_bench", new Vector3(-6f, 0f, 6.9f), 180f, new Vector3(1.16f, 0.9f, 0.5f), 0.5f, false, true);
        MapKit.PropSolid("A table 1", "outdoor_table_chair_set_01", new Vector3(44.5f, 1.2f, 30f), 90f, new Vector3(0.8f, 0.9f, 1.85f), 0.5f, false, true);
        MapKit.PropSolid("A table 2", "outdoor_table_chair_set_01", new Vector3(40.5f, 1.2f, 33.5f), 0f, new Vector3(0.8f, 0.9f, 1.85f), 0.5f, false, true);
        foreach (float x in new[] { 16f, 22f, -16f, -22f })
            MapKit.PropSolid("CT planter " + x, "planter_box_02", new Vector3(x, 0f, 35.7f), 0f, new Vector3(1.25f, 0.45f, 0.47f), 0.5f, false, true);

        // decor: pots on the parapets and the balcony rail, vases on the terrace, small crates on the crate stacks
        MapKit.Prop("A parapet pot 1", "planter_pot_clay", new Vector3(38.5f, 2.1f, 26.2f), 0f, true);
        MapKit.Prop("A parapet pot 2", "planter_pot_clay", new Vector3(46.5f, 2.1f, 26.2f), 40f, true);
        MapKit.Prop("B balcony pot", "planter_pot_clay", new Vector3(-27.5f, 4.0f, 20.1f), 0f, true);
        MapKit.Prop("A vase 1", "antique_ceramic_vase_01", new Vector3(47.4f, 1.2f, 37.4f), 0f, true);
        MapKit.Prop("A vase 2", "antique_ceramic_vase_01", new Vector3(46.95f, 1.2f, 37.55f), 70f, true, 0.8f);
        MapKit.Prop("A vase 3", "antique_ceramic_vase_01", new Vector3(47.55f, 1.2f, 36.95f), 150f, true, 0.65f);
        MapKit.Prop("A crate top", "wooden_crate_01", new Vector3(30.5f, 2.4f, 22.5f), 15f, true);
        MapKit.Prop("B crate top", "wooden_crate_02", new Vector3(-45.5f, 2.4f, 19.5f), 80f, true);

        // the market stall: goods on the counter
        MapKit.Prop("Stall crate 1", "wooden_crate_01", new Vector3(-11.4f, 0.9f, 2.0f), 90f, true);
        MapKit.Prop("Stall crate 2", "wooden_crate_01", new Vector3(-10.6f, 0.9f, 3.9f), 80f, true);
        MapKit.Prop("Stall vase", "antique_ceramic_vase_01", new Vector3(-10.5f, 0.9f, 2.4f), 0f, true, 0.8f);
        MapKit.Prop("Stall pot", "planter_pot_clay", new Vector3(-11.6f, 0.9f, 3.3f), 0f, true);
    }

    // decor of boxes and cylinders (no colliders, left out of the NavMesh)
    static void Details()
    {
        const MapKit.Col no = MapKit.Col.No;
        const MapKit.Nav none = MapKit.Nav.None;
        const float pen = Penetrable.Impenetrable;

        // stone slabs on the A terrace (2 cm over its top, a little smaller, so no face lies on a face of the terrace)
        MapKit.Twin("A terrace slabs", new Vector3(42f, 1.21f, 32f), new Vector3(11.96f, 0.02f, 11.96f), Vector3.zero, "Tiles143", 2.4f, no, pen, none);

        // the fountain: an upper bowl and a finial on the column
        MapKit.Cylinder("Fountain bowl", new Vector3(0f, 3.55f, 0f), new Vector3(2.6f, 0.3f, 2.6f), Vector3.zero, "Concrete012", 1.5f, no, pen, none);
        MapKit.Cylinder("Fountain bowl water", new Vector3(0f, 3.71f, 0f), new Vector3(2.2f, 0.02f, 2.2f), Vector3.zero, "color:#3b6f86", 1f, no, pen, none);
        MapKit.Cylinder("Fountain finial", new Vector3(0f, 4.1f, 0f), new Vector3(0.45f, 0.8f, 0.45f), Vector3.zero, "Concrete012", 1f, no, pen, none);

        // the well: dark water inside the rim, two posts and a beam for the bucket
        MapKit.TwinCylinder("B well water", new Vector3(-40f, 1.01f, 27f), new Vector3(1.9f, 0.02f, 1.9f), Vector3.zero, "color:#25343a", 1f, no, pen, none);
        MapKit.Twin("B well post 1", new Vector3(-41.05f, 1.8f, 27f), new Vector3(0.14f, 1.6f, 0.14f), Vector3.zero, "WoodFloor043", 1f, no, pen, none);
        MapKit.Twin("B well post 2", new Vector3(-38.95f, 1.8f, 27f), new Vector3(0.14f, 1.6f, 0.14f), Vector3.zero, "WoodFloor043", 1f, no, pen, none);
        MapKit.Twin("B well beam", new Vector3(-40f, 2.62f, 27f), new Vector3(2.5f, 0.14f, 0.14f), Vector3.zero, "WoodFloor043", 1f, no, pen, none);

        // the market stall: four posts and a small tiled roof over the counter
        foreach (Vector2 c in new[] { new Vector2(-12.4f, 1.1f), new Vector2(-9.6f, 1.1f), new Vector2(-12.4f, 4.9f), new Vector2(-9.6f, 4.9f) })
            MapKit.Twin("Stall post " + c.x + " " + c.y, new Vector3(c.x, 1.7f, c.y), new Vector3(0.1f, 1.6f, 0.1f), Vector3.zero, "WoodFloor043", 1f, no, pen, none);
        MapKit.Roof("Stall", new Vector3(-11f, 2.4f, 3f), new Vector3(3f, 0.2f, 4f), 0f, true, "RoofingTiles014A", 2.9f, 22f, 0.25f);

        // a bell tower outside the walls behind A: a landmark over the roofs (outside the map bounds)
        MapKit.Box("Campanile", new Vector3(60f, 13f, 30f), new Vector3(5f, 26f, 5f), Vector3.zero, "Bricks084", 2f, no, pen, none);
        MapKit.Facade("Campanile belfry", new Vector3(60f, 13f, 30f), new Vector3(5f, 26f, 5f), MapKit.Faces.All, "PaintedWood008A", 0f, false, null, new[] { 22.5f });
        MapKit.Roof("Campanile", new Vector3(60f, 13f, 30f), new Vector3(5f, 26f, 5f), 0f, false, "RoofingTiles014A", 2.9f, 55f, 0.35f);
    }
}
