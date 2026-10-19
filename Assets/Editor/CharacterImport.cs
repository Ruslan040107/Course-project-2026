using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Import settings for the character models in Assets/Resources/Characters (Quaternius, CC0).
// - Legacy animation: CharacterBody plays the clips by name with the Animation component.
// - The clips get short names ("CharacterArmature|Run" -> "Run"); Idle*, Run*, Walk loop, Death stays on its last frame.
// - The FBX materials have alpha 0 and would import as invisible transparent ones: they become opaque URP Lit.
// - The model is checked to be about 1.81 m tall (the root is scaled if the file units were wrong).
public class CharacterImport : AssetPostprocessor
{
    const string Folder = "Assets/Resources/Characters/";
    const float Height = 1.81f;

    // a new number makes Unity import the models again with the changed settings
    public override uint GetVersion()
    {
        return 1;
    }

    static bool IsCharacter(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        path = path.Replace('\\', '/');
        return path.StartsWith(Folder, System.StringComparison.OrdinalIgnoreCase) &&
               path.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase);
    }

    void OnPreprocessModel()
    {
        if (!IsCharacter(assetPath)) return;
        ModelImporter mi = assetImporter as ModelImporter;
        if (mi == null) return;

        mi.animationType = ModelImporterAnimationType.Legacy;
        mi.importAnimation = true;
        mi.globalScale = 1f;
        mi.useFileScale = true;
        mi.isReadable = false;
        mi.addCollider = false;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importBlendShapes = false;
        mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
    }

    void OnPreprocessAnimation()
    {
        if (!IsCharacter(assetPath)) return;
        ModelImporter mi = assetImporter as ModelImporter;
        if (mi == null) return;

        ModelImporterClipAnimation[] clips = mi.defaultClipAnimations;
        if (clips == null || clips.Length == 0) return;

        foreach (ModelImporterClipAnimation c in clips)
        {
            string shortName = ShortName(string.IsNullOrEmpty(c.takeName) ? c.name : c.takeName);
            bool loop = Loops(shortName);
            c.name = shortName;
            c.loopTime = loop;
            c.wrapMode = loop ? WrapMode.Loop : shortName == "Death" ? WrapMode.ClampForever : WrapMode.Once;
        }
        mi.clipAnimations = clips;
    }

    // "CharacterArmature|Idle_Gun_Pointing" -> "Idle_Gun_Pointing"
    public static string ShortName(string name)
    {
        int i = name.LastIndexOf('|');
        return i >= 0 ? name.Substring(i + 1) : name;
    }

    static bool Loops(string name)
    {
        return name.StartsWith("Idle") || name.StartsWith("Run") || name == "Walk";
    }

    void OnPostprocessMaterial(Material material)
    {
        if (!IsCharacter(assetPath)) return;
        MakeOpaque(material);
    }

    // an opaque URP Lit material with the same colour (alpha 1)
    static void MakeOpaque(Material m)
    {
        Color c = Color.white;
        if (m.HasProperty("_BaseColor")) c = m.GetColor("_BaseColor");
        else if (m.HasProperty("_Color")) c = m.GetColor("_Color");
        Texture tex = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit != null && m.shader != lit) m.shader = lit;

        c.a = 1f;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (tex != null && m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);

        SetFloat(m, "_Surface", 0f);
        SetFloat(m, "_Blend", 0f);
        SetFloat(m, "_AlphaClip", 0f);
        SetFloat(m, "_SrcBlend", (float)BlendMode.One);
        SetFloat(m, "_DstBlend", (float)BlendMode.Zero);
        SetFloat(m, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloat(m, "_DstBlendAlpha", (float)BlendMode.Zero);
        SetFloat(m, "_ZWrite", 1f);
        SetFloat(m, "_CastShadows", 1f);
        SetFloat(m, "_ReceiveShadows", 1f);
        SetFloat(m, "_Metallic", 0f);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", Mathf.Min(m.GetFloat("_Smoothness"), 0.3f));

        m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        m.DisableKeyword("_ALPHAMODULATE_ON");
        m.DisableKeyword("_ALPHATEST_ON");
        m.DisableKeyword("_RECEIVE_SHADOWS_OFF");
        m.SetOverrideTag("RenderType", "Opaque");
        m.SetShaderPassEnabled("DepthOnly", true);
        m.SetShaderPassEnabled("ShadowCaster", true);
        m.renderQueue = (int)RenderQueue.Geometry;
    }

    static void SetFloat(Material m, string name, float value)
    {
        if (m.HasProperty(name)) m.SetFloat(name, value);
    }

    void OnPostprocessModel(GameObject root)
    {
        if (!IsCharacter(assetPath)) return;

        // the height of the meshes in the import pose, in the space of the model root
        bool any = false;
        float minY = 0f, maxY = 0f;
        Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
        foreach (SkinnedMeshRenderer r in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r.sharedMesh == null) continue;
            Bounds b = r.sharedMesh.bounds;
            Matrix4x4 m = toRoot * r.transform.localToWorldMatrix;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f);
                float y = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, corner)).y;
                if (!any || y < minY) minY = y;
                if (!any || y > maxY) maxY = y;
                any = true;
            }
        }
        if (!any) return;

        float height = maxY - minY;
        if (height > 0.01f && (height < Height * 0.85f || height > Height * 1.15f))
        {
            float k = Height / height;
            root.transform.localScale *= k;
            Debug.Log("CharacterImport: " + assetPath + " was " + height.ToString("F3") + " m tall, scaled by " + k.ToString("F4"));
        }
    }
}
