using System.Globalization;
using UnityEngine;
using UnityEngine.SceneManagement;

// Main menu, pause menu and settings, drawn with IMGUI on top of everything else (GUI.depth -100).
// - Main menu: shown once when the game starts (GameSettings.MenuDone == false) over the frozen game:
//   training (solo on the arena), N×N on the arena, N×N on the desert map, the match options, settings, quit.
//   Match options: players per team 1..10 with the player (−/+ or the mouse wheel; default = the first scene's
//   RoundManager.botsPerTeam from the Inspector, the choice is saved and is dropped when that Inspector value
//   changes) and "Боты стоят на месте" (static bots, saved; it also works at once from the pause → main menu).
//   Choosing a mode always (re)loads its scene; RoundManager takes GameSettings.PendingSolo,
//   PendingBotsPerTeam and BotsFrozen when the match starts.
// - Pause: Escape during play. Escape again or "Продолжить" goes back to the game.
// - Settings in two columns: "Игра" (mouse sensitivity, sensitivity in the zoom, FOV, volume, radar) with a live preview of the crosshair
//   under it, and "Прицел" (style, colour, size, gap, thickness, length, outline, dynamic; GameHud draws it).
//   A narrow screen gets one scrolling column. Changes work at once,
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
    // the saved team size was compared with the scene's botsPerTeam (once per run, before any match changed it)
    static bool teamSizeChecked;
    // the scene's value when nothing was chosen (and when there is no RoundManager)
    const int FallbackTeamSize = 5;
    static readonly GUIContent tempContent = new GUIContent();

    public PlayerController player;

    Page page = Page.None;
    Page settingsBack = Page.Main;   // where "Назад" in the settings goes
    bool mainFromPause;              // the main menu was opened from the pause: Escape goes back there
    bool desertAvailable;
    float savedUntil;                // "Настройки сохранены" is shown until this unscaled time

    RoundManager rounds;

    // texts that depend on the team size, rebuilt when it changes
    int textsFor = -1;
    string arenaTitle, desertTitle, subtitleText, teamHint;

    struct Snapshot
    {
        public float sensitivity, zoomSensitivity, fov, volume, crosshairSize;
        public int crosshairColor;
        public bool radar;
        public CrosshairStyle crosshairStyle;
        public float crosshairGap, crosshairThickness, crosshairLength;
        public bool crosshairOutline, crosshairDynamic;
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
    GUIStyle labelSmallStyle, optionValueStyle;
    GUIStyle sliderStyle, thumbStyle, sectionStyle, previewCaptionStyle;
    Texture2D backgroundTexture, thumbTexture;

    // settings columns (set at the start of DrawSettings)
    float rowColX, rowColW, rowLabelX, rowLabelW, rowCtrlX, rowCtrlRight, rowValueX, rowValueW, rowH;
    Vector2 settingsScroll;   // the one column layout of a narrow screen scrolls

    // ---------------- bootstrap ----------------

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        LastClosedFrame = -10;
        teamSizeChecked = false;
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
        InitTeamSize();
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

    // ---------------- match options ----------------

    // players per team with the player (like RoundManager.botsPerTeam)
    static int TeamSize => Mathf.Clamp(GameSettings.TeamSize > 0 ? GameSettings.TeamSize : FallbackTeamSize,
        GameSettings.MinTeamSize, GameSettings.MaxTeamSize);

    // once per run, in the first scene: nothing chosen yet, the scene is seen for the first time, or the Inspector
    // value of this scene changed since the last check -> the scene's value. Each scene remembers its own value, so
    // starting the editor from another scene (the desert has its own botsPerTeam) throws the choice away only once.
    void InitTeamSize()
    {
        if (teamSizeChecked) return;
        teamSizeChecked = true;

        // SceneBotsPerTeam: the Inspector value, before the menu choice or -bots changed botsPerTeam
        int value = rounds != null ? rounds.SceneBotsPerTeam : FallbackTeamSize;
        value = Mathf.Clamp(value, GameSettings.MinTeamSize, GameSettings.MaxTeamSize);
        string scene = rounds != null ? rounds.gameObject.scene.name : gameObject.scene.name;
        int seen = GameSettings.TeamSizeBaseOf(scene);

        // a scene seen for the first time counts as changed too: the menu then shows the value set in its Inspector
        bool changed = false;
        if (GameSettings.TeamSize <= 0 || seen != value)
        {
            GameSettings.TeamSize = value;
            changed = true;
        }
        if (seen != value) GameSettings.SetTeamSizeBase(scene, value);
        if (changed) GameSettings.SaveMatchOptions();
    }

    static void SetTeamSize(int size)
    {
        size = Mathf.Clamp(size, GameSettings.MinTeamSize, GameSettings.MaxTeamSize);
        if (size == GameSettings.TeamSize) return;
        GameSettings.TeamSize = size;
        GameSettings.SaveMatchOptions();
    }

    // "Бой 5×5 — Арена", the subtitle and "Ты и 4 бота против 5 ботов" for the current team size
    void BuildTeamTexts()
    {
        int n = TeamSize;
        if (n == textsFor) return;
        textsFor = n;

        string size = n + "×" + n;
        arenaTitle = "Бой " + size + " — Арена";
        desertTitle = "Бой " + size + " — Пустыня";
        subtitleText = "Тактический шутер " + n + " на " + n + " против ботов";

        int allies = n - 1;
        string me = allies == 0 ? "Ты один" : "Ты и " + allies + " " + Plural(allies, "бот", "бота", "ботов");
        // after "против": 1 бота, 2 ботов, 5 ботов, 21 бота
        teamHint = me + " против " + n + " " + (n % 10 == 1 && n % 100 != 11 ? "бота" : "ботов");
    }

    // Russian plural: 1 бот, 2 бота, 5 ботов
    static string Plural(int n, string one, string few, string many)
    {
        int d = n % 10, h = n % 100;
        if (d == 1 && h != 11) return one;
        if (d >= 2 && d <= 4 && (h < 12 || h > 14)) return few;
        return many;
    }

    void StartMode(string scene, bool solo)
    {
        if (!Application.CanStreamedLevelBeLoaded(scene)) return;

        GameSettings.PendingSolo = solo;
        // the training has no bots: it keeps the scene's own value
        GameSettings.PendingBotsPerTeam = solo ? (int?)null : TeamSize;
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

        // the Escape that closes the buy menu (BuyMenu.IsOpen is still true in that frame) does not open the pause
        if (Input.GetKeyDown(KeyCode.Escape) && !BuyMenu.IsOpen)
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
        else if (page == Page.None && !BuyMenu.IsOpen && Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
        {
            // the cursor got free without the menu (for example the window lost focus): a click takes it back
            // (not while the buy menu is open: its clicks buy weapons)
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
        s.zoomSensitivity = GameSettings.ZoomSensitivity;
        s.fov = GameSettings.Fov;
        s.volume = GameSettings.MasterVolume;
        s.crosshairSize = GameSettings.CrosshairSize;
        s.crosshairColor = GameSettings.CrosshairColor;
        s.radar = GameSettings.ShowRadar;
        s.crosshairStyle = GameSettings.CrosshairStyle;
        s.crosshairGap = GameSettings.CrosshairGap;
        s.crosshairThickness = GameSettings.CrosshairThickness;
        s.crosshairLength = GameSettings.CrosshairLength;
        s.crosshairOutline = GameSettings.CrosshairOutline;
        s.crosshairDynamic = GameSettings.CrosshairDynamic;
        return s;
    }

    static void Restore(Snapshot s)
    {
        GameSettings.Sensitivity = s.sensitivity;
        GameSettings.ZoomSensitivity = s.zoomSensitivity;
        GameSettings.Fov = s.fov;
        GameSettings.MasterVolume = s.volume;
        GameSettings.CrosshairSize = s.crosshairSize;
        GameSettings.CrosshairColor = s.crosshairColor;
        GameSettings.ShowRadar = s.radar;
        GameSettings.CrosshairStyle = s.crosshairStyle;
        GameSettings.CrosshairGap = s.crosshairGap;
        GameSettings.CrosshairThickness = s.crosshairThickness;
        GameSettings.CrosshairLength = s.crosshairLength;
        GameSettings.CrosshairOutline = s.crosshairOutline;
        GameSettings.CrosshairDynamic = s.crosshairDynamic;
    }

    bool Dirty()
    {
        const float e = 0.0001f;
        return Mathf.Abs(GameSettings.Sensitivity - snapshot.sensitivity) > e
            || Mathf.Abs(GameSettings.ZoomSensitivity - snapshot.zoomSensitivity) > e
            || Mathf.Abs(GameSettings.Fov - snapshot.fov) > e
            || Mathf.Abs(GameSettings.MasterVolume - snapshot.volume) > e
            || Mathf.Abs(GameSettings.CrosshairSize - snapshot.crosshairSize) > e
            || GameSettings.CrosshairColor != snapshot.crosshairColor
            || GameSettings.ShowRadar != snapshot.radar
            || GameSettings.CrosshairStyle != snapshot.crosshairStyle
            || Mathf.Abs(GameSettings.CrosshairGap - snapshot.crosshairGap) > e
            || Mathf.Abs(GameSettings.CrosshairThickness - snapshot.crosshairThickness) > e
            || Mathf.Abs(GameSettings.CrosshairLength - snapshot.crosshairLength) > e
            || GameSettings.CrosshairOutline != snapshot.crosshairOutline
            || GameSettings.CrosshairDynamic != snapshot.crosshairDynamic;
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

        BuildTeamTexts();

        // red and blue halves: the two teams
        float stripe = 480f * k;
        Fill(new Rect(w * 0.5f - stripe * 0.5f, 250f * k, stripe * 0.5f - 4f * k, 5f * k), TColor);
        Fill(new Rect(w * 0.5f + 4f * k, 250f * k, stripe * 0.5f - 4f * k, 5f * k), CTColor);
        GUI.Label(new Rect(0f, 266f * k, w, 38f * k), subtitleText, subtitleStyle);

        float bw = Mathf.Min(620f * k, w - 32f);
        float bx = (w - bw) * 0.5f;
        float y = 326f * k;
        float modeH = 84f * k;
        float optionH = 78f * k;
        float gap = 10f * k;

        if (MenuButton(new Rect(bx, y, bw, modeH), "Тренировка",
                "Арена без ботов: мишень и бесконечные патроны", true, false, false))
            StartMode(ArenaScene, true);
        y += modeH + gap;

        if (MenuButton(new Rect(bx, y, bw, modeH), arenaTitle,
                "Арена 50×50 м с укрытиями", true, false, false))
            StartMode(ArenaScene, false);
        y += modeH + gap;

        string desertHint = desertAvailable
            ? "Большая карта 80×64 м: три линии, тоннели, двери"
            : "Карта не добавлена в сборку (Tools → Build Desert Map)";
        if (MenuButton(new Rect(bx, y, bw, modeH), desertTitle, desertHint, desertAvailable, false, false))
            StartMode(DesertScene, false);
        y += modeH + 14f * k;

        // the options of the two battles above (not of the training)
        TeamSizeRow(new Rect(bx, y, bw, optionH));
        y += optionH + 8f * k;
        FrozenBotsRow(new Rect(bx, y, bw, optionH));
        y += optionH + 24f * k;

        float smallH = 60f * k;
        if (MenuButton(new Rect(bx, y, bw, smallH), "Настройки", null, true, false, false)) OpenSettings(Page.Main);
        y += smallH + gap;
        if (MenuButton(new Rect(bx, y, bw, smallH), "Выход", null, true, false, false)) Quit();

        string footer = "WASD — ходьба    Пробел — прыжок    C — присед    Shift — тихий шаг    R — перезарядка\n"
            + "1–4, колесо, Q — оружие    ПКМ — прицел / режим огня    B — закупка    G / E — выбросить / поднять    TAB — счёт    Esc — пауза";
        if (mainFromPause) footer = "Esc — назад к паузе\n" + footer;
        GUI.Label(new Rect(16f, h - 110f * k, w - 32f, 96f * k), footer, footerStyle);
    }

    // "Игроков в команде  [−] 5 [+]" with "Ты и 4 бота против 5 ботов" under it; the mouse wheel over the row works too
    void TeamSizeRow(Rect r)
    {
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        Fill(r, hover ? ButtonHover : ButtonColor);
        Frame(r, BorderColor);
        if (hover) Fill(new Rect(r.x, r.y, Mathf.Max(3f, 6f * k), r.height), Accent);

        int size = TeamSize;
        float padX = 28f * k;
        float s = Mathf.Round(48f * k);
        float valueW = Mathf.Round(64f * k);
        Rect plus = new Rect(r.xMax - padX - s, Mathf.Round(r.center.y - s * 0.5f), s, s);
        Rect value = new Rect(plus.x - valueW, r.y, valueW, r.height);
        Rect minus = new Rect(value.x - s, plus.y, s, s);

        float textW = Mathf.Max(0f, minus.x - 16f * k - (r.x + padX));
        GUI.Label(new Rect(r.x + padX, r.y + 10f * k, textW, 40f * k), "Игроков в команде", buttonStyle);
        GUI.Label(new Rect(r.x + padX, r.y + 48f * k, textW, 26f * k), teamHint, hintStyle);

        int step = 0;
        if (SmallButton(minus, "−", size > GameSettings.MinTeamSize)) step = -1;
        if (SmallButton(plus, "+", size < GameSettings.MaxTeamSize)) step = 1;
        GUI.Label(value, size.ToString(CultureInfo.InvariantCulture), optionValueStyle);

        // wheel up = more players
        if (hover && e.type == EventType.ScrollWheel && e.delta.y != 0f)
        {
            step = e.delta.y < 0f ? 1 : -1;
            e.Use();
        }
        if (step != 0) SetTeamSize(size + step);
    }

    // "Боты стоят на месте": a click on the row switches it (saved at once, RoundManager reads it when the match starts)
    void FrozenBotsRow(Rect r)
    {
        bool on = GameSettings.BotsFrozen;
        bool hover = r.Contains(Event.current.mousePosition);
        Fill(r, hover ? ButtonHover : ButtonColor);
        Frame(r, BorderColor);
        if (hover) Fill(new Rect(r.x, r.y, Mathf.Max(3f, 6f * k), r.height), Accent);

        float padX = 28f * k;
        float pillW = Mathf.Round(112f * k);
        float pillH = Mathf.Round(40f * k);
        Rect pill = new Rect(r.xMax - padX - pillW, Mathf.Round(r.center.y - pillH * 0.5f), pillW, pillH);
        Fill(pill, on ? SavedColor : new Color(0.3f, 0.32f, 0.35f, 1f));
        Frame(pill, BorderColor);
        GUI.color = on ? new Color(0.06f, 0.08f, 0.06f, 1f) : Color.white;
        GUI.Label(pill, on ? "ВКЛ" : "ВЫКЛ", buttonCenterStyle);
        GUI.color = Color.white;

        float textW = Mathf.Max(0f, pill.x - 16f * k - (r.x + padX));
        string hint = on ? "Не двигаются и не стреляют, их можно убить" : "Выключено: боты ходят и стреляют";
        GUI.Label(new Rect(r.x + padX, r.y + 10f * k, textW, 40f * k), "Боты стоят на месте", buttonStyle);
        GUI.Label(new Rect(r.x + padX, r.y + 48f * k, textW, 26f * k), hint, hintStyle);

        if (GUI.Button(r, GUIContent.none, GUIStyle.none))
        {
            GameSettings.BotsFrozen = !on;
            GameSettings.SaveMatchOptions();
        }
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

    // "Бой 5×5 — Арена   3 : 2" with the team colours; the size is counted from the players of the match
    string ModeLine()
    {
        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
        if (rounds == null) return MapName();
        if (rounds.IsSolo) return "Тренировка · " + MapName();

        int a = 0, b = 0;
        foreach (Health h in Health.All)
        {
            if (h == null || h.invulnerable) continue;   // the training target is not a player
            if (h.team == 0) a++;
            else if (h.team == 1) b++;
        }
        if (a == 0 && b == 0) a = b = rounds.botsPerTeam;

        string t = ColorUtility.ToHtmlStringRGB(TColor);
        string ct = ColorUtility.ToHtmlStringRGB(CTColor);
        string line = "Бой " + a + "×" + b + " — " + MapName() + "    <color=#" + t + "><b>" + rounds.score[0] + "</b></color> : <color=#"
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

    // Two columns side by side ("Игра" with the crosshair preview under it, "Прицел"); on a narrow screen
    // one column in a scroll view. Status line and the buttons stay under the rows.
    void DrawSettings()
    {
        float pad = 36f * k;
        rowH = Mathf.Round(60f * k);
        float headH = Mathf.Round(44f * k);
        float bh = 64f * k;
        float colGap = 40f * k;
        float previewH = 4f * rowH - headH;

        float pw = Mathf.Min(1560f * k, Screen.width - 32f);
        float colW = (pw - pad * 2f - colGap) * 0.5f;
        bool twoColumns = colW >= 560f * k;
        if (!twoColumns)
        {
            pw = Mathf.Min(860f * k, Screen.width - 32f);
            pad = Mathf.Min(pad, pw * 0.04f);
        }

        float topH = (28f + 70f + 10f) * k;
        float bottomH = (12f + 34f + 12f + 28f) * k + bh;
        float sectionGap = 16f * k;
        // two columns: the taller of "Игра" + preview and "Прицел"; one column: everything under each other
        float contentH = twoColumns
            ? Mathf.Max(headH * 2f + GameRowCount * rowH + previewH, headH + CrosshairRowCount * rowH)
            : headH * 2f + (GameRowCount + CrosshairRowCount) * rowH + previewH + sectionGap * 2f;
        float maxContent = Mathf.Max(rowH, Screen.height - 32f - topH - bottomH);
        float viewH = Mathf.Min(contentH, maxContent);

        float ph = topH + viewH + bottomH;
        Rect p = new Rect((Screen.width - pw) * 0.5f, (Screen.height - ph) * 0.5f, pw, ph);
        Panel(p);

        float y = p.y + 28f * k;
        GUI.Label(new Rect(p.x, y, pw, 70f * k), "НАСТРОЙКИ", pageTitleStyle);
        y += (70f + 10f) * k;

        bool changed = false;

        if (twoColumns)
        {
            float lx = p.x + pad;
            float rx = lx + colW + colGap;

            float ly = SectionHeader(lx, y, colW, headH, "Игра");
            ly = GameRows(lx, ly, colW, ref changed);
            ly = SectionHeader(lx, ly, colW, headH, "Предпросмотр прицела");
            DrawPreview(new Rect(lx, ly, colW, previewH - 6f * k));

            float ry = SectionHeader(rx, y, colW, headH, "Прицел");
            CrosshairRows(rx, ry, colW, ref changed);
        }
        else
        {
            // the content is drawn in the coordinates of the scroll view
            Rect view = new Rect(p.x, y, pw, viewH);
            bool scroll = contentH > viewH + 0.5f;
            float innerW = pw - (scroll ? 20f : 0f);
            settingsScroll = GUI.BeginScrollView(view, settingsScroll, new Rect(0f, 0f, innerW, contentH));

            float cx = pad;
            float cw = innerW - pad * 2f;
            float cy = SectionHeader(cx, 0f, cw, headH, "Игра");
            cy = GameRows(cx, cy, cw, ref changed);
            cy += sectionGap;
            cy = SectionHeader(cx, cy, cw, headH, "Прицел");
            DrawPreview(new Rect(cx, cy, cw, previewH));
            cy += previewH + sectionGap;
            CrosshairRows(cx, cy, cw, ref changed);

            GUI.EndScrollView();
        }
        y += viewH;

        if (changed)
        {
            GameSettings.Apply();
            savedUntil = 0f;
        }

        y += 12f * k;
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
        y += (34f + 12f) * k;

        float gap = 16f * k;
        float bw = Mathf.Min((pw - pad * 2f - gap * 2f) / 3f, 420f * k);
        float bx = p.x + (pw - bw * 3f - gap * 2f) * 0.5f;

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

    // the title of a group of rows with a thin accent line under it; returns the y under it
    float SectionHeader(float x, float y, float w, float h, string title)
    {
        GUI.Label(new Rect(x, y, w, h - 6f * k), title, sectionStyle);
        Fill(new Rect(x, y + h - 6f * k, w, Mathf.Max(1f, 2f * k)), new Color(Accent.r, Accent.g, Accent.b, 0.5f));
        return y + h;
    }

    // sensitivity, sensitivity in the zoom, FOV, volume, radar (GameRowCount rows)
    const int GameRowCount = 5;
    const int CrosshairRowCount = 8;

    float GameRows(float x, float y, float w, ref bool changed)
    {
        SetColumn(x, w);

        RowBack(y, 0);
        float sens = SliderRow(y, "Чувствительность", GameSettings.Sensitivity,
            GameSettings.MinSensitivity, GameSettings.MaxSensitivity, 0.05f, 0.01f, "{0:0.00}");
        if (sens != GameSettings.Sensitivity) { GameSettings.Sensitivity = sens; changed = true; }
        y += rowH;

        // a multiplier on top of the sensitivity while a scope or an optic is up (1 = the same feel)
        RowBack(y, 1);
        float zoomSens = SliderRow(y, "Чувствительность в зуме", GameSettings.ZoomSensitivity,
            GameSettings.MinZoomSensitivity, GameSettings.MaxZoomSensitivity, 0.05f, 0.01f, "{0:0.00}×");
        if (zoomSens != GameSettings.ZoomSensitivity) { GameSettings.ZoomSensitivity = zoomSens; changed = true; }
        y += rowH;

        RowBack(y, 2);
        float fov = SliderRow(y, "Поле зрения (FOV)", GameSettings.Fov, GameSettings.MinFov, GameSettings.MaxFov, 1f, 1f, "{0:0}°");
        if (fov != GameSettings.Fov) { GameSettings.Fov = fov; changed = true; }
        y += rowH;

        RowBack(y, 3);
        float shownVolume = Mathf.Round(GameSettings.MasterVolume * 100f);
        float volume = SliderRow(y, "Общая громкость", shownVolume, 0f, 100f, 5f, 1f, "{0:0}%");
        if (volume != shownVolume) { GameSettings.MasterVolume = volume / 100f; changed = true; }
        y += rowH;

        RowBack(y, 4);
        bool radar = ToggleRow(y, "Радар", GameSettings.ShowRadar, "Включён", "Выключен");
        if (radar != GameSettings.ShowRadar) { GameSettings.ShowRadar = radar; changed = true; }
        y += rowH;

        return y;
    }

    // style, colour, size, gap, thickness, length, outline, dynamic
    float CrosshairRows(float x, float y, float w, ref bool changed)
    {
        SetColumn(x, w);

        RowBack(y, 0);
        int styles = GameSettings.CrosshairStyleCount;
        int step = CycleRow(y, "Стиль", GameSettings.CrosshairStyleName, false, Color.white);
        if (step != 0)
        {
            GameSettings.CrosshairStyle = (CrosshairStyle)(((int)GameSettings.CrosshairStyle + step + styles) % styles);
            changed = true;
        }
        y += rowH;

        RowBack(y, 1);
        int colors = GameSettings.CrosshairColors.Length;
        step = CycleRow(y, "Цвет", GameSettings.CrosshairColorName, true, GameSettings.CrosshairColorValue);
        if (step != 0)
        {
            GameSettings.CrosshairColor = (GameSettings.CrosshairColor + step + colors) % colors;
            changed = true;
        }
        y += rowH;

        RowBack(y, 2);
        float size = SliderRow(y, "Размер", GameSettings.CrosshairSize,
            GameSettings.MinCrosshairSize, GameSettings.MaxCrosshairSize, 0.1f, 0.05f, "{0:0.00}×");
        if (size != GameSettings.CrosshairSize) { GameSettings.CrosshairSize = size; changed = true; }
        y += rowH;

        RowBack(y, 3);
        float gap = SliderRow(y, "Зазор", GameSettings.CrosshairGap,
            GameSettings.MinCrosshairGap, GameSettings.MaxCrosshairGap, 1f, 0.5f, "{0:0.#}");
        if (gap != GameSettings.CrosshairGap) { GameSettings.CrosshairGap = gap; changed = true; }
        y += rowH;

        RowBack(y, 4);
        float thick = SliderRow(y, "Толщина", GameSettings.CrosshairThickness,
            GameSettings.MinCrosshairThickness, GameSettings.MaxCrosshairThickness, 0.5f, 0.5f, "{0:0.#}");
        if (thick != GameSettings.CrosshairThickness) { GameSettings.CrosshairThickness = thick; changed = true; }
        y += rowH;

        RowBack(y, 5);
        float length = SliderRow(y, "Длина", GameSettings.CrosshairLength,
            GameSettings.MinCrosshairLength, GameSettings.MaxCrosshairLength, 1f, 1f, "{0:0}");
        if (length != GameSettings.CrosshairLength) { GameSettings.CrosshairLength = length; changed = true; }
        y += rowH;

        RowBack(y, 6);
        bool outline = ToggleRow(y, "Обводка", GameSettings.CrosshairOutline, "Есть", "Нет");
        if (outline != GameSettings.CrosshairOutline) { GameSettings.CrosshairOutline = outline; changed = true; }
        y += rowH;

        RowBack(y, 7);
        bool dynamic = ToggleRow(y, "Динамический", GameSettings.CrosshairDynamic,
            "Да — зазор растёт", "Нет");
        if (dynamic != GameSettings.CrosshairDynamic) { GameSettings.CrosshairDynamic = dynamic; changed = true; }
        y += rowH;

        return y;
    }

    // the crosshair of the settings at its real size in the game, on a wall (dark and light half) over a floor;
    // the dynamic one breathes like while running and shooting
    void DrawPreview(Rect r)
    {
        Fill(r, new Color(0.13f, 0.14f, 0.16f, 1f));
        Fill(new Rect(r.x + r.width * 0.5f, r.y, r.width * 0.5f, r.height * 0.64f), new Color(0.46f, 0.47f, 0.48f, 1f));
        Fill(new Rect(r.x, r.y + r.height * 0.64f, r.width, r.height * 0.36f), new Color(0.6f, 0.52f, 0.38f, 1f));
        Frame(r, BorderColor);

        float scale = GameHud.Scale;
        float spread = 0f;
        if (GameSettings.CrosshairDynamic)
            spread = (0.5f - 0.5f * Mathf.Cos(Time.unscaledTime * 2.4f)) * 22f * scale;

        GUI.BeginGroup(r);
        GameHud.DrawCrosshairShape(new Vector2(r.width * 0.5f, r.height * 0.5f), scale, spread, GameSettings.CrosshairColorValue);
        GUI.EndGroup();
        GUI.color = Color.white;

        string caption = GameSettings.CrosshairStyleName + (GameSettings.CrosshairDynamic ? " · динамический" : "");
        GUI.Label(new Rect(r.x + 12f * k, r.yMax - 32f * k, r.width - 24f * k, 28f * k), caption, previewCaptionStyle);
    }

    // the label, control and value columns of the rows inside a column at x of width w
    void SetColumn(float x, float w)
    {
        rowColX = x;
        rowColW = w;
        rowLabelX = x;
        rowLabelW = w * 0.4f;
        rowValueW = 96f * k;
        rowValueX = x + w - rowValueW;
        rowCtrlX = rowLabelX + rowLabelW;
        rowCtrlRight = rowValueX - 12f * k;
    }

    // every second row gets a faint stripe
    void RowBack(float y, int index)
    {
        if (index % 2 == 0)
            Fill(new Rect(rowColX - 10f * k, y + 3f * k, rowColW + 20f * k, rowH - 6f * k), new Color(1f, 1f, 1f, 0.035f));
    }

    // a long label ("Чувствительность в зуме") gets a smaller font instead of running into the control
    void RowLabel(float y, string text)
    {
        tempContent.text = text;
        GUIStyle style = labelStyle.CalcSize(tempContent).x > rowLabelW - 8f * k ? labelSmallStyle : labelStyle;
        GUI.Label(new Rect(rowLabelX, y, rowLabelW, rowH), text, style);
    }

    float ButtonSize => Mathf.Round(Mathf.Min(44f * k, rowH - 12f * k));

    Rect LeftButtonRect(float y)
    {
        float s = ButtonSize;
        return new Rect(rowCtrlX, y + (rowH - s) * 0.5f, s, s);
    }

    Rect RightButtonRect(float y)
    {
        float s = ButtonSize;
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
        float sx = minus.xMax + 14f * k;
        Rect slider = new Rect(sx, y + (rowH - thumb) * 0.5f, Mathf.Max(thumb, plus.x - 14f * k - sx), thumb);

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

    // "◄", the current choice (a click = the next one), "►" over the control and value columns;
    // with a swatch a small square of that colour goes before the text. Returns -1, 0 or +1.
    int CycleRow(float y, string label, string text, bool swatch, Color swatchColor)
    {
        RowLabel(y, label);

        float s = ButtonSize;
        Rect prev = LeftButtonRect(y);
        Rect next = new Rect(rowValueX + rowValueW - s, prev.y, s, s);
        int step = 0;
        if (SmallButton(prev, "◄")) step = -1;
        if (SmallButton(next, "►")) step = 1;

        Rect mid = new Rect(prev.xMax + 10f * k, prev.y, next.x - 10f * k - (prev.xMax + 10f * k), prev.height);
        bool hover = mid.Contains(Event.current.mousePosition);
        Fill(mid, hover ? ButtonHover : ButtonColor);
        Frame(mid, BorderColor);

        float tx = mid.x + 14f * k;
        if (swatch)
        {
            float sw = 24f * k;
            Fill(new Rect(tx, mid.center.y - sw * 0.5f, sw, sw), swatchColor);
            tx += sw + 12f * k;
        }
        GUI.Label(new Rect(tx, mid.y, Mathf.Max(0f, mid.xMax - tx - 8f * k), mid.height), text, labelStyle);

        if (GUI.Button(mid, GUIContent.none, GUIStyle.none)) step = 1;
        return step;
    }

    // on/off over the whole control column
    bool ToggleRow(float y, string label, bool on, string onText, string offText)
    {
        RowLabel(y, label);

        float s = ButtonSize;
        Rect r = new Rect(rowCtrlX, y + (rowH - s) * 0.5f, rowValueX + rowValueW - rowCtrlX, s);
        bool hover = r.Contains(Event.current.mousePosition);
        Fill(r, hover ? ButtonHover : ButtonColor);
        Frame(r, BorderColor);

        float dot = 18f * k;
        Fill(new Rect(r.x + 16f * k, r.center.y - dot * 0.5f, dot, dot), on ? SavedColor : new Color(0.4f, 0.42f, 0.45f, 1f));
        GUI.Label(new Rect(r.x + 16f * k + dot + 14f * k, r.y, r.width - dot - 40f * k, r.height), on ? onText : offText, labelStyle);

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
        return SmallButton(r, text, true);
    }

    // a disabled one is dim and does nothing (but still eats the click, so the row under it does not get it)
    bool SmallButton(Rect r, string text, bool enabled)
    {
        bool hover = enabled && r.Contains(Event.current.mousePosition);
        Fill(r, !enabled ? ButtonOff : hover ? ButtonHover : ButtonColor);
        Frame(r, hover ? new Color(Accent.r, Accent.g, Accent.b, 0.6f) : BorderColor);
        GUI.color = enabled ? Color.white : new Color(1f, 1f, 1f, 0.3f);
        GUI.Label(r, text, smallButtonStyle);
        GUI.color = Color.white;
        return GUI.Button(r, GUIContent.none, GUIStyle.none) && enabled;
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
        labelSmallStyle = MakeLabel(21, FontStyle.Normal, TextAnchor.MiddleLeft, Color.white);
        valueStyle = MakeLabel(25, FontStyle.Bold, TextAnchor.MiddleRight, Color.white);
        optionValueStyle = MakeLabel(34, FontStyle.Bold, TextAnchor.MiddleCenter, Accent);
        infoStyle = MakeLabel(24, FontStyle.Normal, TextAnchor.MiddleCenter, DimText);
        footerStyle = MakeLabel(18, FontStyle.Normal, TextAnchor.LowerCenter, DimText);
        footerStyle.wordWrap = true;
        statusStyle = MakeLabel(21, FontStyle.Normal, TextAnchor.MiddleCenter, Accent);
        smallButtonStyle = MakeLabel(30, FontStyle.Bold, TextAnchor.MiddleCenter, Color.white);
        sectionStyle = MakeLabel(22, FontStyle.Bold, TextAnchor.LowerLeft, Accent);
        previewCaptionStyle = MakeLabel(18, FontStyle.Normal, TextAnchor.MiddleLeft, new Color(1f, 1f, 1f, 0.75f));

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
