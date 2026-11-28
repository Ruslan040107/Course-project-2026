using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

// The Blender file supplies geometry, metres, spawn points and route landmarks.
// C_ meshes provide collision and navigation. D_ meshes are visible architecture and props.
public static class ClassicMapBuilder
{
    // A checkout without the optional local assets can still build the public scenes.
    [InitializeOnLoadMethod]
    static void RemoveMissingLocalScenes()
    {
        EditorApplication.delayCall += () =>
        {
            var original = EditorBuildSettings.scenes;
            var available = original.Where(s => File.Exists(s.path) ||
                (s.path != "Assets/Scenes/Dust2Map.unity" && s.path != "Assets/Scenes/MirageMap.unity")).ToArray();
            if (available.Length != original.Length) EditorBuildSettings.scenes = available;
        };
    }

    [MenuItem("Tools/Maps/Build Dust 2")]
    public static void BuildDust2() => Build("Dust2", MainMenu.Dust2Scene, 76f, 81f);

    [MenuItem("Tools/Maps/Build Mirage")]
    public static void BuildMirage() => Build("Mirage", MainMenu.MirageScene, 79f, 67f);

    public static void BuildBoth()
    {
        BuildDust2();
        BuildMirage();
    }

    static void Build(string modelName, string sceneName, float halfX, float halfZ)
    {
        string modelPath = "Assets/Models/Maps/" + modelName + ".fbx";
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
        if (model == null) throw new InvalidOperationException("Missing Blender export: " + modelPath);
        if (!MapKit.Begin(modelName, sceneName)) return;
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, MapKit.Level);
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
        var palette = new Dictionary<string, Material>();
        foreach (var filter in instance.GetComponentsInChildren<MeshFilter>())
        {
            var obj = filter.gameObject;
            var renderer = obj.GetComponent<MeshRenderer>();
            if (obj.name.StartsWith("C_", StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(renderer);
                obj.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
                obj.AddComponent<Penetrable>().strength = obj.name.StartsWith("C_Wood_") ? .5f : Penetrable.Impenetrable;
                continue;
            }
            if (!obj.name.StartsWith("D_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unclassified Blender mesh: " + obj.name);
            var mats = renderer.sharedMaterials;
            for (int i = 0; i < mats.Length; i++)
            {
                string id = mats[i].name;
                if (!palette.TryGetValue(id, out var material))
                    palette[id] = material = Surface(id);
                mats[i] = material;
            }
            renderer.sharedMaterials = mats;
        }

        var anchors = instance.GetComponentsInChildren<Transform>()
            .Where(t => t.childCount == 0 && t.GetComponent<MeshFilter>() == null)
            .ToDictionary(t => t.name, t => t.position);
        Vector3[] Spawns(string team) => Enumerable.Range(1, 5)
            .Select(i => anchors[team + i] + Vector3.up).ToArray();
        var tSpawns = Spawns("T");
        MapKit.SpawnPoints("T Spawn", tSpawns, 0f);
        MapKit.SpawnPoints("CT Spawn", Spawns("CT"), 180f);
        foreach (string team in new[] { "T", "CT" })
        {
            var group = GameObject.Find(team + " Spawn").transform;
            for (int i = 0; i < group.childCount; i++)
            {
                string key = team + (i + 1);
                group.GetChild(i).rotation = Quaternion.LookRotation(anchors["Facing_" + key] - anchors[key]);
            }
        }
        foreach (string site in new[] { "A", "B" })
            MapKit.Bombsite("Bombsite " + site, anchors["Site" + site] + 1.5f * Vector3.up, new Vector3(8, 3, 8));
        MapKit.Extras(tSpawns[2], anchors["Training"] + Vector3.up, anchors["AimTarget"] + Vector3.up);
        if (modelName == "Mirage")
        {
            var entries = File.ReadAllLines("Tools/Blender/mirage_positions.csv").Skip(1)
                .Where(line => line.Length > 0).Select(line => line.Split(',')).ToArray();
            if (entries.Length != 66 || entries.Select(r => r[0]).Distinct().Count() != entries.Length)
                throw new InvalidOperationException("Expected 66 distinct Mirage callouts");
            var callouts = instance.AddComponent<MapCallouts>();
            callouts.floorPoints = entries.Select(r => anchors["Spot_" + r[0]]).ToArray();
            callouts.labels = entries.Select(r => r[1] + " · " + r[2] + (r[3].Length == 0 ? "" : " (" + r[3] + ")")).ToArray();
        }
        MapKit.SetBounds(halfX + .5f, halfZ + .5f, halfX - 3, halfZ - 3, -18f, 42f);
        var bounds = UnityEngine.Object.FindAnyObjectByType<MapBounds>();
        bounds.patrolPoints = anchors.Where(a => a.Key.StartsWith("Route_") || a.Key == "SiteA" || a.Key == "SiteB")
            .Select(a => a.Value).ToArray();
        bounds.rallyPoint = anchors[modelName == "Mirage" ? "Spot_M07" : "Route_Mid"];
        UnityEngine.Object.FindAnyObjectByType<RoundManager>().useSpawnPointRotation = true;
        MapKit.Overview(new Vector3(0f, 85f, 0f));
        MapKit.Sky(modelName == "Dust2" ? MapKit.SkySettings.Porto() : MapKit.SkySettings.Borgo());
        MapKit.Finish(NavMeshCollectGeometry.PhysicsColliders);
        ValidateScene(sceneName);
    }

    static Material Surface(string id)
    {
        if (id.StartsWith("L_", StringComparison.Ordinal))
        {
            var fields = id.Split('_');
            string path = "Assets/Models/Maps/" + fields[1] + "/" + id;
            var material = AssetDatabase.LoadAssetAtPath<Material>(path + ".mat");
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path + ".mat");
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path + ".png");
            if (texture == null) throw new InvalidOperationException("Missing map texture: " + path);
            material.SetTexture("_BaseMap", texture);
            ColorUtility.TryParseHtmlString("#" + fields[4], out var tint);
            material.SetColor("_BaseColor", tint);
            material.SetFloat("_Smoothness", .13f);
            if (fields[3] == "C")
            {
                material.SetFloat("_AlphaClip", 1f);
                material.SetFloat("_Cutoff", .35f);
                material.SetFloat("_Cull", 0f);
                material.EnableKeyword("_ALPHATEST_ON");
                material.SetOverrideTag("RenderType", "TransparentCutout");
                material.renderQueue = 2450;
            }
            EditorUtility.SetDirty(material);
            return material;
        }
        if (id.StartsWith("Tint_", StringComparison.Ordinal))
            return MapKit.GetMaterial("color:#" + id.Substring(5), .15f);
        throw new InvalidOperationException("Unknown map material " + id);
    }

    // Reload the saved scene so this checks persistent colliders, the baked mesh and both vertical levels.
    static void ValidateScene(string sceneName)
    {
        string path = "Assets/Scenes/" + sceneName + ".unity";
        EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
        Physics.SyncTransforms();
        if (!EditorBuildSettings.scenes.Any(s => s.path == path && s.enabled) ||
            !MainMenu.Maps.Any(m => m.scene == sceneName))
            throw new InvalidOperationException(sceneName + " missing from the build or the map menu");
        var level = GameObject.Find("Level").transform;
        var goals = level.GetComponentsInChildren<Transform>()
            .Where(t => t.name.StartsWith("Route_") || t.name == "SiteA" || t.name == "SiteB").ToArray();
        if (goals.Length < 6) throw new InvalidOperationException(sceneName + " lost route markers");
        var ts = GameObject.Find("T Spawn").transform.Cast<Transform>().ToArray();
        var cts = GameObject.Find("CT Spawn").transform.Cast<Transform>().ToArray();
        int rays = 0;
        float nearest = float.PositiveInfinity;
        foreach (var t in ts)
        foreach (var ct in cts)
        {
            nearest = Mathf.Min(nearest, Vector3.Distance(t.position, ct.position));
            foreach (bool reverse in new[] { false, true })
            foreach (float eye in new[] { .15f, .65f })
            foreach (float target in new[] { -.3f, .1f, .65f })
            {
                var from = (reverse ? ct : t).position + Vector3.up * eye;
                var to = (reverse ? t : ct).position + Vector3.up * target;
                if (!Physics.RaycastAll(from, (to-from).normalized, (to-from).magnitude, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore).Any(h => h.transform.IsChildOf(level)))
                    throw new InvalidOperationException(sceneName + " exposed spawn pair: " + t.name + ", " + ct.name);
                rays++;
            }
        }
        Debug.Log(sceneName + " blocked spawn sightlines: " + rays + "/300; closest spawns " + nearest.ToString("F1") + " m");
        var callouts = level.GetComponentInChildren<MapCallouts>();
        if (sceneName == MainMenu.MirageScene)
        {
            if (callouts == null || callouts.floorPoints.Length != 66 || callouts.labels.Length != 66)
                throw new InvalidOperationException("The saved scene lost its callouts");
            var problems = new List<string>();
            var startPoint = OnMesh(ts[0].position - Vector3.up, "T spawn");
            var entries = File.ReadAllLines("Tools/Blender/mirage_positions.csv").Skip(1).ToArray();
            int walkable = 0;
            for (int i = 0; i < callouts.floorPoints.Length; i++)
            {
                var point = callouts.floorPoints[i];
                if (callouts.At(point) != callouts.labels[i] ||
                    !Physics.RaycastAll(point + Vector3.up * .55f, Vector3.down, 1.25f, Physics.DefaultRaycastLayers,
                        QueryTriggerInteraction.Ignore).Any(h => h.transform.IsChildOf(level) && h.normal.y > .45f))
                    problems.Add("floor/label " + callouts.labels[i] + " " + point);
                if (!entries[i].EndsWith(",walk", StringComparison.Ordinal)) continue;
                var route = new NavMeshPath();
                if (!NavMesh.SamplePosition(point, out var dest, 1.7f, NavMesh.AllAreas) ||
                    Mathf.Abs(dest.position.y-point.y) > .85f ||
                    !NavMesh.CalculatePath(startPoint, dest.position, NavMesh.AllAreas, route) ||
                    route.status != NavMeshPathStatus.PathComplete)
                    problems.Add("route " + callouts.labels[i] + " " + point);
                else walkable++;
            }
            if (problems.Count != 0) throw new InvalidOperationException("Mirage callouts: " + String.Join(" | ", problems));
            Debug.Log("Mirage callouts: 66 labels with physical support; reachable walking positions " + walkable);
        }
        int paths = 0;
        foreach (var side in new[] { "T Spawn", "CT Spawn" })
        {
            var spawns = GameObject.Find(side).transform;
            if (spawns.childCount != 5) throw new InvalidOperationException(side + " requires five spawn points");
            foreach (Transform spawn in spawns)
            {
                var floor = spawn.position - Vector3.up;
                var start = OnMesh(floor, spawn.name);
                if (Physics.OverlapCapsule(floor + .5f * Vector3.up, floor + 1.6f * Vector3.up, .32f,
                                           Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                           .Any(c => c.transform.IsChildOf(level)))
                    throw new InvalidOperationException(side + "/" + spawn.name + " is obstructed at " + floor);
                foreach (var goal in goals)
                {
                    var end = OnMesh(goal.position, goal.name);
                    var route = new NavMeshPath();
                    if (!NavMesh.CalculatePath(start, end, NavMesh.AllAreas, route) ||
                        route.status != NavMeshPathStatus.PathComplete)
                        throw new InvalidOperationException(side + "/" + spawn.name + " cannot reach " + goal.name);
                    paths++;
                }
            }
        }
        Debug.Log("Classic map validated: " + sceneName + ", clear spawns: 10, complete paths: " + paths);
    }

    static Vector3 OnMesh(Vector3 floor, string label)
    {
        if (!NavMesh.SamplePosition(floor + .1f * Vector3.up, out var hit, .7f, NavMesh.AllAreas) ||
            Mathf.Abs(hit.position.y - floor.y) > .45f)
            throw new InvalidOperationException("No walkable floor at " + label + " " + floor);
        return hit.position;
    }
}
