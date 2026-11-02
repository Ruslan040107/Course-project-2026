using System.Collections.Generic;
using UnityEngine;

// "Boost player contrast" for the character models (like the CS2 video setting), and a two-tone palette for the two
// old models (T_Adventurer, CT_Swat: dark and mid-tone cloth only, they melt into shaded walls far away).
// CharacterBody.SpawnModel calls Apply right after FixMaterials, so the bots, the player's model (and its shadow-only
// copies), the bodies and the training target all get it; simulations have no models at all.
// Every material of a model gets one shared copy per team with the shader Resources/Shaders/CharacterContrast:
// dark lift, fill light, rim and adaptive contrast against the scene behind the character, all faded in from
// FadeStart to FadeEnd metres. Without that shader (missing or not supported) the copies are URP Lit with a lifted
// colour and a little emission (helps much less, see below).
// The option itself is one global shader float (_CC_Boost): GameSettings.Apply sets Enabled and calls Refresh,
// nothing is rebuilt.
// Measured in the game (AutoShot test farview: a frozen bot at 40 / 60 / 80 m on the longest lane of the arena and of
// the desert, 1600×900), the share of the bot's pixels that differ from the background behind them by at least 15 L*,
// the four agents: without MSAA boost off 25-72 %, boost on (the values below, set "P1") 80-99 %; with 4x MSAA (the
// default: the few pixels of a far bot blend with the background) off 16-52 %, on 27-87 %. The first models in the
// research: original materials 26-85 %, palette + boost 90-100 %, palette + the URP Lit fallback 23-68 %.
// The adaptive term does most of the work.
public static class CharacterContrast
{
    public const string ShaderPath = "Shaders/CharacterContrast";   // Assets/Resources/Shaders/CharacterContrast.shader

    // the option (GameSettings.BoostPlayerContrast puts it here)
    public static bool Enabled = true;

    // the boost fades in with the distance, m: nothing below FadeStart, all of it from FadeEnd on
    public static float FadeStart = 8f;
    public static float FadeEnd = 25f;

    // the boost terms of the shader
    public static float MinAlbedo = 0.03f;     // dark lift: the albedo luminance (linear) is raised to at least this
    public static float Fill = 0.15f;          // fill light: this part of the albedo is added as self-illumination
    public static float RimStrength = 0.5f;    // fresnel rim on the silhouette
    public static float RimPower = 2.5f;
    public static float Adaptive = 1f;         // 0..1, how far a pixel is pushed away from the background luminance
    public static float MinContrast = 0.2f;    // the wanted |sqrt(Y character) - sqrt(Y background)| (0.2 = about 20-25 L*)

    // the URP Lit fallback (no distance fade there): dark lift and emission
    public static float FallbackMinLum = 0.05f;
    public static float FallbackFill = 0.3f;

    // The palette of the old models by FBX material name (sRGB): a dark torso and light limbs, so at least one big part
    // differs from any wall of the maps by 20+ L*, and a team accent. Only for CT_Swat / T_Adventurer: the agents
    // (CT_SAS, CT_FBI, T_Phoenix, T_Guerrilla) have colours of their own, some with the same material names.
    static readonly Dictionary<string, Color32>[] Palette =
    {
        new Dictionary<string, Color32>   // 0 = T, T_Adventurer
        {
            { "LightGreen", Hex(0x2E2A25) },  // shirt front and back, backpack: dark warm charcoal
            { "Green", Hex(0xCBB585) },       // sleeves and shirt sides: light khaki
            { "Brown", Hex(0x4B3C2B) },       // trousers, bedroll: dark brown
            { "Hair", Hex(0x2A1C12) },
            { "Eyebrows", Hex(0x2A1C12) },
            { "Eye", Hex(0x1A120C) },
            { "Skin", Hex(0xD2B08A) },
            { "Grey", Hex(0x2B2622) },        // boots
            { "Black", Hex(0x141414) },       // soles
            { "Brown2", Hex(0xB4472A) },      // pockets and belt: rust, the team accent
            { "Gold", Hex(0xC8A050) },        // buckle
        },
        new Dictionary<string, Color32>   // 1 = CT, CT_Swat
        {
            { "Swat_Black", Hex(0x1D2738) },  // vest, helmet, gloves, pads, boots: dark navy
            { "Swat", Hex(0xAEBBCE) },        // sleeves and trousers: light steel blue-grey
            { "Visor", Hex(0x3D8BFF) },       // goggles: blue, the team accent
            { "Skin", Hex(0xD2B08A) },
        },
    };

    struct Key
    {
        public Material source;
        public int team;
        public bool palette;
    }

    static Shader shader;
    static bool shaderLooked;
    static readonly Dictionary<Key, Material> copies = new Dictionary<Key, Material>();

