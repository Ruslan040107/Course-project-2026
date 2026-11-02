using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;

// Import settings and URP materials for the assets of the maps Borgo and Porto (Tools/Maps/fetch_map_assets.py puts
// them into the project; all CC0, see CREDITS.md):
// - Assets/Textures/Map/<ID>/<ID>_NormalGL.jpg (ambientCG): normal maps.
// - Assets/Textures/Sky/*.hdr (Poly Haven): panoramic skies; 2D, no mipmaps (no seam at u = 0/1), repeat u, clamp v.
// - Assets/Models/Props/<slug>/<slug>.fbx (Poly Haven, FBX made in Blender): no cameras, lights, colliders or animation,
//   1 unit = 1 m. Its materials are mapped (in the .meta) to URP Lit materials beside it, made from <slug>.json:
//   colour, normal, mask (metallic R, occlusion G, smoothness A; packed from the ARM map) and emission maps.
// The settings live in the .meta files, so this runs once after new files were copied in (Tools > Maps > Set Up Map Assets);
// MapKit calls Model(slug) for every prop it places, which sets up a prop that is not set up yet.
// Nothing here is an AssetPostprocessor: the other models and textures of the project are never touched.
public static class PropImport
{
    public const string PropFolder = "Assets/Models/Props";
    public const string MapTextureFolder = "Assets/Textures/Map";
    public const string SkyFolder = "Assets/Textures/Sky";

    // written into importer.userData: an importer with this mark is set up already
    // (a new number sets everything up again with changed settings)
    const string Mark = "PropImport 1";

    // <slug>.json written by fetch_map_assets.py
    [System.Serializable]
    public class MaterialInfo
    {
        public string name;
        public string baseMap, normalMap, maskMap, emissionMap;
        public float[] baseColor, emission;
        public float metallic = 1f, roughness = 1f;
        public string alphaMode;
        public float alphaCutoff = 0.5f;
        public float transmission;
        public bool doubleSided;
    }

    [System.Serializable]
    public class PropInfo
    {
        public string slug, source, license, authors;
        public float[] size;            // x, height, z in metres (Unity axes, as exported)
        public float[] bottomCentre;    // in the FBX's own space
        public int tris;
        public MaterialInfo[] materials;
    }

    static readonly Dictionary<string, PropInfo> infos = new Dictionary<string, PropInfo>();

    [MenuItem("Tools/Maps/Set Up Map Assets")]
    public static void SetUpAll()
    {
        infos.Clear();
        int textures = SetUpMapTextures();
        int skies = SetUpSkies();
        int props = 0;
        foreach (string slug in Slugs())
            if (Model(slug) != null) props++;
        AssetDatabase.SaveAssets();
        Debug.Log("PropImport: " + textures + " normal maps, " + skies + " skies, " + props + " props set up");
    }

    // every prop folder that has its <slug>.fbx and <slug>.json
    public static List<string> Slugs()
    {
        var list = new List<string>();
        if (!Directory.Exists(PropFolder)) return list;
        foreach (string dir in Directory.GetDirectories(PropFolder))
        {
            string slug = Path.GetFileName(dir);
            if (File.Exists(dir + "/" + slug + ".fbx") && File.Exists(dir + "/" + slug + ".json")) list.Add(slug);
        }
        list.Sort(System.StringComparer.Ordinal);
        return list;
    }

    // ---------------- map textures and skies ----------------

    // <ID>_NormalGL.jpg -> normal map (the desert textures already are: their .meta files do not change)
    public static int SetUpMapTextures()
    {
        int n = 0;
        if (!Directory.Exists(MapTextureFolder)) return 0;
        foreach (string dir in Directory.GetDirectories(MapTextureFolder))
        {
            string id = Path.GetFileName(dir);
            if (NormalMap(MapTextureFolder + "/" + id + "/" + id + "_NormalGL.jpg")) n++;
        }
        return n;
    }

    public static bool NormalMap(string path)
    {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return false;
        if (ti.textureType != TextureImporterType.NormalMap)
        {
            ti.textureType = TextureImporterType.NormalMap;
            ti.SaveAndReimport();
        }
        return true;
    }

