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
// The match options of the main menu (players per team, static bots) are saved at once by SaveMatchOptions.
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
        MenuDone = false;
        PendingSolo = null;
        PendingBotsPerTeam = null;
        ZoomSensitivity = DefaultZoomSensitivity;
        BotsFrozen = false;
        TeamSize = 0;
        Sensitivity = DefaultSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
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
        CrosshairSize = PlayerPrefs.GetFloat("crosshairSize", CrosshairSize);
        CrosshairColor = Mathf.Clamp(PlayerPrefs.GetInt("crosshairColor", CrosshairColor), 0, CrosshairColors.Length - 1);
        CrosshairStyle = (CrosshairStyle)PlayerPrefs.GetInt("crosshairStyle", (int)CrosshairStyle);
        CrosshairGap = PlayerPrefs.GetFloat("crosshairGap", CrosshairGap);
        CrosshairThickness = PlayerPrefs.GetFloat("crosshairThickness", CrosshairThickness);
        CrosshairLength = PlayerPrefs.GetFloat("crosshairLength", CrosshairLength);
        CrosshairOutline = PlayerPrefs.GetInt("crosshairOutline", CrosshairOutline ? 1 : 0) == 1;
        CrosshairDynamic = PlayerPrefs.GetInt("crosshairDynamic", CrosshairDynamic ? 1 : 0) == 1;
        BotsFrozen = PlayerPrefs.GetInt("botsFrozen", BotsFrozen ? 1 : 0) == 1;
        TeamSize = PlayerPrefs.GetInt("teamSize", TeamSize);
        Apply();
    }

    // the main menu options (team size, static bots): written at once when they change
    public static void SaveMatchOptions()
    {
        Clamp();
        PlayerPrefs.SetInt("botsFrozen", BotsFrozen ? 1 : 0);
        PlayerPrefs.SetInt("teamSize", TeamSize);
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
        PlayerPrefs.SetFloat("crosshairSize", CrosshairSize);
        PlayerPrefs.SetInt("crosshairColor", CrosshairColor);
        PlayerPrefs.SetInt("crosshairStyle", (int)CrosshairStyle);
        PlayerPrefs.SetFloat("crosshairGap", CrosshairGap);
        PlayerPrefs.SetFloat("crosshairThickness", CrosshairThickness);
        PlayerPrefs.SetFloat("crosshairLength", CrosshairLength);
        PlayerPrefs.SetInt("crosshairOutline", CrosshairOutline ? 1 : 0);
        PlayerPrefs.SetInt("crosshairDynamic", CrosshairDynamic ? 1 : 0);
        PlayerPrefs.Save();
        Apply();
    }

    // back to the defaults (not saved until Save is called)
    public static void ResetToDefaults()
    {
        Sensitivity = DefaultSensitivity;
        ZoomSensitivity = DefaultZoomSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
        CrosshairSize = DefaultCrosshairSize;
        CrosshairColor = DefaultCrosshairColor;
        ResetCrosshairShape();
        Apply();
    }

    // pushes the values to the engine and tells the listeners (player controller, camera, HUD)
    public static void Apply()
    {
        Clamp();
        AudioListener.volume = MasterVolume;
        Changed?.Invoke();
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
    }
}
