using UnityEngine;

// Player settings (saved in PlayerPrefs) and the choice made in the main menu for the next match.
// Simulations never read these: RoundManager ignores them when it simulates.
// The settings screen is in MainMenu.cs (Escape → Настройки).
public static class GameSettings
{
    // defaults (also used by "По умолчанию" in the settings screen)
    public const float DefaultSensitivity = 2f;
    public const float DefaultFov = 90f;
    public const float DefaultVolume = 0.8f;
    public const bool DefaultShowRadar = true;
    public const float DefaultCrosshairSize = 1f;
    public const int DefaultCrosshairColor = 0;

    // allowed ranges (the sliders of the settings screen use them too)
    public const float MinSensitivity = 0.1f;
    public const float MaxSensitivity = 10f;
    public const float MinFov = 70f;
    public const float MaxFov = 110f;
    public const float MinCrosshairSize = 0.5f;
    public const float MaxCrosshairSize = 2f;

    // mouse sensitivity multiplier used by PlayerController (the AWP scope multiplies it further)
    public static float Sensitivity = DefaultSensitivity;
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

    // the mode chosen in the main menu for the next scene load; null = the scene's own settings
    public static bool? PendingSolo;

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
        Sensitivity = DefaultSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
        CrosshairSize = DefaultCrosshairSize;
        CrosshairColor = DefaultCrosshairColor;
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

    public static void Load()
    {
        if (loaded) return;
        loaded = true;
        Sensitivity = PlayerPrefs.GetFloat("sensitivity", Sensitivity);
        Fov = PlayerPrefs.GetFloat("fov", Fov);
        MasterVolume = PlayerPrefs.GetFloat("volume", MasterVolume);
        ShowRadar = PlayerPrefs.GetInt("radar", ShowRadar ? 1 : 0) == 1;
        CrosshairSize = PlayerPrefs.GetFloat("crosshairSize", CrosshairSize);
        CrosshairColor = Mathf.Clamp(PlayerPrefs.GetInt("crosshairColor", CrosshairColor), 0, CrosshairColors.Length - 1);
        Apply();
    }

    public static void Save()
    {
        PlayerPrefs.SetFloat("sensitivity", Sensitivity);
        PlayerPrefs.SetFloat("fov", Fov);
        PlayerPrefs.SetFloat("volume", MasterVolume);
        PlayerPrefs.SetInt("radar", ShowRadar ? 1 : 0);
        PlayerPrefs.SetFloat("crosshairSize", CrosshairSize);
        PlayerPrefs.SetInt("crosshairColor", CrosshairColor);
        PlayerPrefs.Save();
        Apply();
    }

    // back to the defaults (not saved until Save is called)
    public static void ResetToDefaults()
    {
        Sensitivity = DefaultSensitivity;
        Fov = DefaultFov;
        MasterVolume = DefaultVolume;
        ShowRadar = DefaultShowRadar;
        CrosshairSize = DefaultCrosshairSize;
        CrosshairColor = DefaultCrosshairColor;
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
        if (float.IsNaN(Fov)) Fov = DefaultFov;
        if (float.IsNaN(MasterVolume)) MasterVolume = DefaultVolume;
        if (float.IsNaN(CrosshairSize)) CrosshairSize = DefaultCrosshairSize;
        Sensitivity = Mathf.Clamp(Sensitivity, MinSensitivity, MaxSensitivity);
        Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
        MasterVolume = Mathf.Clamp01(MasterVolume);
        CrosshairSize = Mathf.Clamp(CrosshairSize, MinCrosshairSize, MaxCrosshairSize);
        CrosshairColor = Mathf.Clamp(CrosshairColor, 0, CrosshairColors.Length - 1);
    }
}
