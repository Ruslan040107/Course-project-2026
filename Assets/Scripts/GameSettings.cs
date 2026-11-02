using System.Collections.Generic;
using UnityEngine;

// the shape of the crosshair (saved as a number: new styles go to the END)
public enum CrosshairStyle
{
    Classic,    // four lines with a gap
    LinesDot,   // four lines with a gap and a dot in the middle
    Dot,        // only a dot
    Circle,     // a ring and a dot
    Cross,      // four lines without a gap
    TShape,     // like Classic without the top line
    None        // no crosshair at all
}

// Player settings (saved in PlayerPrefs) and the choice made in the main menu for the next match.
// Simulations never read these: RoundManager ignores them when it simulates.
// The settings screen is in MainMenu.cs (Escape → Настройки), GameHud.DrawCrosshairShape draws the crosshair.
// The match options of the main menu (players per team, static bots, the map) are saved at once by SaveMatchOptions.
// The screen mode and resolution are put on the window only by Save and at the start of the game (Load),
// never in batch mode, simulations (-sim), without graphics or in the editor. The anti-aliasing (MSAA of the render
// pipeline asset) works at once, but likewise only in a player with a screen.
public static class GameSettings
{
    // defaults (also used by "По умолчанию" in the settings screen)
    public const float DefaultSensitivity = 2f;
    public const float DefaultFov = 90f;
    public const float DefaultVolume = 0.8f;
    public const bool DefaultShowRadar = true;
    public const float DefaultCrosshairSize = 1f;
    public const int DefaultCrosshairColor = 0;
    public const CrosshairStyle DefaultCrosshairStyle = CrosshairStyle.Classic;
    // crosshair lines in pixels at 1080p, before the size multiplier
    public const float DefaultCrosshairGap = 4f;
    public const float DefaultCrosshairThickness = 2f;
    public const float DefaultCrosshairLength = 10f;
    public const bool DefaultCrosshairOutline = true;
    public const bool DefaultCrosshairDynamic = true;

    // allowed ranges (the sliders of the settings screen use them too)
    public const float MinSensitivity = 0.1f;
    public const float MaxSensitivity = 10f;
    public const float MinFov = 70f;
    public const float MaxFov = 110f;
    public const float MinCrosshairSize = 0.5f;
    public const float MaxCrosshairSize = 2f;
    public const float MinCrosshairGap = 0f;
    public const float MaxCrosshairGap = 20f;
    public const float MinCrosshairThickness = 1f;
    public const float MaxCrosshairThickness = 6f;
    public const float MinCrosshairLength = 2f;
    public const float MaxCrosshairLength = 30f;

    // mouse sensitivity multiplier used by PlayerController (the AWP scope multiplies it further)
    public static float Sensitivity = DefaultSensitivity;
    // extra multiplier while zoomed (scopes and optics), 1 = the same feel as without the scope
    public const float DefaultZoomSensitivity = 1f;
    public const float MinZoomSensitivity = 0.1f;
    public const float MaxZoomSensitivity = 3f;
    public static float ZoomSensitivity = DefaultZoomSensitivity;
    // bots stand still and do not shoot (training against static bots); RoundManager reads it at the start of a match
    public static bool BotsFrozen;
    // horizontal field of view in degrees on a 16:9 screen; other screens keep the same vertical angle
    // (wider screens see more at the sides). The camera gets VerticalFov. 90 = the old look of the scene camera.
    public static float Fov = DefaultFov;
    // 0..1, AudioListener.volume
    public static float MasterVolume = DefaultVolume;
    public static bool ShowRadar = DefaultShowRadar;
    // the radar: its size on screen (1 = the default size) and its zoom (1 = the default range, more = closer)
    public const float DefaultRadarSize = 1f, MinRadarSize = 0.6f, MaxRadarSize = 2f;
    public const float DefaultRadarZoom = 1f, MinRadarZoom = 0.5f, MaxRadarZoom = 2.5f;
    public static float RadarSize = DefaultRadarSize;
    public static float RadarZoom = DefaultRadarZoom;
    // how the game window is shown: a window, exclusive full screen, or a borderless full-screen window
    public enum DisplayMode { Windowed, Fullscreen, Borderless }
    public static DisplayMode Display = DisplayMode.Windowed;
    // names for the settings screen, in the order of the DisplayMode values
    public static readonly string[] DisplayModeNames = { "Оконный", "Полноэкранный", "Полноэкранный в окне" };
    // the chosen resolution in pixels (0 = the size the game runs at now)
    public static int ScreenWidth, ScreenHeight;
    // the smallest size offered in the resolution list
    const int MinScreenWidth = 640, MinScreenHeight = 480;
    // crosshair: size multiplier and colour index (see CrosshairColors)
    public static float CrosshairSize = DefaultCrosshairSize;
    public static int CrosshairColor = DefaultCrosshairColor;
    public static readonly Color[] CrosshairColors = { Color.green, Color.white, Color.cyan, Color.yellow, Color.red };
    // names for the settings screen, in the same order as CrosshairColors
    public static readonly string[] CrosshairColorNames = { "Зелёный", "Белый", "Голубой", "Жёлтый", "Красный" };