    // Play mode starts without a domain reload: the copies of the last play are gone
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        copies.Clear();
        shader = null;
        shaderLooked = false;
    }

    static Color32 Hex(int rgb)
    {
        return new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
    }

    // the boost shader, null when it is missing or the graphics card cannot run it (then the URP Lit fallback)
    public static Shader Shader
    {
        get
        {
            if (!shaderLooked)
            {
                shaderLooked = true;
                shader = Resources.Load<Shader>(ShaderPath);
                if (shader != null && !shader.isSupported) shader = null;
                if (shader == null) Debug.LogWarning("CharacterContrast: no supported shader at Resources/" + ShaderPath + ", using URP Lit instead");
            }
            return shader;
        }
    }

    // the old models get the palette
    public static bool UsesPalette(GameObject prefab)
    {
        return prefab != null && (prefab.name == "CT_Swat" || prefab.name == "T_Adventurer");
    }

    static void SetGlobals()
    {
        UnityEngine.Shader.SetGlobalFloat("_CC_Boost", Enabled ? 1f : 0f);
        UnityEngine.Shader.SetGlobalFloat("_CC_FadeStart", FadeStart);
        UnityEngine.Shader.SetGlobalFloat("_CC_FadeEnd", FadeEnd);
    }

    // puts the option and the tunables on the shader and on every copy (after Enabled or a value changed)
    public static void Refresh()
    {
        SetGlobals();
        foreach (KeyValuePair<Key, Material> p in copies)
        {
            if (p.Key.source == null || p.Value == null) continue;
            if (shader != null && p.Value.shader == shader) SetBoostTerms(p.Value);
            else FillLit(p.Value, p.Key.source, p.Key.team, p.Key.palette);   // the fallback holds the boost in its colours
        }
    }

    // swaps the materials of a new character model (once per instance); team 0 = T, 1 = CT
    public static void Apply(GameObject model, int team, bool palette)
    {
        if (model == null) return;
        SetGlobals();
        foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
            Material[] mats = r.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < mats.Length; i++)
            {
                Material m = mats[i];
                if (m == null) continue;
                Key key = new Key { source = m, team = team, palette = palette };
                if (!copies.TryGetValue(key, out Material copy) || copy == null)
                {
                    copy = MakeCopy(m, team, palette);
                    copies[key] = copy;
                }
                mats[i] = copy;
                changed = true;
            }
            if (changed) r.sharedMaterials = mats;
        }
    }

    // the name of the FBX material ("Swat (opaque)" -> "Swat")
    static string BaseName(Material m)
    {
        string n = m.name;
        int k = n.IndexOf(" (", System.StringComparison.Ordinal);
        return k > 0 ? n.Substring(0, k) : n;
    }

    static Color BaseColour(Material src, int team, bool palette)
    {
        Color c = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor") : src.HasProperty("_Color") ? src.color : Color.white;
        if (palette && team >= 0 && team < Palette.Length && Palette[team].TryGetValue(BaseName(src), out Color32 p)) c = p;
        c.a = 1f;
        return c;
    }

    static void SetBoostTerms(Material c)
    {
        c.SetFloat("_MinAlbedo", MinAlbedo);
        c.SetFloat("_Fill", Fill);
        c.SetFloat("_RimStrength", RimStrength);
        c.SetFloat("_RimPower", RimPower);
        c.SetFloat("_Adaptive", Adaptive);
        c.SetFloat("_MinContrast", MinContrast);
    }

    static Material MakeCopy(Material src, int team, bool palette)
    {
        Shader s = Shader;
        Material c;
        if (s != null)
        {
            c = new Material(s) { name = BaseName(src) + " (contrast)" };
            c.SetColor("_BaseColor", BaseColour(src, team, palette));
            if (src.HasProperty("_BaseMap") && src.GetTexture("_BaseMap") != null) c.SetTexture("_BaseMap", src.GetTexture("_BaseMap"));
            c.SetFloat("_Smoothness", src.HasProperty("_Smoothness") ? Mathf.Min(src.GetFloat("_Smoothness"), 0.3f) : 0.25f);
            c.SetFloat("_Metallic", 0f);
            // the rim takes a hint of the team colour (T warm, CT cool)
            c.SetColor("_RimColor", team == 1 ? new Color(0.85f, 0.92f, 1f) : new Color(1f, 0.93f, 0.85f));
            SetBoostTerms(c);
        }
        else
        {
            c = new Material(src) { name = BaseName(src) + " (contrast lit)" };
            FillLit(c, src, team, palette);
        }
        return c;
    }

    // URP Lit fallback: dark colours lifted to a minimum luminance, plus emission = a part of the colour (the emission
    // shows only if a URP Lit material with Emission on is in the build, otherwise its variant is stripped)
    static void FillLit(Material c, Material src, int team, bool palette)
    {
        Color lin = BaseColour(src, team, palette).linear;
        float lum = 0.2126f * lin.r + 0.7152f * lin.g + 0.0722f * lin.b;
        if (Enabled && lum < FallbackMinLum)
            lin = lum > 1e-4f ? lin * (FallbackMinLum / lum) : new Color(FallbackMinLum, FallbackMinLum, FallbackMinLum);
        lin.a = 1f;
        c.SetColor("_BaseColor", lin.gamma);
        if (!c.HasProperty("_EmissionColor")) return;
        float f = Enabled ? FallbackFill : 0f;
        c.SetColor("_EmissionColor", new Color(lin.r * f, lin.g * f, lin.b * f, 1f));
        if (Enabled) c.EnableKeyword("_EMISSION");
        else c.DisableKeyword("_EMISSION");
        c.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
    }
}
