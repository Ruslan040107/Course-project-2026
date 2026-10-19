using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// Main menu, pause menu and settings, drawn with IMGUI on top of everything else (GUI.depth -100).
// - Main menu: shown once when the game starts (GameSettings.MenuDone == false) over the frozen game:
//   training (solo on the arena), 5v5 on the arena, 5v5 on the desert map, settings, quit.
//   Choosing a mode always (re)loads its scene; RoundManager takes GameSettings.PendingSolo in Start.
// - Pause: Escape during play. Escape again or "Продолжить" goes back to the game.
// - Settings: mouse sensitivity, FOV, volume, crosshair size and colour, radar. Changes work at once,
//   "Сохранить" writes them to PlayerPrefs, "Назад" (or Escape) drops the unsaved ones.
// While a menu is open the game is paused (Time.timeScale = 0, sound paused) and the cursor is free:
// PlayerShoot and PlayerLoadout only work with a locked cursor, PlayerController skips look and movement.
// The FOV setting is put on the camera by PlayerShoot itself (it listens to GameSettings.Changed and zooms from it).
// It adds itself to every scene with a PlayerController, never in simulations or batch mode.
// "-nomenu" (or the "-shot" screenshot test) on the command line skips the main menu at the start.
public class MainMenu : MonoBehaviour
{
    public const string ArenaScene = "SampleScene";
    public const string DesertScene = "DesertMap";

    enum Page { None, Main, Pause, Settings }

    static MainMenu instance;

    // a menu component runs in this scene (it owns Escape and the cursor)
    public static bool Active => instance != null && instance.isActiveAndEnabled;
    // the main menu, the pause or the settings are on the screen
    public static bool IsOpen => Active && instance.page != Page.None;
    // the frame the menu was closed: the mouse jump of locking the cursor is ignored by the mouse look
    public static int LastClosedFrame = -10;

    public PlayerController player;

    Page page = Page.None;
    Page settingsBack = Page.Main;   // where "Назад" in the settings goes
    bool mainFromPause;              // the main menu was opened from the pause: Escape goes back there
    bool desertAvailable;
    float savedUntil;                // "Настройки сохранены" is shown until this unscaled time

    RoundManager rounds;

    struct Snapshot
    {
        public float sensitivity, fov, volume, crosshairSize;
        public int crosshairColor;
        public bool radar;
    }
    Snapshot snapshot;