    // crosshair shape: the style, the gap from the centre, line thickness and length (pixels at 1080p,
    // the size multiplier above scales them), a dark outline, and a gap that grows with the spread of the next shot
    public static CrosshairStyle CrosshairStyle = DefaultCrosshairStyle;
    public static float CrosshairGap = DefaultCrosshairGap;
    public static float CrosshairThickness = DefaultCrosshairThickness;
    public static float CrosshairLength = DefaultCrosshairLength;
    public static bool CrosshairOutline = DefaultCrosshairOutline;
    public static bool CrosshairDynamic = DefaultCrosshairDynamic;
    // names for the settings screen, in the order of the CrosshairStyle values
    public static readonly string[] CrosshairStyleNames =
        { "Классический", "Линии и точка", "Только точка", "Круг и точка", "Крест без зазора", "Т-образный", "Без прицела" };

    // "Boost player contrast" like in CS2: the character models stand out from the background far away
    // (CharacterContrast; Apply puts it on the shader at once)
    public const bool DefaultBoostPlayerContrast = true;
    public static bool BoostPlayerContrast = DefaultBoostPlayerContrast;

    // anti-aliasing: the index of the choice (MSAA samples in AntiAliasingSamples), put on the render pipeline asset in use
    public const int DefaultAntiAliasing = 2;   // 4x
    public static int AntiAliasing = DefaultAntiAliasing;
    public static readonly int[] AntiAliasingSamples = { 1, 2, 4, 8 };
    // names for the settings screen, in the same order
    public static readonly string[] AntiAliasingNames = { "Выкл", "2x", "4x", "8x" };

    // the agent (character model) of the player on each side: 0 = auto (like a bot, by the number in the team),
    // 1 / 2 = the first / second agent of CharacterBody.TAgents / CTAgents; the model changes at once
    public const int AgentAuto = 0;
    public static int AgentT = AgentAuto;
    public static int AgentCT = AgentAuto;
    // names for the settings screen, in the order of the values
    public static readonly string[] AgentTNames = { "Авто", "Феникс", "Партизан" };
    public static readonly string[] AgentCTNames = { "Авто", "SAS", "ФБР" };

    // the mode chosen in the main menu for the next scene load; null = the scene's own settings
    public static bool? PendingSolo;
    // players per team chosen in the main menu for the next match (the player counts on his team); null = the scene's value
    public static int? PendingBotsPerTeam;

    // main menu options of a match (saved at once by SaveMatchOptions, "По умолчанию" of the settings does not touch them):
    // players per team with the player (like RoundManager.botsPerTeam), 0 = not chosen yet (the scene's value).
    // TeamSizeBaseOf keeps the botsPerTeam of each scene at the time of the choice: when the Inspector value
    // of that scene changes later, the new one wins
    public const int MinTeamSize = 1;
    public const int MaxTeamSize = 10;
    public static int TeamSize;
    // the scene of the map chosen in the main menu ("" = not chosen yet: the scene the game started in)
    public static string MenuMap = "";