    public static int SetUpSkies()
    {
        int n = 0;
        if (!Directory.Exists(SkyFolder)) return 0;
        foreach (string file in Directory.GetFiles(SkyFolder, "*.hdr"))
            if (Sky(SkyFolder + "/" + Path.GetFileName(file)) != null) n++;
        return n;
    }

    // a panorama for Skybox/Panoramic: no mips, so the sky has no seam where u wraps round
    public static Texture2D Sky(string path)
    {
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null) return null;
        if (ti.userData != Mark)
        {
            ti.textureType = TextureImporterType.Default;
            ti.textureShape = TextureImporterShape.Texture2D;
            ti.mipmapEnabled = false;
            ti.wrapModeU = TextureWrapMode.Repeat;
            ti.wrapModeV = TextureWrapMode.Clamp;
            ti.filterMode = FilterMode.Bilinear;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.userData = Mark;
            ti.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // ---------------- props ----------------

    public static string Folder(string slug)
    {
        return PropFolder + "/" + slug;
    }

    public static PropInfo Info(string slug)
    {
        if (infos.TryGetValue(slug, out PropInfo cached)) return cached;
        string path = Folder(slug) + "/" + slug + ".json";
        if (!File.Exists(path)) return null;
        PropInfo info = JsonUtility.FromJson<PropInfo>(File.ReadAllText(path));
        infos[slug] = info;
        return info;
    }

    // the imported model of a prop (sets up its textures, materials and import settings the first time)
    public static GameObject Model(string slug)
    {
        string fbx = Folder(slug) + "/" + slug + ".fbx";
        ModelImporter mi = AssetImporter.GetAtPath(fbx) as ModelImporter;
        PropInfo info = Info(slug);
        if (mi == null || info == null)
        {
            Debug.LogError("PropImport: no prop " + slug + " (" + fbx + " and " + slug + ".json)");
            return null;
        }
        if (mi.userData != Mark || !MaterialsExist(info)) SetUpProp(slug, mi, info);
        return AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
    }

    static bool MaterialsExist(PropInfo info)
    {
        foreach (MaterialInfo m in info.materials)
            if (AssetDatabase.LoadAssetAtPath<Material>(MaterialPath(info.slug, m.name)) == null) return false;
        return true;
    }

    public static string MaterialPath(string slug, string materialName)
    {
        return Folder(slug) + "/" + SafeName(materialName) + ".mat";
    }

    static string SafeName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace('.', '_');
    }

    static void SetUpProp(string slug, ModelImporter mi, PropInfo info)
    {
        string dir = Folder(slug) + "/";
        foreach (MaterialInfo m in info.materials)
        {
            Texture(dir + m.baseMap, TextureImporterType.Default, true);
            Texture(dir + m.normalMap, TextureImporterType.NormalMap, false);
            Texture(dir + m.maskMap, TextureImporterType.Default, false);     // data, not colour
            Texture(dir + m.emissionMap, TextureImporterType.Default, true);
        }

        var made = new Dictionary<string, Material>();
        foreach (MaterialInfo m in info.materials) made[m.name] = PropMaterial(slug, m);

        mi.globalScale = 1f;
        mi.useFileScale = true;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importVisibility = false;
        mi.addCollider = false;
        mi.animationType = ModelImporterAnimationType.None;
        mi.importAnimation = false;
        mi.importBlendShapes = false;
        mi.isReadable = false;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.importNormals = ModelImporterNormals.Import;
        mi.importTangents = ModelImporterTangents.CalculateMikk;
        mi.generateSecondaryUV = false;
        // the FBX materials are replaced by the ones made here (the map is kept in the .meta file)
        mi.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        foreach (var source in new List<AssetImporter.SourceAssetIdentifier>(mi.GetExternalObjectMap().Keys))
            if (source.type == typeof(Material)) mi.RemoveRemap(source);
        foreach (KeyValuePair<string, Material> kv in made)
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
        mi.userData = Mark;
        mi.SaveAndReimport();

        // every renderer should now use the materials made here
        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(dir + slug + ".fbx");
        if (model != null)
            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (m == null || !made.ContainsValue(m))
                        Debug.LogWarning("PropImport: " + slug + "/" + r.name + " has material " + (m != null ? m.name : "null") + " that is not in " + slug + ".json");
    }