    // look
    static readonly Color Accent = new Color(0.96f, 0.64f, 0.2f, 1f);
    static readonly Color AccentHover = new Color(1f, 0.75f, 0.34f, 1f);
    static readonly Color TColor = new Color(0.88f, 0.31f, 0.23f, 1f);
    static readonly Color CTColor = new Color(0.29f, 0.55f, 0.94f, 1f);
    static readonly Color ButtonColor = new Color(0.13f, 0.15f, 0.18f, 0.92f);
    static readonly Color ButtonHover = new Color(0.21f, 0.24f, 0.28f, 0.96f);
    static readonly Color ButtonOff = new Color(0.1f, 0.11f, 0.13f, 0.7f);
    static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.08f);
    static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.09f, 0.95f);
    static readonly Color DimText = new Color(0.68f, 0.71f, 0.75f, 1f);
    static readonly Color BarBack = new Color(1f, 1f, 1f, 0.14f);
    static readonly Color SavedColor = new Color(0.45f, 0.85f, 0.45f, 1f);

    float k = 1f;   // Screen.height / 1080
    int stylesFor = -1;
    GUIStyle titleStyle, subtitleStyle, pageTitleStyle, buttonStyle, buttonCenterStyle, hintStyle;
    GUIStyle labelStyle, valueStyle, infoStyle, footerStyle, statusStyle, smallButtonStyle;
    GUIStyle sliderStyle, thumbStyle;
    Texture2D backgroundTexture, thumbTexture;

    // settings columns (set at the start of DrawSettings)
    float rowLabelX, rowLabelW, rowCtrlX, rowCtrlRight, rowValueX, rowValueW, rowH;

    // ---------------- bootstrap ----------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        LastClosedFrame = -10;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryCreate(SceneManager.GetActiveScene());
    }

    static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryCreate(scene);
    }

    static bool HasArg(string name)
    {
        return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), name) >= 0;
    }

    // a menu for a scene with a human player, never in a simulation or without graphics
    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || HasArg("-sim")) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        PlayerController pc = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<MainMenu>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
            if (pc == null) pc = root.GetComponentInChildren<PlayerController>(true);
        }
        if (pc == null || (rm != null && rm.simulation)) return;

        GameObject go = new GameObject("MainMenu");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<MainMenu>().player = pc;
    }

    void Awake()
    {
        // background runs pass -sim on the command line; RoundManager reads it only in its Start
        rounds = FindAnyObjectByType<RoundManager>();
        if ((rounds != null && rounds.simulation) || HasArg("-sim") || Application.isBatchMode)
        {
            enabled = false;
            return;
        }
        instance = this;

        GameSettings.Load();
        if (HasArg("-nomenu") || HasArg("-shot")) GameSettings.MenuDone = true;

        if (player == null) player = FindAnyObjectByType<PlayerController>();

        desertAvailable = Application.CanStreamedLevelBeLoaded(DesertScene);
        thumbTexture = MakeCircle(64);

        if (!GameSettings.MenuDone) Open(Page.Main);
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;

        // the pause must not outlive the scene (the next scene's menu pauses again if it needs to)
        if (page != Page.None)
        {
            Time.timeScale = 1f;
            AudioListener.pause = false;
        }

        if (backgroundTexture != null) Destroy(backgroundTexture);
        if (thumbTexture != null) Destroy(thumbTexture);
    }

    // ---------------- state ----------------

    void Open(Page p)
    {
        page = p;
        Time.timeScale = 0f;
        AudioListener.pause = true;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Close()
    {
        page = Page.None;
        mainFromPause = false;
        Time.timeScale = 1f;
        AudioListener.pause = false;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        LastClosedFrame = Time.frameCount;
        GUIUtility.keyboardControl = 0;
    }

    void StartMode(string scene, bool solo)
    {
        if (!Application.CanStreamedLevelBeLoaded(scene)) return;

        GameSettings.PendingSolo = solo;
        GameSettings.MenuDone = true;
        Close();
        // always a clean start, also when this scene is already loaded
        SceneManager.LoadScene(scene);
    }

    void OpenSettings(Page from)
    {
        settingsBack = from;
        snapshot = Take();
        savedUntil = 0f;
        page = Page.Settings;
    }

    void BackFromSettings()
    {
        if (Dirty())
        {
            Restore(snapshot);
            GameSettings.Apply();
        }
        page = settingsBack;
        GUIUtility.keyboardControl = 0;
    }

    static void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void Update()
    {
        if (page != Page.None)
        {
            // other scripts must not unpause or lock the cursor under the menu
            Time.timeScale = 0f;
            AudioListener.pause = true;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            switch (page)
            {
                case Page.None: Open(Page.Pause); break;
                case Page.Pause: Close(); break;
                case Page.Settings: BackFromSettings(); break;
                case Page.Main:
                    if (mainFromPause)
                    {
                        mainFromPause = false;
                        page = Page.Pause;
                    }
                    break;
            }
        }
        else if (page == Page.None && Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
        {
            // the cursor got free without the menu (for example the window lost focus): a click takes it back
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            LastClosedFrame = Time.frameCount;
        }
    }

    // ---------------- settings values ----------------

    static Snapshot Take()
    {
        Snapshot s;
        s.sensitivity = GameSettings.Sensitivity;
        s.fov = GameSettings.Fov;
        s.volume = GameSettings.MasterVolume;
        s.crosshairSize = GameSettings.CrosshairSize;
        s.crosshairColor = GameSettings.CrosshairColor;
        s.radar = GameSettings.ShowRadar;
        return s;
    }

    static void Restore(Snapshot s)
    {
        GameSettings.Sensitivity = s.sensitivity;
        GameSettings.Fov = s.fov;
        GameSettings.MasterVolume = s.volume;
        GameSettings.CrosshairSize = s.crosshairSize;
        GameSettings.CrosshairColor = s.crosshairColor;
        GameSettings.ShowRadar = s.radar;
    }

    bool Dirty()
    {
        const float e = 0.0001f;
        return Mathf.Abs(GameSettings.Sensitivity - snapshot.sensitivity) > e
            || Mathf.Abs(GameSettings.Fov - snapshot.fov) > e
            || Mathf.Abs(GameSettings.MasterVolume - snapshot.volume) > e
            || Mathf.Abs(GameSettings.CrosshairSize - snapshot.crosshairSize) > e
            || GameSettings.CrosshairColor != snapshot.crosshairColor
            || GameSettings.ShowRadar != snapshot.radar;
    }

    // ---------------- drawing ----------------

    void OnGUI()
    {
        if (page == Page.None) return;

        GUI.depth = -100;   // above the HUD, the radar, the scoreboard and the sniper scope
        BuildStyles();
        GUI.color = Color.white;

        switch (page)
        {
            case Page.Main:
                DrawBackground();
                DrawMain();
                break;
            case Page.Pause:
                DrawShade();
                DrawPause();
                break;
            case Page.Settings:
                if (settingsBack == Page.Main) DrawBackground();
                else DrawShade();
                DrawSettings();
                break;
        }

        GUI.color = Color.white;
    }

    void DrawBackground()
    {
        if (backgroundTexture == null) backgroundTexture = MakeBackground();
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), backgroundTexture, ScaleMode.ScaleAndCrop);
    }

    static void DrawShade()
    {
        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.6f));
    }

    void DrawMain()
    {
        float w = Screen.width;
        float h = Screen.height;

        ShadowLabel(new Rect(0f, 118f * k, w, 124f * k), "КУРСОВОЙ ШУТЕР", titleStyle);

        // red and blue halves: the two teams
        float stripe = 480f * k;
        Fill(new Rect(w * 0.5f - stripe * 0.5f, 250f * k, stripe * 0.5f - 4f * k, 5f * k), TColor);
        Fill(new Rect(w * 0.5f + 4f * k, 250f * k, stripe * 0.5f - 4f * k, 5f * k), CTColor);
        GUI.Label(new Rect(0f, 266f * k, w, 38f * k), "Тактический шутер 5 на 5 против ботов", subtitleStyle);

        float bw = Mathf.Min(620f * k, w - 32f);
        float bx = (w - bw) * 0.5f;
        float y = 346f * k;
        float modeH = 88f * k;
        float gap = 12f * k;

        if (MenuButton(new Rect(bx, y, bw, modeH), "Тренировка",
                "Арена без ботов: мишень и бесконечные патроны", true, false, false))
            StartMode(ArenaScene, true);
        y += modeH + gap;

        if (MenuButton(new Rect(bx, y, bw, modeH), "Бой 5×5 — Арена",
                "Ты с ботами против команды ботов · карта 50×50 м", true, false, false))
            StartMode(ArenaScene, false);
        y += modeH + gap;

        string desertHint = desertAvailable
            ? "Большая карта 80×64 м: три линии, тоннели, двери"
            : "Карта не добавлена в сборку (Tools → Build Desert Map)";
        if (MenuButton(new Rect(bx, y, bw, modeH), "Бой 5×5 — Пустыня", desertHint, desertAvailable, false, false))
            StartMode(DesertScene, false);
        y += modeH + 28f * k;

        float smallH = 62f * k;
        if (MenuButton(new Rect(bx, y, bw, smallH), "Настройки", null, true, false, false)) OpenSettings(Page.Main);
        y += smallH + gap;
        if (MenuButton(new Rect(bx, y, bw, smallH), "Выход", null, true, false, false)) Quit();

        string footer = "WASD — ходьба    Пробел — прыжок    C — присед    Shift — тихий шаг    R — перезарядка\n"
            + "1–9, колесо — оружие    ПКМ — прицел AWP    TAB — счёт    Esc — пауза";
        if (mainFromPause) footer = "Esc — назад к паузе\n" + footer;
        GUI.Label(new Rect(16f, h - 110f * k, w - 32f, 96f * k), footer, footerStyle);
    }

    void DrawPause()
    {
        float bh = 64f * k;
        float gap = 12f * k;
        float pw = Mathf.Min(560f * k, Screen.width - 32f);
        float ph = (36f + 76f + 40f + 24f + 22f + 30f + 26f) * k + 4f * bh + 3f * gap;
        Rect p = new Rect((Screen.width - pw) * 0.5f, (Screen.height - ph) * 0.5f, pw, ph);
        Panel(p);

        float y = p.y + 36f * k;
        GUI.Label(new Rect(p.x, y, pw, 76f * k), "ПАУЗА", pageTitleStyle);
        y += 76f * k;
        GUI.Label(new Rect(p.x + 16f * k, y, pw - 32f * k, 40f * k), ModeLine(), infoStyle);
        y += (40f + 24f) * k;

        float bx = p.x + 40f * k;
        float bw = pw - 80f * k;
        if (MenuButton(new Rect(bx, y, bw, bh), "Продолжить", null, true, true, true)) Close();
        y += bh + gap;
        if (MenuButton(new Rect(bx, y, bw, bh), "Настройки", null, true, false, true)) OpenSettings(Page.Pause);
        y += bh + gap;
        if (MenuButton(new Rect(bx, y, bw, bh), "Главное меню", null, true, false, true))
        {
            mainFromPause = true;
            page = Page.Main;
        }
        y += bh + gap;
        if (MenuButton(new Rect(bx, y, bw, bh), "Выход", null, true, false, true)) Quit();
        y += bh + 22f * k;

        GUI.Label(new Rect(p.x, y, pw, 30f * k), "Esc — продолжить", footerStyle);
    }

    // "Бой 5×5 — Арена   3 : 2" with the team colours
    string ModeLine()
    {
        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
        if (rounds == null) return MapName();
        if (rounds.IsSolo) return "Тренировка · " + MapName();

        string t = ColorUtility.ToHtmlStringRGB(TColor);
        string ct = ColorUtility.ToHtmlStringRGB(CTColor);
        string line = "Бой 5×5 — " + MapName() + "    <color=#" + t + "><b>" + rounds.score[0] + "</b></color> : <color=#"
            + ct + "><b>" + rounds.score[1] + "</b></color>";
        if (rounds.MatchOver) line += "    (матч окончен)";
        return line;
    }

    static string MapName()
    {
        string scene = SceneManager.GetActiveScene().name;
        if (scene == ArenaScene) return "Арена";
        if (scene == DesertScene) return "Пустыня";
        return scene;
    }

    void DrawSettings()
    {
        float pw = Mathf.Min(1000f * k, Screen.width - 32f);
        float pad = Mathf.Min(40f * k, pw * 0.04f);
        rowH = 70f * k;
        float bh = 64f * k;
        float ph = (32f + 70f + 14f + 14f + 34f + 16f + 32f) * k + 6f * rowH + bh;
        Rect p = new Rect((Screen.width - pw) * 0.5f, (Screen.height - ph) * 0.5f, pw, ph);
        Panel(p);

        float y = p.y + 32f * k;
        GUI.Label(new Rect(p.x, y, pw, 70f * k), "НАСТРОЙКИ", pageTitleStyle);
        y += (70f + 14f) * k;

        rowLabelX = p.x + pad;
        rowLabelW = pw * 0.33f;
        rowValueW = 120f * k;
        rowValueX = p.xMax - pad - rowValueW;
        rowCtrlX = rowLabelX + rowLabelW;
        rowCtrlRight = rowValueX - 16f * k;

        bool changed = false;

        RowBack(p, pad, y, 0);
        float sens = SliderRow(y, "Чувствительность мыши", GameSettings.Sensitivity,
            GameSettings.MinSensitivity, GameSettings.MaxSensitivity, 0.05f, 0.01f, "{0:0.00}");
        if (sens != GameSettings.Sensitivity) { GameSettings.Sensitivity = sens; changed = true; }
        y += rowH;

        RowBack(p, pad, y, 1);
        float fov = SliderRow(y, "Поле зрения (FOV)", GameSettings.Fov, GameSettings.MinFov, GameSettings.MaxFov, 1f, 1f, "{0:0}°");
        if (fov != GameSettings.Fov) { GameSettings.Fov = fov; changed = true; }
        y += rowH;

        RowBack(p, pad, y, 2);
        float shownVolume = Mathf.Round(GameSettings.MasterVolume * 100f);
        float volume = SliderRow(y, "Общая громкость", shownVolume, 0f, 100f, 5f, 1f, "{0:0}%");
        if (volume != shownVolume) { GameSettings.MasterVolume = volume / 100f; changed = true; }
        y += rowH;

        RowBack(p, pad, y, 3);
        float size = SliderRow(y, "Размер прицела", GameSettings.CrosshairSize,
            GameSettings.MinCrosshairSize, GameSettings.MaxCrosshairSize, 0.1f, 0.05f, "{0:0.00}×");
        if (size != GameSettings.CrosshairSize) { GameSettings.CrosshairSize = size; changed = true; }
        y += rowH;

        RowBack(p, pad, y, 4);
        int color = ColorRow(y, GameSettings.CrosshairColor);
        if (color != GameSettings.CrosshairColor) { GameSettings.CrosshairColor = color; changed = true; }
        y += rowH;

        RowBack(p, pad, y, 5);
        bool radar = ToggleRow(y, "Радар", GameSettings.ShowRadar);
        if (radar != GameSettings.ShowRadar) { GameSettings.ShowRadar = radar; changed = true; }
        y += rowH;

        if (changed)
        {
            GameSettings.Apply();
            savedUntil = 0f;
        }

        y += 14f * k;
        if (Time.unscaledTime < savedUntil)
        {
            statusStyle.normal.textColor = SavedColor;
            GUI.Label(new Rect(p.x, y, pw, 34f * k), "Настройки сохранены", statusStyle);
        }
        else if (Dirty())
        {
            statusStyle.normal.textColor = Accent;
            GUI.Label(new Rect(p.x, y, pw, 34f * k), "Есть несохранённые изменения — «Назад» их отменит", statusStyle);
        }
        y += (34f + 16f) * k;

        float gap = 16f * k;
        float inner = pw - pad * 2f;
        float bw = (inner - gap * 2f) / 3f;
        float bx = p.x + pad;

        if (MenuButton(new Rect(bx, y, bw, bh), "Сохранить", null, true, true, true))
        {
            GameSettings.Save();
            snapshot = Take();
            savedUntil = Time.unscaledTime + 2f;
        }
        if (MenuButton(new Rect(bx + bw + gap, y, bw, bh), "По умолчанию", null, true, false, true))
        {
            GameSettings.ResetToDefaults();
            savedUntil = 0f;
        }
        if (MenuButton(new Rect(bx + (bw + gap) * 2f, y, bw, bh), "Назад", null, true, false, true)) BackFromSettings();
    }

    // every second row gets a faint stripe
    void RowBack(Rect panel, float pad, float y, int index)
    {
        if (index % 2 == 0) Fill(new Rect(panel.x + pad * 0.5f, y + 3f * k, panel.width - pad, rowH - 6f * k), new Color(1f, 1f, 1f, 0.035f));
    }

    void RowLabel(float y, string text)
    {
        GUI.Label(new Rect(rowLabelX, y, rowLabelW, rowH), text, labelStyle);
    }

    Rect LeftButtonRect(float y)
    {
        float s = 46f * k;
        return new Rect(rowCtrlX, y + (rowH - s) * 0.5f, s, s);
    }

    Rect RightButtonRect(float y)
    {
        float s = 46f * k;
        return new Rect(rowCtrlRight - s, y + (rowH - s) * 0.5f, s, s);
    }

    Rect ValueRect(float y)
    {
        return new Rect(rowValueX, y, rowValueW, rowH);
    }

    // label, "−", slider, "+", value; step is for the buttons, snap rounds the slider
    float SliderRow(float y, string label, float value, float min, float max, float step, float snap, string format)
    {
        RowLabel(y, label);

        Rect minus = LeftButtonRect(y);
        Rect plus = RightButtonRect(y);
        if (SmallButton(minus, "−")) value = Snap(value - step, step);
        if (SmallButton(plus, "+")) value = Snap(value + step, step);

        float thumb = thumbStyle.fixedWidth;
        float sx = minus.xMax + 16f * k;
        Rect slider = new Rect(sx, y + (rowH - thumb) * 0.5f, plus.x - 16f * k - sx, thumb);

        // the bar is drawn by hand: thin, with the filled part up to the thumb
        float t = Mathf.InverseLerp(min, max, value);
        float barH = Mathf.Max(2f, 6f * k);
        float barX = slider.x + thumb * 0.5f;
        float barW = slider.width - thumb;
        float barY = slider.center.y - barH * 0.5f;
        Fill(new Rect(barX, barY, barW, barH), BarBack);
        Fill(new Rect(barX, barY, barW * t, barH), Accent);

        GUI.color = slider.Contains(Event.current.mousePosition) ? AccentHover : Color.white;
        float moved = GUI.HorizontalSlider(slider, value, min, max, sliderStyle, thumbStyle);
        GUI.color = Color.white;
        if (moved != value) value = Snap(moved, snap);

        value = Mathf.Clamp(value, min, max);
        GUI.Label(ValueRect(y), string.Format(CultureInfo.InvariantCulture, format, value), valueStyle);
        return value;
    }

    static float Snap(float v, float step)
    {
        return step > 0f ? Mathf.Round(v / step) * step : v;
    }

    // crosshair colour: "◄", the colour with its name (click = next), "►", a small preview of the crosshair
    int ColorRow(float y, int index)
    {
        RowLabel(y, "Цвет прицела");

        int n = GameSettings.CrosshairColors.Length;
        Rect prev = LeftButtonRect(y);
        Rect next = RightButtonRect(y);
        if (SmallButton(prev, "◄")) index = (index - 1 + n) % n;
        if (SmallButton(next, "►")) index = (index + 1) % n;

        Rect mid = new Rect(prev.xMax + 12f * k, prev.y, next.x - 12f * k - (prev.xMax + 12f * k), prev.height);
        bool hover = mid.Contains(Event.current.mousePosition);
        Fill(mid, hover ? ButtonHover : ButtonColor);
        Frame(mid, BorderColor);

        float sw = 26f * k;
        Fill(new Rect(mid.x + 16f * k, mid.center.y - sw * 0.5f, sw, sw), GameSettings.CrosshairColorValue);
        GUI.Label(new Rect(mid.x + 16f * k + sw + 14f * k, mid.y, mid.width - sw - 40f * k, mid.height),
            GameSettings.CrosshairColorName, labelStyle);
        if (GUI.Button(mid, GUIContent.none, GUIStyle.none)) index = (index + 1) % n;

        // preview on a dark square in the value column
        Rect v = ValueRect(y);
        float box = Mathf.Min(rowH - 12f * k, v.width);
        Rect pr = new Rect(v.xMax - box, v.y + (rowH - box) * 0.5f, box, box);
        Fill(pr, new Color(0.03f, 0.035f, 0.04f, 1f));
        Frame(pr, BorderColor);
        DrawCrosshairPreview(pr.center, GameSettings.CrosshairColors[Mathf.Clamp(index, 0, n - 1)]);

        return Mathf.Clamp(index, 0, n - 1);
    }

    void DrawCrosshairPreview(Vector2 c, Color color)
    {
        float s = GameSettings.CrosshairSize * k;
        float len = 9f * s;
        float gap = 4f * s;
        float th = Mathf.Max(1f, Mathf.Round(2f * s));
        Fill(new Rect(c.x - gap - len, c.y - th * 0.5f, len, th), color);
        Fill(new Rect(c.x + gap, c.y - th * 0.5f, len, th), color);
        Fill(new Rect(c.x - th * 0.5f, c.y - gap - len, th, len), color);
        Fill(new Rect(c.x - th * 0.5f, c.y + gap, th, len), color);
    }

    // on/off over the whole control column
    bool ToggleRow(float y, string label, bool on)
    {
        RowLabel(y, label);

        float s = 46f * k;
        Rect r = new Rect(rowCtrlX, y + (rowH - s) * 0.5f, rowCtrlRight - rowCtrlX, s);
        bool hover = r.Contains(Event.current.mousePosition);
        Fill(r, hover ? ButtonHover : ButtonColor);
        Frame(r, BorderColor);

        float dot = 18f * k;
        Fill(new Rect(r.x + 18f * k, r.center.y - dot * 0.5f, dot, dot), on ? SavedColor : new Color(0.4f, 0.42f, 0.45f, 1f));
        GUI.Label(new Rect(r.x + 18f * k + dot + 14f * k, r.y, r.width - dot - 40f * k, r.height), on ? "Включён" : "Выключен", labelStyle);

        if (GUI.Button(r, GUIContent.none, GUIStyle.none)) on = !on;
        return on;
    }

    // a big flat button; primary = filled with the accent colour; with a hint the title goes up and the hint under it
    bool MenuButton(Rect r, string title, string hint, bool enabled, bool primary, bool centered)
    {
        bool hover = enabled && r.Contains(Event.current.mousePosition);

        Color back;
        if (!enabled) back = ButtonOff;
        else if (primary) back = hover ? AccentHover : Accent;
        else back = hover ? ButtonHover : ButtonColor;
        Fill(r, back);
        Frame(r, BorderColor);
        if (hover && !primary) Fill(new Rect(r.x, r.y, Mathf.Max(3f, 6f * k), r.height), Accent);

        Color text = !enabled ? new Color(1f, 1f, 1f, 0.35f) : primary ? new Color(0.08f, 0.08f, 0.09f, 1f) : Color.white;
        GUIStyle style = centered ? buttonCenterStyle : buttonStyle;
        float padX = 28f * k;

        GUI.color = text;
        if (string.IsNullOrEmpty(hint))
        {
            GUI.Label(new Rect(r.x + padX, r.y, r.width - padX * 2f, r.height), title, style);
        }
        else
        {
            GUI.Label(new Rect(r.x + padX, r.y + 10f * k, r.width - padX * 2f, 40f * k), title, style);
            GUI.color = enabled ? Color.white : new Color(1f, 1f, 1f, 0.6f);
            GUI.Label(new Rect(r.x + padX, r.y + 50f * k, r.width - padX * 2f, 28f * k), hint, hintStyle);
        }
        GUI.color = Color.white;

        return GUI.Button(r, GUIContent.none, GUIStyle.none) && enabled;
    }

    bool SmallButton(Rect r, string text)
    {
        bool hover = r.Contains(Event.current.mousePosition);
        Fill(r, hover ? ButtonHover : ButtonColor);
        Frame(r, hover ? new Color(Accent.r, Accent.g, Accent.b, 0.6f) : BorderColor);
        GUI.Label(r, text, smallButtonStyle);
        return GUI.Button(r, GUIContent.none, GUIStyle.none);
    }

    void Panel(Rect p)
    {
        Fill(p, PanelColor);
        Frame(p, BorderColor);
        float line = Mathf.Max(2f, 4f * k);
        Fill(new Rect(p.x, p.y, p.width * 0.5f, line), TColor);
        Fill(new Rect(p.x + p.width * 0.5f, p.y, p.width * 0.5f, line), CTColor);
    }

    void ShadowLabel(Rect r, string text, GUIStyle style)
    {
        float o = Mathf.Max(1f, 4f * k);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, style);
        GUI.color = Color.white;
        GUI.Label(r, text, style);
    }

    static void Fill(Rect r, Color c)
    {
        Color old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    static void Frame(Rect r, Color c)
    {
        Fill(new Rect(r.x, r.y, r.width, 1f), c);
        Fill(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
        Fill(new Rect(r.x, r.y + 1f, 1f, r.height - 2f), c);
        Fill(new Rect(r.xMax - 1f, r.y + 1f, 1f, r.height - 2f), c);
    }

    // ---------------- styles and textures ----------------

    void BuildStyles()
    {
        if (titleStyle != null && stylesFor == Screen.height) return;
        stylesFor = Screen.height;
        k = Mathf.Max(0.3f, Screen.height / 1080f);

        titleStyle = MakeLabel(96, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        subtitleStyle = MakeLabel(24, FontStyle.Normal, TextAnchor.MiddleCenter, DimText);
        pageTitleStyle = MakeLabel(50, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        buttonStyle = MakeLabel(30, FontStyle.Bold, TextAnchor.MiddleLeft, Color.white);
        buttonCenterStyle = MakeLabel(28, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        hintStyle = MakeLabel(19, FontStyle.Normal, TextAnchor.MiddleLeft, DimText);
        labelStyle = MakeLabel(25, FontStyle.Normal, TextAnchor.MiddleLeft, Color.white);
        valueStyle = MakeLabel(25, FontStyle.Bold, TextAnchor.MiddleRight, Color.white);
        infoStyle = MakeLabel(24, FontStyle.Normal, TextAnchor.MiddleCenter, DimText);
        footerStyle = MakeLabel(18, FontStyle.Normal, TextAnchor.LowerCenter, DimText);
        footerStyle.wordWrap = true;
        statusStyle = MakeLabel(21, FontStyle.Normal, TextAnchor.MiddleCenter, Accent);
        smallButtonStyle = MakeLabel(30, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);

        // the slider has no background of its own (the bar is drawn by hand), the thumb is a round dot
        sliderStyle = new GUIStyle();
        thumbStyle = new GUIStyle();
        thumbStyle.normal.background = thumbTexture;
        thumbStyle.hover.background = thumbTexture;
        thumbStyle.active.background = thumbTexture;
        thumbStyle.focused.background = thumbTexture;
        thumbStyle.fixedWidth = Mathf.Round(28f * k);
        thumbStyle.fixedHeight = Mathf.Round(28f * k);
    }

    GUIStyle MakeLabel(int size, FontStyle fontStyle, TextAnchor anchor, Color color)
    {
        GUIStyle s = new GUIStyle(GUI.skin.label);
        s.fontSize = Mathf.Max(9, Mathf.RoundToInt(size * k));
        s.fontStyle = fontStyle;
        s.alignment = anchor;
        s.normal.textColor = color;
        s.hover.textColor = color;
        s.wordWrap = false;
        s.richText = true;
        s.padding = new RectOffset(0, 0, 0, 0);
        s.margin = new RectOffset(0, 0, 0, 0);
        return s;
    }

    // dark blue-grey with a red glow at the bottom left (T) and a blue one at the top right (CT)
    static Texture2D MakeBackground()
    {
        const int w = 320, h = 180;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        Color low = new Color(0.03f, 0.035f, 0.045f);
        Color high = new Color(0.085f, 0.1f, 0.125f);
        Color32[] px = new Color32[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float u = x / (w - 1f);
                float v = y / (h - 1f);   // 0 = bottom
                Color c = Color.Lerp(low, high, v);

                float ax = (u - 0.08f) * 1.78f, ay = v - 0.1f;
                float bx = (u - 0.92f) * 1.78f, by = v - 0.9f;
                c += TColor * (0.2f * Mathf.Exp(-(ax * ax + ay * ay) * 3.5f));
                c += CTColor * (0.2f * Mathf.Exp(-(bx * bx + by * by) * 3.5f));

                // faint diagonal stripes and a vignette
                if (((x + y) / 4) % 2 == 0) c *= 0.965f;
                float dx = (u - 0.5f) * 1.6f, dy = (v - 0.5f) * 1.6f;
                c *= 1f - 0.45f * Mathf.Clamp01(dx * dx + dy * dy);

                c.a = 1f;
                px[y * w + x] = c;
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    // white disc with a soft edge (tinted by GUI.color)
    static Texture2D MakeCircle(int n)
    {
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float r = n * 0.5f;
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                float a = Mathf.Clamp01(r - 1f - d);
                // a darker ring inside the edge, so the dot stands out on the bar
                float shade = d > r - 6f ? 0.8f : 1f;
                px[y * n + x] = new Color(shade, shade, shade, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }
}