    // true after the main menu was closed once in this run (the menu is not shown again on reload)
    public static bool MenuDone;

    public static event System.Action Changed;

    static bool loaded;

    // the project enters Play mode without a domain reload, so static values would survive between plays in the
    // editor: start every run with the defaults and the main menu
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetRun()
    {
        loaded = false;
        hasScreen = -1;
        MenuDone = false;
        PendingSolo = null;
        PendingBotsPerTeam = null;
        ZoomSensitivity = DefaultZoomSensitivity;
        BotsFrozen = false;
        TeamSize = 0;
        MenuMap = "";
        BoostPlayerContrast = DefaultBoostPlayerContrast;
        AntiAliasing = DefaultAntiAliasing;
        AgentT = AgentAuto;
        AgentCT = AgentAuto;
        Sensitivity = DefaultSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
        RadarSize = DefaultRadarSize;
        RadarZoom = DefaultRadarZoom;
        Display = DisplayMode.Windowed;
        ScreenWidth = 0;
        ScreenHeight = 0;
        CrosshairSize = DefaultCrosshairSize;
        CrosshairColor = DefaultCrosshairColor;
        ResetCrosshairShape();
    }

    static void ResetCrosshairShape()
    {
        CrosshairStyle = DefaultCrosshairStyle;
        CrosshairGap = DefaultCrosshairGap;
        CrosshairThickness = DefaultCrosshairThickness;
        CrosshairLength = DefaultCrosshairLength;
        CrosshairOutline = DefaultCrosshairOutline;
        CrosshairDynamic = DefaultCrosshairDynamic;
    }

    // the vertical angle for Camera.fieldOfView
    public static float VerticalFov => Camera.HorizontalToVerticalFieldOfView(Fov, 16f / 9f);

    public static Color CrosshairColorValue => CrosshairColors[Mathf.Clamp(CrosshairColor, 0, CrosshairColors.Length - 1)];

    public static string CrosshairColorName
    {
        get
        {
            int i = Mathf.Clamp(CrosshairColor, 0, CrosshairColors.Length - 1);
            return i < CrosshairColorNames.Length ? CrosshairColorNames[i] : "Цвет " + (i + 1);
        }
    }

    public static int CrosshairStyleCount => CrosshairStyleNames.Length;