    static void Texture(string path, TextureImporterType type, bool sRGB)
    {
        if (path.EndsWith("/")) return;     // no such map
        TextureImporter ti = AssetImporter.GetAtPath(path) as TextureImporter;
        if (ti == null)
        {
            Debug.LogWarning("PropImport: texture not found " + path);
            return;
        }
        if (ti.userData == Mark) return;
        ti.textureType = type;
        ti.sRGBTexture = sRGB;
        ti.mipmapEnabled = true;
        ti.maxTextureSize = 1024;
        ti.textureCompression = TextureImporterCompression.Compressed;
        ti.userData = Mark;
        ti.SaveAndReimport();
    }

    // a URP Lit material from the glTF material of the prop
    static Material PropMaterial(string slug, MaterialInfo m)
    {
        string dir = Folder(slug) + "/";
        string path = MaterialPath(slug, m.name);
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        else mat.shader = Shader.Find("Universal Render Pipeline/Lit");

        Color baseColor = Col(m.baseColor, Color.white);
        Texture2D baseMap = Load(dir, m.baseMap);
        Texture2D normal = Load(dir, m.normalMap);
        Texture2D mask = Load(dir, m.maskMap);
        Texture2D emissionMap = Load(dir, m.emissionMap);

        // glass: see-through and smooth (Poly Haven glass is "transmission", which URP Lit does not have)
        bool glass = m.transmission > 0.01f;
        bool blend = glass || m.alphaMode == "BLEND";
        if (glass) baseColor.a = Mathf.Min(baseColor.a, 0.3f);

        mat.SetFloat("_WorkflowMode", 1f);      // metallic
        mat.SetTexture("_BaseMap", baseMap);
        mat.SetColor("_BaseColor", baseColor);
        mat.SetTexture("_MetallicGlossMap", mask);
        mat.SetTexture("_OcclusionMap", mask);
        mat.SetFloat("_OcclusionStrength", 1f);
        mat.SetFloat("_SmoothnessTextureChannel", 0f);      // smoothness from the mask's alpha
        // with a mask the metallic comes from it and _Smoothness scales its alpha
        mat.SetFloat("_Metallic", mask != null ? 1f : glass ? 0f : m.metallic);
        mat.SetFloat("_Smoothness", glass ? 0.92f : mask != null ? 1f : 1f - m.roughness);
        mat.SetTexture("_BumpMap", normal);
        mat.SetFloat("_BumpScale", 1f);

        Color emission = Col(m.emission, Color.black);
        if (emissionMap != null && emission.maxColorComponent <= 0f) emission = Color.white;
        mat.SetTexture("_EmissionMap", emissionMap);
        mat.SetColor("_EmissionColor", emission);
        // the shader GUI keeps emission on only for an emissive GI flag (there is no GI bake, so realtime)
        mat.globalIlluminationFlags = emission.maxColorComponent > 0f ? MaterialGlobalIlluminationFlags.RealtimeEmissive
                                                                      : MaterialGlobalIlluminationFlags.EmissiveIsBlack;

        mat.SetFloat("_Surface", blend ? 1f : 0f);
        mat.SetFloat("_Blend", 0f);     // alpha
        mat.SetFloat("_AlphaClip", m.alphaMode == "MASK" ? 1f : 0f);
        mat.SetFloat("_Cutoff", m.alphaCutoff);
        mat.SetFloat("_Cull", m.doubleSided || glass ? 0f : 2f);
        mat.SetFloat("_ReceiveShadows", 1f);
        mat.SetFloat("_CastShadows", glass ? 0f : 1f);

        // the keywords, passes and render queue exactly as the URP material inspector sets them
        BaseShaderGUI.SetMaterialKeywords(mat, LitGUI.SetMaterialKeywords);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Texture2D Load(string dir, string file)
    {
        return string.IsNullOrEmpty(file) ? null : AssetDatabase.LoadAssetAtPath<Texture2D>(dir + file);
    }

    static Color Col(float[] c, Color fallback)
    {
        if (c == null || c.Length < 3) return fallback;
        return new Color(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
    }
}