    public static string CrosshairStyleName
    {
        get
        {
            int i = (int)CrosshairStyle;
            return i >= 0 && i < CrosshairStyleNames.Length ? CrosshairStyleNames[i] : CrosshairStyle.ToString();
        }
    }

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        Sensitivity = PlayerPrefs.GetFloat("sensitivity", Sensitivity);
        ZoomSensitivity = PlayerPrefs.GetFloat("zoomSensitivity", ZoomSensitivity);
        Fov = PlayerPrefs.GetFloat("fov", Fov);
        MasterVolume = PlayerPrefs.GetFloat("volume", MasterVolume);
        ShowRadar = PlayerPrefs.GetInt("radar", ShowRadar ? 1 : 0) == 1;
        RadarSize = PlayerPrefs.GetFloat("radarSize", RadarSize);
        RadarZoom = PlayerPrefs.GetFloat("radarZoom", RadarZoom);
        CrosshairSize = PlayerPrefs.GetFloat("crosshairSize", CrosshairSize);
        CrosshairColor = Mathf.Clamp(PlayerPrefs.GetInt("crosshairColor", CrosshairColor), 0, CrosshairColors.Length - 1);
        CrosshairStyle = (CrosshairStyle)PlayerPrefs.GetInt("crosshairStyle", (int)CrosshairStyle);
        CrosshairGap = PlayerPrefs.GetFloat("crosshairGap", CrosshairGap);
        CrosshairThickness = PlayerPrefs.GetFloat("crosshairThickness", CrosshairThickness);
        CrosshairLength = PlayerPrefs.GetFloat("crosshairLength", CrosshairLength);
        CrosshairOutline = PlayerPrefs.GetInt("crosshairOutline", CrosshairOutline ? 1 : 0) == 1;
        CrosshairDynamic = PlayerPrefs.GetInt("crosshairDynamic", CrosshairDynamic ? 1 : 0) == 1;
        BoostPlayerContrast = PlayerPrefs.GetInt("boostContrast", BoostPlayerContrast ? 1 : 0) == 1;
        AntiAliasing = PlayerPrefs.GetInt("antiAliasing", AntiAliasing);
        AgentT = PlayerPrefs.GetInt("agentT", AgentT);
        AgentCT = PlayerPrefs.GetInt("agentCT", AgentCT);
        BotsFrozen = PlayerPrefs.GetInt("botsFrozen", BotsFrozen ? 1 : 0) == 1;
        TeamSize = PlayerPrefs.GetInt("teamSize", TeamSize);
        MenuMap = PlayerPrefs.GetString("menuMap", MenuMap);
        LoadDisplay();
        Apply();
    }

    // ---------------- screen mode and resolution ----------------

    static bool HasArg(string name)
    {
        foreach (string a in System.Environment.GetCommandLineArgs())
            if (string.Equals(a, name, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // a real game window: not batch mode, not a simulation, with graphics (checked once per run)
    public static bool HasScreen
    {
        get
        {
            if (hasScreen < 0)
                hasScreen = !Application.isBatchMode && !HasArg("-sim") && !HasArg("-nographics")
                    && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null ? 1 : 0;
            return hasScreen == 1;
        }
    }
    static int hasScreen = -1;

    // the window can be changed (in the editor the Game view keeps its own size)
    public static bool CanChangeDisplay => HasScreen && !Application.isEditor;

    // the Unity screen arguments on the command line win over the saved mode for this run
    static bool ScreenArgs()
    {
        return HasArg("-screen-width") || HasArg("-screen-height") || HasArg("-screen-fullscreen")
            || HasArg("-popupwindow") || HasArg("-window-mode");
    }

    public static FullScreenMode ModeOf(DisplayMode d)
    {
        switch (d)
        {
            case DisplayMode.Fullscreen: return FullScreenMode.ExclusiveFullScreen;
            case DisplayMode.Borderless: return FullScreenMode.FullScreenWindow;
            default: return FullScreenMode.Windowed;
        }
    }

    public static DisplayMode DisplayOf(FullScreenMode m)
    {
        if (m == FullScreenMode.ExclusiveFullScreen) return DisplayMode.Fullscreen;
        if (m == FullScreenMode.FullScreenWindow) return DisplayMode.Borderless;
        return DisplayMode.Windowed;   // Windowed and MaximizedWindow
    }

    public static string DisplayModeName
    {
        get
        {
            int i = (int)Display;
            return i >= 0 && i < DisplayModeNames.Length ? DisplayModeNames[i] : Display.ToString();
        }
    }

    // the saved mode and resolution go on the window at the start of the game; the first time (nothing saved)
    // and when the command line sets the screen, the settings take the window the game opened with
    static void LoadDisplay()
    {
        if (!HasScreen) return;
        if (!PlayerPrefs.HasKey("displayMode"))
        {
            ReadDisplay();
            return;
        }

        Display = (DisplayMode)PlayerPrefs.GetInt("displayMode", (int)Display);
        ScreenWidth = PlayerPrefs.GetInt("screenWidth", 0);
        ScreenHeight = PlayerPrefs.GetInt("screenHeight", 0);
        Clamp();
        if (ScreenArgs()) ReadDisplay();
        else ApplyDisplay();
    }

    // the settings take the mode and the size the window has now (the settings screen calls it when it opens,
    // Alt+Enter may have switched the mode)
    public static void ReadDisplay()
    {
        if (!HasScreen) return;
        Display = DisplayOf(Screen.fullScreenMode);
        ScreenWidth = Screen.width;
        ScreenHeight = Screen.height;
        Clamp();
    }

    // the chosen size, or the current one when nothing is chosen
    public static Vector2Int ChosenSize =>
        ScreenWidth > 0 && ScreenHeight > 0 ? new Vector2Int(ScreenWidth, ScreenHeight) : new Vector2Int(Screen.width, Screen.height);

    // puts the chosen mode and size on the window (nothing happens when they are already there)
    public static void ApplyDisplay()
    {
        if (!CanChangeDisplay) return;
        Clamp();
        Vector2Int s = ChosenSize;
        FullScreenMode mode = ModeOf(Display);
        if (DisplayOf(Screen.fullScreenMode) == Display && Screen.width == s.x && Screen.height == s.y) return;
        Screen.SetResolution(s.x, s.y, mode);
    }

    // the size of the monitor itself
    public static Vector2Int NativeSize
    {
        get
        {
            UnityEngine.Display d = UnityEngine.Display.main;
            if (d != null && d.systemWidth > 0 && d.systemHeight > 0) return new Vector2Int(d.systemWidth, d.systemHeight);
            Resolution r = Screen.currentResolution;
            return new Vector2Int(r.width, r.height);
        }
    }

    // the distinct sizes of the monitor (Screen.resolutions has one entry per refresh rate), small to big,
    // with the size of the window now and the chosen one added when they are not in the list
    public static List<Vector2Int> ScreenSizes()
    {
        List<Vector2Int> list = new List<Vector2Int>();
        foreach (Resolution r in Screen.resolutions)
        {
            Vector2Int s = new Vector2Int(r.width, r.height);
            if (s.x >= MinScreenWidth && s.y >= MinScreenHeight && !list.Contains(s)) list.Add(s);
        }
        Vector2Int now = new Vector2Int(Screen.width, Screen.height);
        if (now.x > 0 && now.y > 0 && !list.Contains(now)) list.Add(now);
        Vector2Int chosen = ChosenSize;
        if (chosen.x > 0 && chosen.y > 0 && !list.Contains(chosen)) list.Add(chosen);
        list.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        return list;
    }

    // a new mode from the settings screen; the size follows it: a full-screen mode takes the monitor's own size,
    // a window that would cover the whole monitor gets the biggest size of the list that leaves room for
    // the title bar and the taskbar (at most 90% of the monitor each way, 1600×900 on a 1920×1080 monitor)
    public static void ChooseDisplayMode(DisplayMode mode, List<Vector2Int> sizes)
    {
        if (mode == Display) return;
        Display = mode;
        Vector2Int native = NativeSize;
        if (native.x <= 0 || native.y <= 0) return;

        if (mode != DisplayMode.Windowed)
        {
            ScreenWidth = native.x;
            ScreenHeight = native.y;
            return;
        }

        Vector2Int s = ChosenSize;
        float maxW = native.x * 0.9f, maxH = native.y * 0.9f;
        if (s.x <= maxW && s.y <= maxH) return;
        Vector2Int best = Vector2Int.zero;
        if (sizes != null)
            foreach (Vector2Int c in sizes)
                if (c.x <= maxW && c.y <= maxH && c.x * c.y > best.x * best.y) best = c;
        if (best.x > 0)
        {
            ScreenWidth = best.x;
            ScreenHeight = best.y;
        }
    }

    // the main menu options (team size, static bots, the map): written at once when they change
    public static void SaveMatchOptions()
    {
        Clamp();
        PlayerPrefs.SetInt("botsFrozen", BotsFrozen ? 1 : 0);
        PlayerPrefs.SetInt("teamSize", TeamSize);
        PlayerPrefs.SetString("menuMap", MenuMap ?? "");
        PlayerPrefs.Save();
    }

    // the scene's botsPerTeam remembered for the team size choice (0 = this scene was never seen)
    public static int TeamSizeBaseOf(string scene)
    {
        return PlayerPrefs.GetInt("teamSizeBase." + scene, 0);
    }

    public static void SetTeamSizeBase(string scene, int value)
    {
        PlayerPrefs.SetInt("teamSizeBase." + scene, value);
        PlayerPrefs.Save();
    }

    public static void Save()
    {
        PlayerPrefs.SetFloat("sensitivity", Sensitivity);
        PlayerPrefs.SetFloat("zoomSensitivity", ZoomSensitivity);
        PlayerPrefs.SetFloat("fov", Fov);
        PlayerPrefs.SetFloat("volume", MasterVolume);
        PlayerPrefs.SetInt("radar", ShowRadar ? 1 : 0);
        PlayerPrefs.SetFloat("radarSize", RadarSize);
        PlayerPrefs.SetFloat("radarZoom", RadarZoom);
        if (HasScreen)
        {
            PlayerPrefs.SetInt("displayMode", (int)Display);
            PlayerPrefs.SetInt("screenWidth", ScreenWidth);
            PlayerPrefs.SetInt("screenHeight", ScreenHeight);
        }
        PlayerPrefs.SetFloat("crosshairSize", CrosshairSize);
        PlayerPrefs.SetInt("crosshairColor", CrosshairColor);
        PlayerPrefs.SetInt("crosshairStyle", (int)CrosshairStyle);
        PlayerPrefs.SetFloat("crosshairGap", CrosshairGap);
        PlayerPrefs.SetFloat("crosshairThickness", CrosshairThickness);
        PlayerPrefs.SetFloat("crosshairLength", CrosshairLength);
        PlayerPrefs.SetInt("crosshairOutline", CrosshairOutline ? 1 : 0);
        PlayerPrefs.SetInt("crosshairDynamic", CrosshairDynamic ? 1 : 0);
        PlayerPrefs.SetInt("boostContrast", BoostPlayerContrast ? 1 : 0);
        PlayerPrefs.SetInt("antiAliasing", AntiAliasing);
        PlayerPrefs.SetInt("agentT", AgentT);
        PlayerPrefs.SetInt("agentCT", AgentCT);
        PlayerPrefs.Save();
        Apply();
        ApplyDisplay();
    }

    // back to the defaults (not saved until Save is called); the screen mode and resolution stay,
    // they belong to the monitor and not to the game
    public static void ResetToDefaults()
    {
        Sensitivity = DefaultSensitivity;
        ZoomSensitivity = DefaultZoomSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
        RadarSize = DefaultRadarSize;
        RadarZoom = DefaultRadarZoom;
        CrosshairSize = DefaultCrosshairSize;
        CrosshairColor = DefaultCrosshairColor;
        ResetCrosshairShape();
        BoostPlayerContrast = DefaultBoostPlayerContrast;
        AntiAliasing = DefaultAntiAliasing;
        AgentT = AgentAuto;
        AgentCT = AgentAuto;
        Apply();
    }

    // pushes the values to the engine and tells the listeners (player controller, camera, HUD, radar, the contrast
    // shader, MSAA); the screen mode is not touched here (ApplyDisplay, on Save)
    public static void Apply()
    {
        Clamp();
        AudioListener.volume = MasterVolume;
        CharacterContrast.Enabled = BoostPlayerContrast;
        if (HasScreen) CharacterContrast.Refresh();
        ApplyAntiAliasing();
        Changed?.Invoke();
    }

    // the MSAA can be changed: a player with a screen (in the editor the change would stay in the project's asset)
    public static bool CanChangeAntiAliasing => CanChangeDisplay;

    public static string AntiAliasingName => AntiAliasingNames[Mathf.Clamp(AntiAliasing, 0, AntiAliasingNames.Length - 1)];

    public static string AgentName(int team)
    {
        string[] names = team == 1 ? AgentCTNames : AgentTNames;
        int i = team == 1 ? AgentCT : AgentT;
        return names[Mathf.Clamp(i, 0, names.Length - 1)];
    }

    // MSAA samples of the render pipeline asset in use (Standalone: Assets/Settings/PC_RPAsset, saved with MSAA off),
    // only in a player with a screen: batch runs and simulations draw nothing, the editor must keep the asset as it is
    static void ApplyAntiAliasing()
    {
        if (!CanChangeAntiAliasing) return;
        UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset urp =
            UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline as UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset;
        int samples = AntiAliasingSamples[Mathf.Clamp(AntiAliasing, 0, AntiAliasingSamples.Length - 1)];
        if (urp != null && urp.msaaSampleCount != samples) urp.msaaSampleCount = samples;
    }

    // keeps broken or old saved values inside the allowed ranges
    static void Clamp()
    {
        if (float.IsNaN(Sensitivity)) Sensitivity = DefaultSensitivity;
        if (float.IsNaN(ZoomSensitivity)) ZoomSensitivity = DefaultZoomSensitivity;
        if (float.IsNaN(Fov)) Fov = DefaultFov;
        if (float.IsNaN(MasterVolume)) MasterVolume = DefaultVolume;
        if (float.IsNaN(CrosshairSize)) CrosshairSize = DefaultCrosshairSize;
        if (float.IsNaN(CrosshairGap)) CrosshairGap = DefaultCrosshairGap;
        if (float.IsNaN(CrosshairThickness)) CrosshairThickness = DefaultCrosshairThickness;
        if (float.IsNaN(CrosshairLength)) CrosshairLength = DefaultCrosshairLength;
        if (float.IsNaN(RadarSize)) RadarSize = DefaultRadarSize;
        if (float.IsNaN(RadarZoom)) RadarZoom = DefaultRadarZoom;
        RadarSize = Mathf.Clamp(RadarSize, MinRadarSize, MaxRadarSize);
        RadarZoom = Mathf.Clamp(RadarZoom, MinRadarZoom, MaxRadarZoom);
        if ((int)Display < 0 || (int)Display >= DisplayModeNames.Length) Display = DisplayMode.Windowed;
        // a broken size = nothing chosen (the current size)
        if (ScreenWidth < 320 || ScreenHeight < 200 || ScreenWidth > 16384 || ScreenHeight > 16384)
        {
            ScreenWidth = 0;
            ScreenHeight = 0;
        }
        Sensitivity = Mathf.Clamp(Sensitivity, MinSensitivity, MaxSensitivity);
        ZoomSensitivity = Mathf.Clamp(ZoomSensitivity, MinZoomSensitivity, MaxZoomSensitivity);
        Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
        // 0 stays 0 (not chosen yet)
        if (TeamSize != 0) TeamSize = Mathf.Clamp(TeamSize, MinTeamSize, MaxTeamSize);
        MasterVolume = Mathf.Clamp01(MasterVolume);
        CrosshairSize = Mathf.Clamp(CrosshairSize, MinCrosshairSize, MaxCrosshairSize);
        CrosshairColor = Mathf.Clamp(CrosshairColor, 0, CrosshairColors.Length - 1);
        if ((int)CrosshairStyle < 0 || (int)CrosshairStyle >= CrosshairStyleNames.Length) CrosshairStyle = DefaultCrosshairStyle;
        CrosshairGap = Mathf.Clamp(CrosshairGap, MinCrosshairGap, MaxCrosshairGap);
        CrosshairThickness = Mathf.Clamp(CrosshairThickness, MinCrosshairThickness, MaxCrosshairThickness);
        CrosshairLength = Mathf.Clamp(CrosshairLength, MinCrosshairLength, MaxCrosshairLength);
        AntiAliasing = Mathf.Clamp(AntiAliasing, 0, AntiAliasingSamples.Length - 1);
        if (AgentT < 0 || AgentT >= AgentTNames.Length) AgentT = AgentAuto;
        if (AgentCT < 0 || AgentCT >= AgentCTNames.Length) AgentCT = AgentAuto;
        if (MenuMap == null) MenuMap = "";
    }
}
