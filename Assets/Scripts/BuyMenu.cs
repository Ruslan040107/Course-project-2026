using System.Collections.Generic;
using UnityEngine;

// CS:GO-like buy menu of the player and the buy notices (IMGUI).
// B opens it only when buying is allowed (Economy.CanBuy: alive, in the buy time, inside the own buy zone; the solo mode
// and the console command "buyanywhere" always allow it). Otherwise B shows a notice instead:
// "Период покупки в 20 сек. истёк" (then the menu can not be opened until the next round) or "Вы покинули зону покупки".
// An open menu closes by itself with the same notice when the buy time runs out or the player walks out of the zone.
// The notice is a dark bar with thin gold edges in the lower middle of the screen, about 3 s with a fade.
// Look like CS:GO: a round wheel of six sectors with the numbers 1-6 around the team emblem (drawn here: a gold disc with
// a star, T warm, CT steel): 1 ПИСТОЛЕТ, 2 ТЯЖЁЛОЕ, 3 ПП, 4 ВИНТОВКА, 5 СНАРЯЖЕНИЕ (a wheel of four), 6 ГРАНАТА.
// A category opens the wheel of its weapons in the CS:GO order (only the own side's weapons, see SectorSpecs).
// Weapons without a sector of their own are the alternative of another one (P2000 / USP-S, R8 / Desert Eagle,
// M4A1-S / M4A4; in the solo mode also the other side's weapons): RMB on such a sector swaps them (kept in PlayerPrefs).
// Around the wheel: the money (left), the time left to buy (top), the teammates with their money and weapons
// (bottom left), the stats of the weapon under the mouse (bottom right), a turning 3D view of that weapon (its model
// rendered by a private camera far above the map; the kill icon if there is no model), the own weapons (right edge).
// Armour, grenades, the defuse kit and the shield are not in the game yet: grey sectors.
// Keys: the mouse picks a sector by its direction, the wheel moves the selection, LMB / Enter buys or opens a category,
// 1-6 choose a sector at once. After a buy the menu goes back to the categories (like CS:GO).
// RMB (not on an alternative), Backspace, Escape or a click on the emblem go back; Escape on the categories or B closes.
// While it is open the cursor is free: no shooting, no weapon switching, no camera turning; moving still works.
// MainMenu reads IsOpen, so the Escape that closes this menu does not open the pause. Nothing here reacts to the
// keyboard while the developer console is open. RoundManager adds it in normal games (never in simulations).
[DefaultExecutionOrder(100)]
public class BuyMenu : MonoBehaviour
{
    // the menu is on the screen; also true in the frame it was closed, so the same Escape does not open the pause
    public static bool IsOpen => open || Time.frameCount == closedFrame;

    static bool open;
    static int closedFrame = -10;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        open = false;
        closedFrame = -10;
    }

    public KeyCode key = KeyCode.B;
    [Tooltip("seconds a notice ('Вы покинули зону покупки' ...) stays on the screen")] public float noticeTime = 3f;
    [Tooltip("the weapon under the mouse turns as a 3D model (off: its kill icon)")] public bool modelPreview = true;

    RoundManager rounds;
    Health player;
    PlayerLoadout loadout;

    // ---------------- what the wheels hold ----------------

    static readonly string[] CategoryTitles = { "ПИСТОЛЕТ", "ТЯЖЁЛОЕ", "ПП", "ВИНТОВКА", "СНАРЯЖЕНИЕ", "ГРАНАТА" };
    const int PistolCategory = 0, HeavyCategory = 1, SmgCategory = 2, RifleCategory = 3, GearCategory = 4;

    // Sector 1 is the top left one (top for the wheel of four), then clockwise, like the CS:GO screenshots.
    // "A|B" = alternatives (the own side's first, RMB swaps), "!key" = an item that is not in the game yet, "" = empty.
    // The team filter keeps only what the player's side can buy (T: Glock-18, CT: USP-S / P2000 ...).
    static readonly string[][] SectorSpecs =
    {
        new[] { "Glock-18|USP-S|P2000", "Dual Berettas", "P250", "Tec-9|Five-SeveN", "Desert Eagle|R8 Revolver", "CZ75-Auto" },
        new[] { "Nova", "XM1014", "Sawed-Off|MAG-7", "M249", "Negev", "!shield" },
        new[] { "MAC-10|MP9", "MP7", "UMP-45", "P90", "PP-Bizon", "MP5-SD" },
        new[] { "Galil AR|FAMAS", "AK-47|M4A4|M4A1-S", "SSG 08", "SG 553|AUG", "AWP", "G3SG1|SCAR-20" },
        new[] { "!vest", "!vesthelmet", "Zeus x27", "!defuser" },
        new[] { "!molotov|!incendiary", "!decoy", "!flash", "!he", "!smoke", "" },
    };

    // the drawn icons of the items that are not in the game yet
    const int ShapeVest = 0, ShapeVestHelmet = 1, ShapeDefuser = 2, ShapeShield = 3, ShapeMolotov = 4,
              ShapeIncendiary = 5, ShapeDecoy = 6, ShapeFlash = 7, ShapeHe = 8, ShapeSmoke = 9;

    struct SoonItem
    {
        public string key, title, about, tag;
        public int price, team, shape;
        public SoonItem(string key, string title, int price, int team, int shape, string tag, string about)
        {
            this.key = key;
            this.title = title;
            this.price = price;
            this.team = team;
            this.shape = shape;
            this.tag = tag;
            this.about = about;
        }
    }

    const string Soon = "скоро";

    static readonly SoonItem[] SoonItems =
    {
        new SoonItem("vest", "Бронежилет", 650, -1, ShapeVest, Soon,
            "Защищает тело и снижает урон от пуль. Броня появится в следующих версиях игры."),
        new SoonItem("vesthelmet", "Бронежилет + шлем", 1000, -1, ShapeVestHelmet, Soon,
            "Защищает тело и голову: выстрел в голову уже не так опасен. Появится вместе с бронёй."),
        new SoonItem("defuser", "Набор сапёра", 400, 1, ShapeDefuser, Soon,
            "Позволяет обезвредить бомбу вдвое быстрее. Появится вместе с режимом закладки бомбы."),
        new SoonItem("shield", "Полицейский щит", 1100, 0, ShapeShield, "недоступно",
            "Закрывает от пуль спереди, но сильно замедляет. В этом режиме игры недоступен."),
        new SoonItem("molotov", "Коктейль Молотова", 400, 0, ShapeMolotov, Soon,
            "Разливает горящую смесь: огонь на несколько секунд перекрывает проход. Гранаты появятся позже."),
        new SoonItem("incendiary", "Зажигательная граната", 500, 1, ShapeIncendiary, Soon,
            "Поджигает участок земли, через огонь не пройти без потерь. Гранаты появятся позже."),
        new SoonItem("decoy", "Ложная граната", 50, -1, ShapeDecoy, Soon,
            "Шумит как стрельба и сбивает противника с толку. Гранаты появятся позже."),
        new SoonItem("flash", "Световая граната", 200, -1, ShapeFlash, Soon,
            "Ослепляет всех, кто смотрит в сторону вспышки. Гранаты появятся позже."),
        new SoonItem("he", "Осколочная граната", 300, -1, ShapeHe, Soon,
            "Взрыв ранит всех рядом, броня снижает урон. Гранаты появятся позже."),
        new SoonItem("smoke", "Дымовая граната", 300, -1, ShapeSmoke, Soon,
            "Ставит дымовую завесу, через которую ничего не видно. Гранаты появятся позже."),
    };

    // one thing a sector can offer
    class Entry
    {
        public string title;
        public int price;
        public int team = -1;          // -1 both sides, 0 T, 1 CT
        public WeaponData weapon;      // null: not in the game yet (grey)
        public int shape = -1;         // the drawn icon of an item without a weapon
        public string tag = "";        // "скоро" / "недоступно" for an item without a weapon
        public string about = "";      // its description for the stats panel
        public Texture2D icon;
    }

    class Sector
    {
        public readonly List<Entry> options = new List<Entry>();
        public int choice;
        public string prefKey = "";
        // cached texts of the alternative ("ПКМ — M4A1-S")
        public int hintChoice = -1;
        public string hint = "", swapHint = "";

        public bool Empty => options.Count == 0;
        public Entry Current => options.Count == 0 ? null : options[Mathf.Clamp(choice, 0, options.Count - 1)];
        public Entry Next => options.Count < 2 ? null : options[(Mathf.Clamp(choice, 0, options.Count - 1) + 1) % options.Count];
    }

    // the sectors of every category, per (ring team, player team)
    readonly Dictionary<int, Sector[][]> wheels = new Dictionary<int, Sector[][]>();

    // ---------------- state ----------------

    int level;                 // 0 = the categories, 1 = the sectors of 'category'
    int category;
    int selected = -1;         // the highlighted sector, -1 = none
    bool wheelMode;            // the wheel / keys moved the selection; the mouse takes over when it moves
    bool centreHover;          // the mouse is on the emblem
    Vector2 lastMouse;
    float openedAt, levelAt;
    int consoleFrame = -10;

    string notice = "";
    float noticeAt = -100f;

    // ---------------- look ----------------

    static readonly Color PriceGreen = new Color(0.45f, 0.78f, 0.42f, 1f);
    static readonly Color PriceRed = new Color(0.86f, 0.2f, 0.17f, 1f);
    static readonly Color Gold = new Color(0.88f, 0.68f, 0.3f, 1f);
    static readonly Color Grey = new Color(0.52f, 0.52f, 0.52f, 1f);
    static readonly Color BarColor = new Color(0.17f, 0.5f, 0.5f, 0.88f);
    static readonly Color PreviewBack = new Color(0.045f, 0.05f, 0.055f, 0f);
    static readonly string[] Digits = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    const string HintCategories = "[1–6] РАЗДЕЛ     [ЛКМ] ОТКРЫТЬ     [КОЛЕСО] ВЫБОР     [B] ЗАКРЫТЬ";
    const string HintItems = "[ЛКМ] КУПИТЬ     [ПКМ] НАЗАД     [КОЛЕСО] ВЫБОР     [B] ЗАКРЫТЬ";

    // the layout is made for 1920x1080 and scaled (k) into the middle of the screen
    float k = 1f, ox, oy;
    Vector2 centre;
    float wheelR = 300f, rimW = 7f, emblemR = 46f;

    float stylesFor = -1f;
    GUIStyle topStyle, moneyStyle, freeStyle, catStyle, numStyle, nameStyle, nameSmallStyle, priceStyle, smallStyle,
             headStyle, statStyle, aboutStyle, footStyle, footHintStyle, teamStyle, teamMoneyStyle, noticeStyle;
    readonly GUIContent measure = new GUIContent();

    // textures made for the current size (wheel, hover sector, emblems) and the size-free ones
    readonly Dictionary<int, Texture2D> wheelTextures = new Dictionary<int, Texture2D>();
    readonly Dictionary<int, Texture2D> hoverTextures = new Dictionary<int, Texture2D>();
    readonly Dictionary<int, float> hoverTops = new Dictionary<int, float>();
    readonly Texture2D[] emblems = new Texture2D[2];
    readonly Dictionary<int, Texture2D> shapeIcons = new Dictionary<int, Texture2D>();
    readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
    Texture2D spot, fadeRight, fadeDown;
    float texturesFor = -1f;

    // cached texts
    readonly Dictionary<int, string> moneyTexts = new Dictionary<int, string>();
    int secondsShown = -1;
    string timeText = "";
    string noticeMeasured;
    float noticeWidth, noticeWidthFor = -1f;

    class Stats
    {
        public string ammo, reward, damage, rate, control, range, armor, about;
        public float damageBar, rateBar, controlBar, rangeBar, armorBar;
    }

    readonly Dictionary<WeaponData, Stats> stats = new Dictionary<WeaponData, Stats>();

    // teammates for the bottom left panel
    readonly List<Health> mates = new List<Health>();
    readonly Dictionary<Health, BotBuyer> buyers = new Dictionary<Health, BotBuyer>();
    readonly Dictionary<Health, string> names = new Dictionary<Health, string>();

    // ---------------- 3D view of the weapon ----------------

    const int PreviewLayer = 30;
    const float PreviewFov = 20f;
    const float PreviewTilt = 9f;
    // far above the map: the main camera (far plane 1000) never sees it, nothing shadows it
    static readonly Vector3 PreviewPosition = new Vector3(0f, 3000f, 0f);
    // the barrel points to the right of the preview camera
    static readonly Quaternion PreviewTurn = Quaternion.Euler(0f, 90f, 0f);

    class PreviewModel
    {
        public GameObject holder;
        public Vector3 size;
    }

    GameObject previewRoot;
    Transform previewPivot;
    Camera previewCam;
    RenderTexture previewRT;
    bool previewBroken, previewReady;
    WeaponData previewFor;
    PreviewModel previewModel;
    float previewSince;
    readonly Dictionary<WeaponData, PreviewModel> previewModels = new Dictionary<WeaponData, PreviewModel>();

    // ---------------- life ----------------

    void Awake()
    {
        rounds = GetComponent<RoundManager>();
        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
    }

    // the wheels and the emblems are made now, so the first B press has no hitch
    void Start()
    {
        Layout();
        CheckTextures();
        WheelTexture(6);
        WheelTexture(4);
        HoverTexture(6, out _);
        HoverTexture(4, out _);
        Emblem(0);
        Emblem(1);
    }

    void OnDisable()
    {
        if (open) Close(false);
        HidePreview();
    }

    void OnDestroy()
    {
        DestroySizedTextures();
        foreach (Texture2D t in shapeIcons.Values) if (t != null) Destroy(t);
        shapeIcons.Clear();
        if (spot != null) Destroy(spot);
        if (fadeRight != null) Destroy(fadeRight);
        if (fadeDown != null) Destroy(fadeDown);
        DestroyPreview();
    }

    bool FindPlayer()
    {
        if (player == null && rounds != null) player = rounds.player;
        if (player != null && loadout == null) loadout = player.GetComponent<PlayerLoadout>();
        return player != null && loadout != null;
    }

    bool Free => !Economy.Enabled;

    int Team => player != null ? player.team : 0;

    // the side whose weapons the wheels show: -1 = all of them (the solo mode, everything is free there)
    int RingTeam => Free ? -1 : Team;

    bool PlayerAlive()
    {
        if (rounds != null && rounds.simulation) return false;
        return FindPlayer() && !player.IsDead && player.gameObject.activeInHierarchy;
    }

    // buying is allowed now: Economy.CanBuy (alive, buy time, own zone; solo / buyanywhere: always) in a running round
    bool CanBuyNow()
    {
        if (!PlayerAlive() || !Economy.CanBuy(player)) return false;
        return Free || Economy.BuyAnywhere || rounds == null || (!rounds.InBreak && !rounds.MatchOver);
    }

    // the notice when buying is not allowed: the buy time is over first, then the zone
    string BlockedNotice()
    {
        if (!Economy.InBuyTime || (rounds != null && (rounds.InBreak || rounds.MatchOver)))
            return "Период покупки в " + Mathf.RoundToInt(Economy.BuyTimeLength) + " сек. истёк";
        if (player != null && !Economy.InBuyZone(player)) return "Вы покинули зону покупки";
        return "Сейчас покупать нельзя";
    }

    void Notice(string text)
    {
        notice = text ?? "";
        noticeAt = Time.unscaledTime;
    }

    // ---------------- input ----------------

    void Update()
    {
        // the console owns the keyboard; it may close in this frame before this Update, so the last frame counts too
        if (DevConsole.IsOpen) consoleFrame = Time.frameCount;
        bool console = DevConsole.IsOpen || consoleFrame >= Time.frameCount - 1;

        if (open)
        {
            if (!PlayerAlive() || MainMenu.IsOpen)
            {
                // the pause menu / the console own the cursor then; otherwise the game takes it back
                Close(!MainMenu.IsOpen && !DevConsole.IsOpen);
                return;
            }
            if (!CanBuyNow())
            {
                // walked out of the zone or the buy time is over: the menu closes and says why
                string why = BlockedNotice();
                Close(!DevConsole.IsOpen);
                Notice(why);
                return;
            }

            Layout();
            if (console) return;

            if (Input.GetKeyDown(key))
            {
                Close(true);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (level == 1) Back();
                else Close(true);
                return;
            }
            HandleInput();
            return;
        }

        if (console || !Input.GetKeyDown(key) || Time.frameCount == closedFrame) return;
        if (MainMenu.IsOpen || Time.timeScale <= 0f || !PlayerAlive()) return;

        if (CanBuyNow()) Open();
        else Notice(BlockedNotice());
    }

    void LateUpdate()
    {
        // nothing may lock the cursor under the menu (for example MainMenu takes it back on a click)
        if (open && Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        UpdatePreview();
    }

    void Open()
    {
        open = true;
        level = 0;
        category = 0;
        selected = -1;
        wheelMode = false;
        centreHover = false;
        notice = "";
        openedAt = Time.unscaledTime;
        levelAt = openedAt;
        // the bots may have been renamed (halftime) or added since the last time
        names.Clear();
        buyers.Clear();
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        Layout();
        lastMouse = MouseGui();
    }

    void Close(bool lockCursor)
    {
        open = false;
        closedFrame = Time.frameCount;
        GUIUtility.keyboardControl = 0;
        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            // the mouse jump of locking the cursor and the click that closed the menu are not look or shots
            MainMenu.LastClosedFrame = Time.frameCount;
        }
        HidePreview();
    }

    static Vector2 MouseGui()
    {
        Vector3 m = Input.mousePosition;
        return new Vector2(m.x, Screen.height - m.y);
    }

    void HandleInput()
    {
        int n = RingCount;
        Vector2 mouse = MouseGui();
        if ((mouse - lastMouse).sqrMagnitude > 4f) wheelMode = false;   // the mouse moved: it picks again
        lastMouse = mouse;

        Vector2 d = mouse - centre;
        centreHover = d.sqrMagnitude < emblemR * emblemR;
        if (!wheelMode) selected = MouseSector(d);

        // the wheel goes round the ring (down = clockwise), over the empty sectors
        float scroll = Input.mouseScrollDelta.y;
        if (scroll != 0f && n > 0)
        {
            int step = scroll < 0f ? 1 : -1;
            int s = selected;
            for (int tries = 0; tries < n; tries++)
            {
                s = s < 0 ? (step > 0 ? 0 : n - 1) : ((s + step) % n + n) % n;
                if (Usable(s)) break;
            }
            if (Usable(s))
            {
                selected = s;
                wheelMode = true;
            }
        }

        // 1-9: a sector at once (a category opens, a weapon is bought)
        for (int i = 0; i < 9; i++)
        {
            if (!Input.GetKeyDown(KeyCode.Alpha1 + i) && !Input.GetKeyDown(KeyCode.Keypad1 + i)) continue;
            if (i < n && Usable(i))
            {
                selected = i;
                wheelMode = true;
                Activate(i);
            }
            return;
        }

        // RMB: swaps an alternative, otherwise back
        if (Input.GetMouseButtonDown(1))
        {
            if (level == 1 && !centreHover && selected >= 0 && Swappable(selected)) Swap(selected);
            else Back();
            return;
        }
        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0))
        {
            Back();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (selected >= 0) Activate(selected);
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (centreHover && !wheelMode) Back();
            else if (selected >= 0) Activate(selected);
        }
    }

    // the sector the mouse points at: by its direction, not on the emblem and not far away from the wheel
    int MouseSector(Vector2 d)
    {
        if (centreHover) return -1;
        float far = wheelR * 1.2f;
        if (d.sqrMagnitude > far * far) return -1;
        int s = SectorAt(d, RingCount);
        return Usable(s) ? s : -1;
    }

    int RingCount => level == 0 ? CategoryTitles.Length : CurrentSectors().Length;

    bool Usable(int i)
    {
        if (i < 0 || i >= RingCount) return false;
        return level == 0 || !CurrentSectors()[i].Empty;
    }

    bool Swappable(int i)
    {
        return level == 1 && i >= 0 && i < RingCount && CurrentSectors()[i].options.Count > 1;
    }

    // the next alternative of the sector; the choice is remembered for this side
    void Swap(int i)
    {
        Sector s = CurrentSectors()[i];
        if (s.options.Count < 2) return;
        s.choice = (s.choice + 1) % s.options.Count;
        try
        {
            PlayerPrefs.SetString(s.prefKey, s.Current.title);
            PlayerPrefs.Save();
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("BuyMenu: the choice is not saved: " + e.Message);
        }
    }

    void Activate(int i)
    {
        if (level == 0)
        {
            OpenCategory(i);
            return;
        }
        if (!Usable(i)) return;
        Entry e = CurrentSectors()[i].Current;
        if (e == null) return;
        if (e.weapon == null)
        {
            Notice(e.title + (e.tag == Soon ? ": скоро в игре" : ": недоступно"));
            return;
        }
        // like CS:GO: after a buy the categories come back
        if (Buy(e.weapon)) Back();
    }

    void OpenCategory(int c)
    {
        level = 1;
        category = Mathf.Clamp(c, 0, CategoryTitles.Length - 1);
        levelAt = Time.unscaledTime;
        // keys and the wheel start on the first sector; the mouse picks by itself
        selected = wheelMode ? FirstUsable() : MouseSector(lastMouse - centre);
    }

    void Back()
    {
        if (level == 0) return;
        int c = category;
        level = 0;
        levelAt = Time.unscaledTime;
        selected = wheelMode ? c : MouseSector(lastMouse - centre);
    }

    int FirstUsable()
    {
        for (int i = 0; i < RingCount; i++)
            if (Usable(i)) return i;
        return -1;
    }

    // ---------------- geometry of a wheel ----------------

    // the direction of sector 0: the wheel of six starts top left (sector 1 in CS:GO), the wheel of four at the top
    static float FirstAngle(int n)
    {
        return n == 4 ? 0f : -180f / Mathf.Max(1, n);
    }

    // the direction of sector i, degrees clockwise from up
    static float AngleOf(int i, int n)
    {
        return FirstAngle(n) + i * 360f / Mathf.Max(1, n);
    }

    // the sector in the direction of d (screen pixels from the centre, y down)
    static int SectorAt(Vector2 d, int n)
    {
        if (n <= 0 || d.sqrMagnitude < 1f) return -1;
        float a = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
        float w = 360f / n;
        return Mathf.FloorToInt(Mathf.Repeat(a - FirstAngle(n) + w * 0.5f, 360f) / w) % n;
    }

    // a screen direction (y down) for an angle clockwise from up
    static Vector2 Dir(float degrees)
    {
        float r = degrees * Mathf.Deg2Rad;
        return new Vector2(Mathf.Sin(r), -Mathf.Cos(r));
    }

    // ---------------- the sectors ----------------

    Sector[][] AllSectors()
    {
        int ringTeam = RingTeam;
        int playerTeam = Team == 1 ? 1 : 0;
        int id = (ringTeam + 1) * 10 + playerTeam;
        if (!wheels.TryGetValue(id, out Sector[][] all))
        {
            all = BuildSectors(ringTeam, playerTeam);
            wheels[id] = all;
        }
        return all;
    }

    Sector[] CurrentSectors()
    {
        return AllSectors()[Mathf.Clamp(category, 0, CategoryTitles.Length - 1)];
    }

    Sector[][] BuildSectors(int ringTeam, int playerTeam)
    {
        Sector[][] all = new Sector[SectorSpecs.Length][];
        for (int c = 0; c < SectorSpecs.Length; c++)
        {
            string[] specs = SectorSpecs[c];
            all[c] = new Sector[specs.Length];
            for (int i = 0; i < specs.Length; i++) all[c][i] = MakeSector(specs[i], ringTeam, playerTeam, c, i);
        }

        // every weapon of the side must be buyable: one without a place (a new asset) takes the first empty sector
        // of its category, or becomes one more alternative of the category's last sector
        foreach (WeaponData d in Economy.Weapons)
        {
            if (d == null || !Economy.Buyable(d)) continue;
            if (ringTeam >= 0 && !Economy.TeamCanBuy(d, ringTeam)) continue;
            if (Placed(all, d)) continue;
            Sector[] ring = all[CategoryOf(d)];
            Sector target = null;
            foreach (Sector s in ring)
            {
                if (!s.Empty) continue;
                target = s;
                break;
            }
            if (target == null) target = ring[ring.Length - 1];
            Entry e = WeaponEntry(d);
            if (e != null) target.options.Add(e);
        }
        return all;
    }

    static bool Placed(Sector[][] all, WeaponData d)
    {
        foreach (Sector[] ring in all)
            foreach (Sector s in ring)
                foreach (Entry e in s.options)
                    if (e.weapon == d) return true;
        return false;
    }

    static int CategoryOf(WeaponData d)
    {
        switch (d.category)
        {
            case WeaponCategory.Pistol: return PistolCategory;
            case WeaponCategory.Shotgun:
            case WeaponCategory.MachineGun: return HeavyCategory;
            case WeaponCategory.SMG: return SmgCategory;
            case WeaponCategory.Gear: return GearCategory;
            default: return RifleCategory;
        }
    }

    Sector MakeSector(string spec, int ringTeam, int playerTeam, int c, int index)
    {
        Sector s = new Sector();
        s.prefKey = "BuyMenu.Choice." + ringTeam + "." + c + "." + index;
        if (string.IsNullOrEmpty(spec)) return s;

        string[] parts = spec.Split('|');
        // in the solo mode (ringTeam -1) the items of the player's own side come first
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (string part in parts)
            {
                Entry e = part.StartsWith("!") ? SoonEntry(part.Substring(1)) : WeaponEntry(Economy.Find(part));
                if (e == null) continue;
                if (ringTeam >= 0 && e.team >= 0 && e.team != ringTeam) continue;
                bool own = e.team < 0 || e.team == playerTeam;
                if (own != (pass == 0)) continue;
                s.options.Add(e);
            }
        }

        string saved = PlayerPrefs.GetString(s.prefKey, "");
        for (int i = 0; i < s.options.Count; i++)
            if (s.options[i].title == saved) s.choice = i;
        return s;
    }

    static Entry WeaponEntry(WeaponData d)
    {
        if (d == null || !Economy.Buyable(d)) return null;
        return new Entry { title = d.weaponName, price = d.price, team = d.team, weapon = d };
    }

    static Entry SoonEntry(string key)
    {
        foreach (SoonItem s in SoonItems)
            if (s.key == key)
                return new Entry { title = s.title, price = s.price, team = s.team, shape = s.shape, tag = s.tag, about = s.about };
        return null;
    }

    // the entry under the selection in a category, null on the categories
    Entry Hovered()
    {
        if (level != 1 || !Usable(selected)) return null;
        return CurrentSectors()[selected].Current;
    }

    // something in the category the player can pay for now
    bool CategoryAffordable(int c)
    {
        if (Free) return true;
        int money = Economy.MoneyOf(player);
        foreach (Sector s in AllSectors()[c])
            foreach (Entry e in s.options)
                if (e.weapon != null && e.price <= money) return true;
        return false;
    }

    void UpdateHints(Sector s)
    {
        if (s.hintChoice == s.choice) return;
        s.hintChoice = s.choice;
        Entry next = s.Next;
        s.hint = next != null ? "ПКМ — " + next.title : "";
        s.swapHint = next != null ? "[ПКМ] ЗАМЕНИТЬ НА " + next.title.ToUpperInvariant() + "     [ЛКМ] КУПИТЬ     [B] ЗАКРЫТЬ" : HintItems;
    }

    // ---------------- buying ----------------

    bool Buy(WeaponData d)
    {
        if (d == null || !Economy.Buyable(d) || !FindPlayer()) return false;

        if (!CanBuyNow())
        {
            Notice(BlockedNotice());
            return false;
        }
        if (loadout.Owns(d))
        {
            Notice("У вас уже есть " + d.weaponName);
            return false;
        }
        if (!Free && !Economy.TeamCanBuy(d, Team)) return false;   // never in the wheel: only the own side's weapons are there

        int money = Economy.MoneyOf(player);
        if (!Free && money < d.price)
        {
            Notice("Недостаточно денег: не хватает $" + (d.price - money));
            return false;
        }
        if (!Economy.TrySpend(player, d.price, Economy.ReasonBuy)) return false;

        if (!loadout.Give(d))
        {
            // the loadout could not take it: the money comes back
            if (!Free) Economy.Set(player, Economy.MoneyOf(player) + d.price);
            return false;
        }
        return true;
    }

    // ---------------- drawing ----------------

    void Layout()
    {
        k = Mathf.Max(0.3f, Mathf.Min(Screen.height / 1080f, Screen.width / 1920f));
        ox = Mathf.Round((Screen.width - 1920f * k) * 0.5f);
        oy = Mathf.Round((Screen.height - 1080f * k) * 0.5f);
        wheelR = Mathf.Round(300f * k);
        rimW = Mathf.Max(2f, Mathf.Round(7f * k));
        emblemR = Mathf.Round(46f * k);
        centre = new Vector2(Mathf.Round(X(605f)), Mathf.Round(Y(375f)));
    }

    // 1080p layout -> screen pixels
    float X(float v) => ox + v * k;
    float Y(float v) => oy + v * k;
    float S(float v) => v * k;

    Rect V(float x, float y, float w, float h)
    {
        return new Rect(Mathf.Round(X(x)), Mathf.Round(Y(y)), Mathf.Round(S(w)), Mathf.Round(S(h)));
    }

    void OnGUI()
    {
        GUI.depth = -60;   // above the HUD, the radar and the scoreboard, below the pause menu
        if (Event.current.type != EventType.Repaint) return;   // the input is read in Update

        bool menu = open && player != null && loadout != null;
        bool showNotice = notice.Length > 0 && Time.unscaledTime - noticeAt < noticeTime && !MainMenu.IsOpen;
        if (!menu && !showNotice) return;

        Layout();
        BuildStyles();
        if (menu) DrawMenu();
        if (showNotice) DrawNotice(menu);
        GUI.color = Color.white;
    }

    void DrawMenu()
    {
        CheckTextures();
        float now = Time.unscaledTime;
        float appear = Mathf.Clamp01((now - openedAt) / 0.12f);
        float ease = appear * appear * (3f - 2f * appear);
        float levelFade = Mathf.Clamp01((now - levelAt) / 0.14f);

        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.62f * appear));

        Entry hovered = Hovered();
        DrawPreview(hovered, appear);
        DrawTopLine(appear);
        DrawMoney(appear);

        // the wheel grows a little when it opens
        Matrix4x4 baseMatrix = GUI.matrix;
        float s = Mathf.Lerp(0.94f, 1f, ease);
        GUIUtility.ScaleAroundPivot(new Vector2(s, s), centre);
        DrawWheel(appear, appear * Mathf.Lerp(0.25f, 1f, levelFade));
        GUI.matrix = baseMatrix;

        DrawTeam(appear);
        if (hovered != null) DrawStats(hovered, appear);
        DrawOwn(appear);
        DrawFooter(appear);
    }

    // "Оставшееся для покупки время: 00:15" on a soft dark band at the top
    void DrawTopLine(float a)
    {
        float h = Mathf.Round(S(70f));
        Draw(new Rect(0f, 0f, Screen.width, h), FadeDown(), new Color(0f, 0f, 0f, 0.55f * a));
        Text(new Rect(0f, Mathf.Round(S(10f)), Screen.width, Mathf.Round(S(44f))), TopText(), topStyle, Fade(Color.white, a), TextAnchor.MiddleCenter);
    }

    string TopText()
    {
        if (Free) return "Тренировка: всё оружие бесплатно";
        if (!Economy.InBuyTime) return "Закупка без ограничений (buyanywhere)";
        int s = Mathf.CeilToInt(Economy.BuyTimeLeft);
        if (s != secondsShown)
        {
            secondsShown = s;
            timeText = "Оставшееся для покупки время: " + (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }
        return timeText;
    }

    // the money, big on the left
    void DrawMoney(float a)
    {
        Rect box = V(18f, 346f, 200f, 58f);
        Draw(box, FadeRight(), new Color(0f, 0f, 0f, 0.6f * a));
        string text = Free ? "Бесплатно" : MoneyText(Economy.MoneyOf(player));
        Text(new Rect(Mathf.Round(X(32f)), box.y, Mathf.Round(S(400f)), box.height), text, Free ? freeStyle : moneyStyle,
            Fade(Color.white, a), TextAnchor.MiddleLeft);
    }

    void DrawWheel(float alpha, float contentAlpha)
    {
        int n = RingCount;

        float sr = wheelR + Mathf.Round(S(80f));
        Draw(new Rect(centre.x - sr, centre.y - sr, sr * 2f, sr * 2f), Spot(), new Color(0f, 0f, 0f, 0.55f * alpha));

        Texture2D wheel = WheelTexture(n);
        float ws = wheel.width;
        Draw(new Rect(centre.x - ws * 0.5f, centre.y - ws * 0.5f, ws, ws), wheel, new Color(1f, 1f, 1f, alpha));

        // the sector under the mouse is a little lighter
        if (Usable(selected))
        {
            Texture2D hover = HoverTexture(n, out float top);
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(AngleOf(selected, n), centre);
            Draw(new Rect(centre.x - hover.width * 0.5f, centre.y - top, hover.width, hover.height), hover,
                new Color(1f, 1f, 1f, 0.09f * alpha));
            GUI.matrix = m;
        }

        for (int i = 0; i < n; i++)
        {
            if (level == 0) DrawCategory(i, n, contentAlpha);
            else DrawItem(i, n, contentAlpha);
        }

        // the numbers around the emblem (none at an empty sector)
        float box = Mathf.Round(S(30f));
        for (int i = 0; i < n && i < Digits.Length; i++)
        {
            if (!Usable(i)) continue;
            Vector2 p = centre + Dir(AngleOf(i, n)) * S(69f);
            Color c = i == selected ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            Text(new Rect(Mathf.Round(p.x - box * 0.5f), Mathf.Round(p.y - box * 0.5f), box, box), Digits[i], numStyle,
                Fade(c, alpha), TextAnchor.MiddleCenter);
        }

        // the team emblem in the middle (a click on it goes back)
        Texture2D em = Emblem(Team);
        float es = Mathf.Round(em.width * (level == 1 && centreHover && !wheelMode ? 1.08f : 1f));
        Draw(new Rect(Mathf.Round(centre.x - es * 0.5f), Mathf.Round(centre.y - es * 0.5f), es, es), em, new Color(1f, 1f, 1f, alpha));
    }

    // a category name: white when something in it is affordable, grey when not
    void DrawCategory(int i, int n, float a)
    {
        Vector2 p = centre + Dir(AngleOf(i, n)) * S(150f);
        Color c = i == selected ? Color.white : CategoryAffordable(i) ? new Color(0.9f, 0.9f, 0.9f, 1f) : Grey;
        float w = Mathf.Round(S(240f)), h = Mathf.Round(S(34f));
        Text(new Rect(Mathf.Round(p.x - w * 0.5f), Mathf.Round(p.y - h * 0.5f), w, h), CategoryTitles[i], catStyle,
            Fade(c, a), TextAnchor.MiddleCenter);
    }

    // a weapon (or an item that is not in the game yet): the name, the silhouette, the price, the alternative
    void DrawItem(int i, int n, float a)
    {
        Sector s = CurrentSectors()[i];
        Entry e = s.Current;
        if (e == null) return;

        Vector2 p = centre + Dir(AngleOf(i, n)) * S(n == 4 ? 186f : 188f);
        bool hover = i == selected;
        bool soon = e.weapon == null;
        bool owned = !soon && loadout.Owns(e.weapon);
        bool expensive = !soon && !Free && Economy.MoneyOf(player) < e.price;
        float w = Mathf.Round(S(220f));
        float x = Mathf.Round(p.x - w * 0.5f);

        Color nameColor = soon ? Grey : hover ? Color.white : new Color(0.84f, 0.84f, 0.84f, 1f);
        Text(new Rect(x, Mathf.Round(p.y - S(62f)), w, Mathf.Round(S(24f))), e.title, Fit(e.title, nameStyle, nameSmallStyle, S(205f)),
            Fade(nameColor, a), TextAnchor.MiddleCenter);

        Color iconColor = soon ? new Color(0.42f, 0.42f, 0.42f, 1f)
            : expensive && !owned ? new Color(0.45f, 0.45f, 0.45f, 1f)
            : hover ? Color.white : new Color(0.9f, 0.9f, 0.9f, 1f);
        DrawIcon(EntryIcon(e), new Vector2(p.x, p.y - S(4f)), S(155f), S(soon ? 44f : 48f), Fade(iconColor, a));

        string price = owned ? "ЕСТЬ" : MoneyText(e.price);
        Color priceColor = soon ? Grey : owned ? Gold : expensive ? PriceRed : PriceGreen;
        Text(new Rect(x, Mathf.Round(p.y + S(30f)), w, Mathf.Round(S(28f))), price, priceStyle, Fade(priceColor, a), TextAnchor.MiddleCenter);

        Rect line = new Rect(x, Mathf.Round(p.y + S(57f)), w, Mathf.Round(S(20f)));
        if (soon)
        {
            Text(line, e.tag, smallStyle, Fade(new Color(Gold.r, Gold.g, Gold.b, 0.6f), a), TextAnchor.MiddleCenter);
        }
        else if (s.options.Count > 1)
        {
            UpdateHints(s);
            Text(line, s.hint, smallStyle, Fade(new Color(0.68f, 0.68f, 0.68f, 1f), a), TextAnchor.MiddleCenter);
        }
    }

    // the teammates: name, weapons, money and the emblem; the player's row first and framed
    void DrawTeam(float a)
    {
        CollectTeam();
        int n = mates.Count;
        if (n == 0) return;

        float left = Mathf.Round(X(308f));
        float width = Mathf.Round(S(607f));
        float bottom = Y(933f);
        float rowH = Mathf.Floor(Mathf.Min(S(37f), (bottom - Y(700f)) / n));
        if (rowH < 8f) return;
        float top = bottom - rowH * n;
        float gap = Mathf.Max(1f, Mathf.Round(S(1f)));

        for (int i = 0; i < n; i++)
        {
            Health h = mates[i];
            bool me = h == player;
            bool dead = h.IsDead || !h.gameObject.activeInHierarchy;
            float ra = a * (dead ? 0.5f : 1f);
            Rect row = new Rect(left, Mathf.Round(top + i * rowH), width, rowH - gap);

            Fill(row, me ? new Color(0.2f, 0.2f, 0.2f, 0.8f * a) : new Color(0.07f, 0.07f, 0.07f, 0.62f * a));
            if (me) Frame(row, Mathf.Max(1f, Mathf.Round(S(2f))), new Color(0.85f, 0.85f, 0.85f, 0.5f * a));

            float pad = Mathf.Max(1f, Mathf.Round(S(4f)));
            float av = row.height - pad * 2f;
            Rect avatar = new Rect(row.xMax - av - pad, row.y + pad, av, av);
            Fill(avatar, new Color(0.24f, 0.21f, 0.15f, 0.9f * ra));
            Draw(avatar, Emblem(h.team), Fade(Color.white, ra));

            float moneyRight = avatar.x - Mathf.Round(S(10f));
            if (!Free)
                Text(new Rect(moneyRight - Mathf.Round(S(90f)), row.y, Mathf.Round(S(90f)), row.height), MoneyText(Economy.MoneyOf(h)),
                    teamMoneyStyle, Fade(new Color(0.92f, 0.92f, 0.92f, 1f), ra), TextAnchor.MiddleRight);

            Text(new Rect(row.x + Mathf.Round(S(10f)), row.y, Mathf.Round(S(200f)), row.height), NameOf(h), teamStyle,
                Fade(me ? Color.white : new Color(0.76f, 0.76f, 0.76f, 1f), ra), TextAnchor.MiddleLeft);

            float right = moneyRight - Mathf.Round(S(100f));
            if (dead)
            {
                Text(new Rect(right - Mathf.Round(S(200f)), row.y, Mathf.Round(S(200f)), row.height), "убит", teamStyle,
                    Fade(PriceRed, ra), TextAnchor.MiddleRight);
                continue;
            }
            WeaponsOf(h, out WeaponData primary, out WeaponData pistol, out WeaponData gear);
            right = TeamIcon(gear, right, row, S(14f), ra);
            right = TeamIcon(pistol, right, row, S(16f), ra);
            TeamIcon(primary, right, row, S(20f), ra);
        }
    }

    // one silhouette of a teammate's row, right-aligned at 'right'; returns where the next one ends
    float TeamIcon(WeaponData d, float right, Rect row, float maxH, float a)
    {
        if (d == null) return right;
        Texture2D icon = Icon(d.weaponName);
        if (icon == null || icon.height <= 0) return right;
        float h = Mathf.Round(Mathf.Min(maxH, row.height * 0.62f));
        float w = Mathf.Round(h * icon.width / icon.height);
        Draw(new Rect(Mathf.Round(right - w), Mathf.Round(row.y + (row.height - h) * 0.5f), w, h), icon, Fade(Color.white, a * 0.92f));
        return right - w - Mathf.Round(S(10f));
    }

    void CollectTeam()
    {
        mates.Clear();
        if (player == null) return;
        mates.Add(player);
        foreach (Health h in Health.All)
        {
            if (h == null || h == player || h.team != player.team) continue;
            if (BuyerOf(h) == null) continue;   // the training target and other objects with health
            mates.Add(h);
            if (mates.Count >= RoundManager.MaxPlayersPerTeam + 2) break;
        }
    }

    BotBuyer BuyerOf(Health h)
    {
        if (!buyers.TryGetValue(h, out BotBuyer b))
        {
            b = h.GetComponent<BotBuyer>();
            buyers[h] = b;
        }
        return b;
    }

    string NameOf(Health h)
    {
        if (h == player) return "Вы";
        if (!names.TryGetValue(h, out string n))
        {
            n = h.name;
            names[h] = n;
        }
        return n;
    }

    void WeaponsOf(Health h, out WeaponData primary, out WeaponData pistol, out WeaponData gear)
    {
        if (h == player)
        {
            primary = loadout.InSlot(PlayerLoadout.PrimarySlot);
            pistol = loadout.InSlot(PlayerLoadout.PistolSlot);
            gear = loadout.InSlot(PlayerLoadout.GearSlot);
            return;
        }
        BotBuyer b = BuyerOf(h);
        primary = b != null ? b.primary : null;
        pistol = b != null ? b.pistol : null;
        gear = null;
    }

    // the stats of the weapon under the mouse (bars like CS:GO), or the description of an item
    void DrawStats(Entry e, float a)
    {
        Rect head = V(1028f, 745f, 580f, 34f);
        Fill(head, new Color(0.38f, 0.35f, 0.31f, 0.86f * a));
        Text(new Rect(head.x + Mathf.Round(S(12f)), head.y, head.width - Mathf.Round(S(24f)), head.height), e.title, headStyle,
            Fade(Color.white, a), TextAnchor.MiddleLeft);

        if (e.weapon != null && !e.weapon.taser)
        {
            Fill(V(1028f, 779f, 580f, 190f), new Color(0f, 0f, 0f, 0.42f * a));
            Stats s = StatsOf(e.weapon);
            float y = 783f;
            StatRow(y, "ПАТРОНЫ:", s.ammo, -1f, a);
            y += 26f;
            StatRow(y, "НАГРАДА ЗА УБИЙСТВО:", s.reward, -1f, a);
            y += 26f;
            StatRow(y, "УРОН", s.damage, s.damageBar, a);
            y += 26f;
            StatRow(y, "СКОРОСТРЕЛЬНОСТЬ", s.rate, s.rateBar, a);
            y += 26f;
            StatRow(y, "КОНТРОЛЬ ОТДАЧИ", s.control, s.controlBar, a);
            y += 26f;
            StatRow(y, "ДАЛЬНОСТЬ ПОРАЖЕНИЯ", s.range, s.rangeBar, a);
            y += 26f;
            StatRow(y, "БРОНЕПРОБИВАЕМОСТЬ", s.armor, s.armorBar, a);
            return;
        }

        Fill(V(1028f, 779f, 580f, 120f), new Color(0f, 0f, 0f, 0.42f * a));
        string about = e.weapon != null ? StatsOf(e.weapon).about : e.about;
        Text(V(1050f, 790f, 536f, 72f), about, aboutStyle, Fade(new Color(0.86f, 0.86f, 0.86f, 1f), a), TextAnchor.UpperLeft);
        if (e.weapon != null)
        {
            StatRow(869f, "НАГРАДА ЗА УБИЙСТВО:", StatsOf(e.weapon).reward, -1f, a);
        }
        else
        {
            string tail = e.tag == Soon ? "СКОРО В ИГРЕ" : "НЕДОСТУПНО";
            Text(V(1050f, 869f, 536f, 24f), tail, statStyle, Fade(Gold, a), TextAnchor.MiddleLeft);
        }
    }

    void StatRow(float y, string label, string value, float bar, float a)
    {
        Rect r = V(1028f, y, 580f, 24f);
        Fill(r, new Color(1f, 1f, 1f, 0.035f * a));
        if (bar >= 0f) Fill(new Rect(r.x, r.y, Mathf.Round(r.width * Mathf.Clamp01(bar)), r.height), Fade(BarColor, a));
        float pad = Mathf.Round(S(12f));
        Text(new Rect(r.x + pad, r.y, r.width * 0.7f, r.height), label, statStyle, Fade(Color.white, a), TextAnchor.MiddleLeft);
        Text(new Rect(r.xMax - pad - r.width * 0.5f, r.y, r.width * 0.5f, r.height), value, statStyle,
            Fade(new Color(0.92f, 0.92f, 0.92f, 1f), a), TextAnchor.MiddleRight);
    }

    // the numbers of the stats panel from WeaponData (like the CS:GO buy menu)
    Stats StatsOf(WeaponData d)
    {
        if (stats.TryGetValue(d, out Stats s)) return s;
        s = new Stats();
        int total = Mathf.RoundToInt(d.damage * Mathf.Max(1, d.pellets));
        int rpm = Mathf.RoundToInt(d.fireRate * 60f);
        // WeaponData.recoil = 0.4 + 1.6 * (1 - 'Отдача %' / 100), back to the percent of the weapons table
        int control = Mathf.Clamp(Mathf.RoundToInt(100f * (1f - (d.recoil - 0.4f) / 1.6f)), 0, 100);
        int armor = Mathf.RoundToInt(d.armorPenetration * 200f);
        s.ammo = d.magazineSize + "/" + d.reserveAmmo;
        s.reward = "$" + d.killReward + " (" + Mathf.RoundToInt(d.killReward * 100f / Economy.DefaultKillReward) + "%)";
        s.damage = total.ToString();
        s.damageBar = Mathf.Clamp01(total / 100f);
        s.rate = rpm + " выстр./мин";
        s.rateBar = Mathf.Clamp01(rpm / 1000f);
        s.control = control.ToString();
        s.controlBar = control / 100f;
        s.range = Mathf.RoundToInt(d.effectiveRange) + " м";
        s.rangeBar = Mathf.Clamp01(d.effectiveRange / 100f);
        s.armor = armor.ToString();
        s.armorBar = Mathf.Clamp01((d.armorPenetration - 0.45f) / 0.55f);
        s.about = d.taser
            ? "Электрошокер: убивает с одного попадания на расстоянии до " + Mathf.RoundToInt(d.range) +
              " м, после выстрела заряжается " + Mathf.RoundToInt(d.rechargeTime) + " с."
            : "";
        stats[d] = s;
        return s;
    }

    // the own weapons in a column at the right edge, the one in the hands brighter
    void DrawOwn(float a)
    {
        float right = Mathf.Round(X(1890f));
        int current = loadout.CurrentSlot;
        OwnSlot(PlayerLoadout.PrimarySlot, Y(690f), right, current, a);
        OwnSlot(PlayerLoadout.PistolSlot, Y(770f), right, current, a);
        OwnSlot(PlayerLoadout.KnifeSlot, Y(850f), right, current, a);
        OwnSlot(PlayerLoadout.GearSlot, Y(925f), right, current, a);
    }

    void OwnSlot(int slot, float cy, float right, int current, float a)
    {
        WeaponData d = loadout.InSlot(slot);
        if (d == null) return;
        Texture2D icon = Icon(d.weaponName);
        float h = IconHeight(icon, S(140f), S(42f));
        if (h <= 0f) return;
        float w = Mathf.Round(h * icon.width / icon.height);
        Color c = slot == current ? new Color(0.92f, 0.92f, 0.92f, 1f) : new Color(0.58f, 0.58f, 0.58f, 0.9f);
        Draw(new Rect(Mathf.Round(right - w), Mathf.Round(cy - h * 0.5f), w, h), icon, Fade(c, a));
    }

    void DrawFooter(float a)
    {
        float h = Mathf.Round(S(95f));
        float top = Screen.height - h;
        Fill(new Rect(0f, top, Screen.width, h), new Color(0f, 0f, 0f, 0.62f * a));
        Fill(new Rect(0f, top, Screen.width, Mathf.Max(1f, Mathf.Round(S(1f)))), new Color(1f, 1f, 1f, 0.07f * a));

        Text(new Rect(Mathf.Round(X(80f)), top, Mathf.Round(S(600f)), h), level == 1 ? "[ESCAPE] НАЗАД" : "[ESCAPE] ЗАКРЫТЬ",
            footStyle, Fade(new Color(0.93f, 0.93f, 0.93f, 1f), a), TextAnchor.MiddleLeft);

        string hint = level == 0 ? HintCategories : HintItems;
        if (Swappable(selected))
        {
            Sector s = CurrentSectors()[selected];
            UpdateHints(s);
            hint = s.swapHint;
        }
        float right = Mathf.Round(X(1840f));
        float w = Mathf.Round(S(1300f));
        Text(new Rect(right - w, top, w, h), hint, footHintStyle, Fade(new Color(0.66f, 0.66f, 0.66f, 1f), a), TextAnchor.MiddleRight);
    }

    // The dark bar with thin gold edges (like CS:GO): the lower middle of the screen above the HUD;
    // while the menu is open, under the weapon view on the right, where it covers neither the wheel nor the panels.
    void DrawNotice(bool menu)
    {
        float t = Time.unscaledTime - noticeAt;
        float a = Mathf.Clamp01(t / 0.15f) * Mathf.Clamp01((noticeTime - t) / 0.6f);
        if (a <= 0f) return;

        if (!ReferenceEquals(noticeMeasured, notice) || !Mathf.Approximately(noticeWidthFor, k))
        {
            noticeMeasured = notice;
            noticeWidthFor = k;
            measure.text = notice;
            noticeWidth = noticeStyle.CalcSize(measure).x;
        }

        float w = Mathf.Round(Mathf.Min(menu ? S(620f) : Screen.width - 20f, Mathf.Max(S(460f), noticeWidth + S(130f))));
        float h = Mathf.Round(S(44f));
        float cx = menu ? X(1320f) : Screen.width * 0.5f;
        float cy = menu ? Y(692f) : Screen.height * 0.72f;
        Rect r = new Rect(Mathf.Round(cx - w * 0.5f), Mathf.Round(cy - h * 0.5f), w, h);

        Fill(r, new Color(0.11f, 0.105f, 0.1f, 0.78f * a));
        float edge = Mathf.Max(2f, Mathf.Round(S(3f)));
        Fill(new Rect(r.x, r.y, edge, h), Fade(Gold, a));
        Fill(new Rect(r.xMax - edge, r.y, edge, h), Fade(Gold, a));
        Text(r, notice, noticeStyle, new Color(1f, 1f, 1f, a), TextAnchor.MiddleCenter);
    }

    // ---------------- the 3D view ----------------

    Rect PreviewRect() => V(1000f, 160f, 640f, 480f);

    // the weapon under the mouse in a category; otherwise the weapon in the hands
    WeaponData PreviewWeapon()
    {
        if (!open || loadout == null) return null;
        if (level == 1)
        {
            Entry e = Hovered();
            if (e != null) return e.weapon;
        }
        return loadout.InSlot(loadout.CurrentSlot);
    }

    void DrawPreview(Entry hovered, float a)
    {
        WeaponData d = PreviewWeapon();
        Entry item = hovered != null && hovered.weapon == null ? hovered : null;
        if (d == null && item == null) return;

        Rect r = PreviewRect();
        Fill(r, new Color(PreviewBack.r, PreviewBack.g, PreviewBack.b, 0.55f * a));
        Draw(r, Spot(), new Color(1f, 1f, 1f, 0.07f * a));

        if (item != null)
        {
            DrawIcon(EntryIcon(item), r.center, S(240f), S(240f), new Color(0.5f, 0.5f, 0.5f, 0.8f * a));
            return;
        }
        if (previewReady && previewFor == d && previewRT != null)
        {
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.DrawTexture(r, previewRT, ScaleMode.StretchToFill, true);
            GUI.color = Color.white;
        }
        else
        {
            // no model (or the 3D view is off): the kill icon, big
            DrawIcon(Icon(d.weaponName), r.center, S(520f), S(150f), new Color(0.92f, 0.92f, 0.92f, 0.9f * a));
        }
    }

    // LateUpdate: shows the model of PreviewWeapon to the private camera and turns it a little
    void UpdatePreview()
    {
        WeaponData want = open && modelPreview ? PreviewWeapon() : null;
        PreviewModel m = want != null && EnsurePreview() ? ModelOf(want) : null;
        if (m == null || m.holder == null)
        {
            if (previewReady || (previewRoot != null && previewRoot.activeSelf)) HidePreview();
            previewFor = null;
            return;
        }

        Rect r = PreviewRect();
        if (!previewRoot.activeSelf) previewRoot.SetActive(true);
        bool refit = EnsureTarget(r);
        if (m != previewModel)
        {
            if (previewModel != null && previewModel.holder != null) previewModel.holder.SetActive(false);
            m.holder.SetActive(true);
            previewModel = m;
            previewSince = Time.unscaledTime;
            refit = true;
        }
        if (refit) FitCamera(m, r.width / Mathf.Max(1f, r.height));

        float t = Time.unscaledTime - previewSince;
        float yaw = Mathf.Sin(t * 0.9f) * 14f - 30f * Mathf.Exp(-t * 6f);
        previewPivot.localRotation = Quaternion.Euler(0f, yaw, 0f) * PreviewTurn;
        previewCam.enabled = true;
        previewFor = want;
        previewReady = true;
    }

    bool EnsurePreview()
    {
        if (previewBroken) return false;
        if (previewRoot != null) return true;
        try
        {
            previewRoot = new GameObject("Buy Menu Preview");
            // the camera looks along the sun, so the side it sees is lit
            previewRoot.transform.SetPositionAndRotation(PreviewPosition, Quaternion.LookRotation(LitSide(), Vector3.up));
            previewPivot = new GameObject("Pivot").transform;
            previewPivot.SetParent(previewRoot.transform, false);

            GameObject camObject = new GameObject("Buy Menu Preview Camera");
            camObject.transform.SetParent(previewRoot.transform, false);
            previewCam = camObject.AddComponent<Camera>();
            previewCam.enabled = false;
            previewCam.clearFlags = CameraClearFlags.SolidColor;
            previewCam.backgroundColor = PreviewBack;
            previewCam.cullingMask = 1 << PreviewLayer;
            previewCam.fieldOfView = PreviewFov;
            previewCam.nearClipPlane = 0.02f;
            previewCam.farClipPlane = 10f;
            previewCam.allowHDR = false;
            previewCam.allowMSAA = false;
            previewCam.useOcclusionCulling = false;
            previewCam.depth = -50f;
            previewRoot.SetActive(false);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogException(e);
            previewBroken = true;
            DestroyPreview();
            return false;
        }
    }

    // the horizontal direction of the sun (forward if there is none)
    static Vector3 LitSide()
    {
        Light sun = RenderSettings.sun;
        if (sun == null)
        {
            foreach (Light l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            {
                if (l.type != LightType.Directional) continue;
                sun = l;
                break;
            }
        }
        if (sun != null)
        {
            Vector3 f = sun.transform.forward;
            f.y = 0f;
            if (f.sqrMagnitude > 0.01f) return f.normalized;
        }
        return Vector3.forward;
    }

    // the model of a weapon under the pivot, centred on it (made once; null if there is none)
    PreviewModel ModelOf(WeaponData d)
    {
        if (previewModels.TryGetValue(d, out PreviewModel m)) return m;
        m = null;
        GameObject prefab = ViewModel.ModelFor(d);
        if (prefab != null)
        {
            GameObject holder = null;
            try
            {
                holder = new GameObject("Preview " + d.weaponName);
                holder.transform.SetParent(previewPivot, false);
                GameObject model = ViewModel.SpawnModel(prefab, holder.transform, false);
                SetLayer(holder.transform, PreviewLayer);
                if (LocalBounds(holder.transform, model, out Bounds b))
                {
                    holder.transform.localPosition = -b.center;
                    holder.SetActive(false);
                    m = new PreviewModel { holder = holder, size = b.size };
                }
                else
                {
                    Destroy(holder);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                if (holder != null) Destroy(holder);
                m = null;
            }
        }
        previewModels[d] = m;
        return m;
    }

    static void SetLayer(Transform t, int layer)
    {
        t.gameObject.layer = layer;
        for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
    }

    // the bounds of the model's meshes in the space of 'space'
    static bool LocalBounds(Transform space, GameObject go, out Bounds b)
    {
        b = default;
        bool any = false;
        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Encapsulate(space, mf.transform, mf.sharedMesh.bounds, ref b, ref any);
        foreach (SkinnedMeshRenderer sk in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (sk.sharedMesh != null) Encapsulate(space, sk.transform, sk.sharedMesh.bounds, ref b, ref any);
        return any;
    }

    static void Encapsulate(Transform space, Transform t, Bounds local, ref Bounds b, ref bool any)
    {
        Vector3 c = local.center, e = local.extents;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            Vector3 q = space.InverseTransformPoint(t.TransformPoint(p));
            if (!any)
            {
                b = new Bounds(q, Vector3.zero);
                any = true;
            }
            else
            {
                b.Encapsulate(q);
            }
        }
    }

    // the render texture for this size of the view (1.5x for smooth edges); true if it is new
    bool EnsureTarget(Rect r)
    {
        int w = Mathf.Clamp(Mathf.RoundToInt(r.width * 1.5f), 64, 2048);
        int h = Mathf.Clamp(Mathf.RoundToInt(r.height * 1.5f), 64, 2048);
        if (previewRT != null && previewRT.width == w && previewRT.height == h) return false;
        if (previewRT != null)
        {
            previewCam.targetTexture = null;
            previewRT.Release();
            Destroy(previewRT);
        }
        previewRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        previewRT.name = "Buy menu preview";
        previewRT.antiAliasing = 1;
        previewRT.Create();
        previewCam.targetTexture = previewRT;
        return true;
    }

    // the camera distance that fits the model (short guns are not blown up to the full width)
    void FitCamera(PreviewModel m, float aspect)
    {
        float tan = Mathf.Tan(PreviewFov * 0.5f * Mathf.Deg2Rad);
        float length = Mathf.Max(m.size.z, 0.55f);
        float height = Mathf.Max(m.size.y, 0.2f);
        float dist = Mathf.Max(length * 0.6f / (tan * Mathf.Max(0.2f, aspect)), height * 0.75f / tan) + m.size.x * 0.5f;
        dist = Mathf.Max(dist, 0.3f);
        Quaternion tilt = Quaternion.Euler(PreviewTilt, 0f, 0f);
        previewCam.transform.localRotation = tilt;
        previewCam.transform.localPosition = tilt * new Vector3(0f, 0f, -dist);
        previewCam.nearClipPlane = Mathf.Max(0.01f, dist * 0.2f);
        previewCam.farClipPlane = dist + Mathf.Max(2f, length * 2f);
    }

    void HidePreview()
    {
        if (previewCam != null) previewCam.enabled = false;
        if (previewRoot != null && previewRoot.activeSelf) previewRoot.SetActive(false);
        previewReady = false;
    }

    void DestroyPreview()
    {
        if (previewCam != null) previewCam.targetTexture = null;
        if (previewRoot != null) Destroy(previewRoot);
        if (previewRT != null)
        {
            previewRT.Release();
            Destroy(previewRT);
        }
        previewRoot = null;
        previewPivot = null;
        previewCam = null;
        previewRT = null;
        previewModel = null;
        previewModels.Clear();
        previewReady = false;
    }

    // ---------------- helpers ----------------

    string MoneyText(int m)
    {
        if (!moneyTexts.TryGetValue(m, out string s))
        {
            s = "$" + m;
            if (moneyTexts.Count < 2000) moneyTexts[m] = s;
        }
        return s;
    }

    Texture2D EntryIcon(Entry e)
    {
        if (e.icon == null) e.icon = e.weapon != null ? Icon(e.weapon.weaponName) : ShapeIcon(e.shape);
        return e.icon;
    }

    // the killfeed silhouette of a weapon (Resources/KillIcons/<weapon name>), null if there is none
    Texture2D Icon(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return null;
        if (!icons.TryGetValue(weaponName, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + weaponName);
            icons[weaponName] = tex;
        }
        return tex;
    }

    void BuildStyles()
    {
        if (topStyle != null && Mathf.Approximately(stylesFor, k)) return;
        stylesFor = k;

        topStyle = MakeStyle(27, FontStyle.Bold, false);
        moneyStyle = MakeStyle(32, FontStyle.Bold, false);
        freeStyle = MakeStyle(22, FontStyle.Bold, false);
        catStyle = MakeStyle(19, FontStyle.Bold, false);
        numStyle = MakeStyle(15, FontStyle.Normal, false);
        nameStyle = MakeStyle(15, FontStyle.Normal, false);
        nameSmallStyle = MakeStyle(13, FontStyle.Normal, false);
        priceStyle = MakeStyle(19, FontStyle.Bold, false);
        smallStyle = MakeStyle(12, FontStyle.Normal, false);
        headStyle = MakeStyle(19, FontStyle.Bold, false);
        statStyle = MakeStyle(12, FontStyle.Normal, false);
        aboutStyle = MakeStyle(15, FontStyle.Normal, true);
        footStyle = MakeStyle(18, FontStyle.Normal, false);
        footHintStyle = MakeStyle(15, FontStyle.Normal, false);
        teamStyle = MakeStyle(12, FontStyle.Normal, false);
        teamMoneyStyle = MakeStyle(12, FontStyle.Bold, false);
        noticeStyle = MakeStyle(20, FontStyle.Normal, false);
    }

    GUIStyle MakeStyle(int size, FontStyle fontStyle, bool wrap)
    {
        GUIStyle s = new GUIStyle(GUI.skin.label);
        s.fontSize = Mathf.Max(8, Mathf.RoundToInt(size * k));
        s.fontStyle = fontStyle;
        s.wordWrap = wrap;
        s.clipping = TextClipping.Overflow;
        s.richText = false;
        s.padding = new RectOffset(0, 0, 0, 0);
        s.margin = new RectOffset(0, 0, 0, 0);
        s.normal.textColor = Color.white;
        return s;
    }

    // the normal style if the text fits in this width, otherwise the smaller one
    GUIStyle Fit(string text, GUIStyle big, GUIStyle small, float width)
    {
        measure.text = text;
        return big.CalcSize(measure).x <= width ? big : small;
    }

    // a text with a soft dark shadow, readable on any background
    static void Text(Rect r, string text, GUIStyle style, Color color, TextAnchor anchor)
    {
        if (string.IsNullOrEmpty(text) || color.a <= 0.003f) return;
        style.alignment = anchor;
        float o = Mathf.Max(1f, Mathf.Round(style.fontSize / 15f));
        style.normal.textColor = new Color(0f, 0f, 0f, 0.6f * color.a);
        GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, style);
        style.normal.textColor = color;
        GUI.Label(r, text, style);
    }

    static Color Fade(Color c, float a)
    {
        return new Color(c.r, c.g, c.b, c.a * a);
    }

    static void Fill(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    static void Frame(Rect r, float thick, Color c)
    {
        Fill(new Rect(r.x, r.y, r.width, thick), c);
        Fill(new Rect(r.x, r.yMax - thick, r.width, thick), c);
        Fill(new Rect(r.x, r.y + thick, thick, r.height - thick * 2f), c);
        Fill(new Rect(r.xMax - thick, r.y + thick, thick, r.height - thick * 2f), c);
    }

    static void Draw(Rect r, Texture t, Color c)
    {
        if (t == null) return;
        GUI.color = c;
        GUI.DrawTexture(r, t);
        GUI.color = Color.white;
    }

    // the height a silhouette gets in this box (0 without an icon)
    static float IconHeight(Texture2D icon, float maxW, float maxH)
    {
        if (icon == null || icon.height <= 0) return 0f;
        float h = maxH;
        float w = h * icon.width / icon.height;
        if (w > maxW) h *= maxW / w;
        return Mathf.Round(h);
    }

    static void DrawIcon(Texture2D icon, Vector2 at, float maxW, float maxH, Color color)
    {
        float h = IconHeight(icon, maxW, maxH);
        if (h <= 0f) return;
        float w = Mathf.Round(h * icon.width / icon.height);
        Draw(new Rect(Mathf.Round(at.x - w * 0.5f), Mathf.Round(at.y - h * 0.5f), w, h), icon, color);
    }

    // ---------------- textures ----------------

    // a new screen size needs new wheels and emblems
    void CheckTextures()
    {
        if (Mathf.Approximately(texturesFor, wheelR)) return;
        DestroySizedTextures();
        texturesFor = wheelR;
    }

    void DestroySizedTextures()
    {
        foreach (Texture2D t in wheelTextures.Values) if (t != null) Destroy(t);
        foreach (Texture2D t in hoverTextures.Values) if (t != null) Destroy(t);
        wheelTextures.Clear();
        hoverTextures.Clear();
        hoverTops.Clear();
        for (int i = 0; i < emblems.Length; i++)
        {
            if (emblems[i] != null) Destroy(emblems[i]);
            emblems[i] = null;
        }
    }

    Texture2D WheelTexture(int n)
    {
        if (!wheelTextures.TryGetValue(n, out Texture2D t) || t == null)
        {
            t = MakeWheel(n, FirstAngle(n), wheelR, rimW, emblemR, Mathf.Max(1.5f, 2f * k));
            wheelTextures[n] = t;
        }
        return t;
    }

    Texture2D HoverTexture(int n, out float top)
    {
        if (!hoverTextures.TryGetValue(n, out Texture2D t) || t == null)
        {
            t = MakeWedge(n, wheelR - rimW, emblemR + Mathf.Round(4f * k), Mathf.Max(1.5f, 2f * k), out float made);
            hoverTextures[n] = t;
            hoverTops[n] = made;
        }
        top = hoverTops[n];
        return t;
    }

    Texture2D Emblem(int team)
    {
        team = team == 1 ? 1 : 0;
        if (emblems[team] == null) emblems[team] = MakeEmblem(team, Mathf.CeilToInt(emblemR * 2f) + 4, emblemR);
        return emblems[team];
    }

    Texture2D ShapeIcon(int shape)
    {
        if (shape < 0) return null;
        if (!shapeIcons.TryGetValue(shape, out Texture2D t) || t == null)
        {
            t = MakeShapeIcon(shape);
            shapeIcons[shape] = t;
        }
        return t;
    }

    Texture2D Spot()
    {
        if (spot == null) spot = MakeSpot(64);
        return spot;
    }

    Texture2D FadeRight()
    {
        if (fadeRight == null) fadeRight = MakeFade(64, true);
        return fadeRight;
    }

    Texture2D FadeDown()
    {
        if (fadeDown == null) fadeDown = MakeFade(64, false);
        return fadeDown;
    }

    // The whole wheel of n sectors: a dark disc (a little lighter outwards), the lines between the sectors
    // from the emblem to the rim, and the light rim. Made for the exact size, so the edges stay smooth.
    static Texture2D MakeWheel(int n, float first, float radius, float rim, float inner, float line)
    {
        const int pad = 2;
        int size = Mathf.CeilToInt(radius * 2f) + pad * 2;
        float c = size * 0.5f;

        // the directions of the lines between the sectors (texture rows go up, angles clockwise from up)
        Vector2[] edges = new Vector2[Mathf.Max(1, n)];
        for (int i = 0; i < n; i++)
        {
            float a = (first + (i + 0.5f) * 360f / n) * Mathf.Deg2Rad;
            edges[i] = new Vector2(Mathf.Sin(a), Mathf.Cos(a));
        }

        Color inside = new Color(0.075f, 0.078f, 0.083f, 0.9f);
        Color outside = new Color(0.112f, 0.115f, 0.12f, 0.9f);
        Color lineColor = new Color(0.3f, 0.305f, 0.31f, 0.9f);
        Color rimColor = new Color(0.25f, 0.255f, 0.26f, 0.96f);

        Color32[] px = new Color32[size * size];
        for (int j = 0; j < size; j++)
        {
            float y = j + 0.5f - c;
            for (int i = 0; i < size; i++)
            {
                float x = i + 0.5f - c;
                float rho = Mathf.Sqrt(x * x + y * y);
                float edge = Mathf.Clamp01(radius - rho + 0.5f);
                if (edge <= 0f) continue;   // stays clear

                Color col = Color.Lerp(inside, outside, Mathf.Clamp01((rho - inner) / Mathf.Max(1f, radius - inner)));
                if (n > 1 && rho > inner)
                {
                    float near = float.MaxValue;
                    for (int e = 0; e < n; e++)
                    {
                        Vector2 d = edges[e];
                        if (x * d.x + y * d.y <= 0f) continue;
                        near = Mathf.Min(near, Mathf.Abs(x * d.y - y * d.x));
                    }
                    col = Color.Lerp(col, lineColor, Mathf.Clamp01(line * 0.5f + 0.5f - near));
                }
                col = Color.Lerp(col, rimColor, Mathf.Clamp01(rho - (radius - rim) + 0.5f));
                col.a *= edge;
                px[j * size + i] = col;
            }
        }
        return MakeTexture(size, size, px, "Buy menu wheel", false);
    }

    // One sector of a wheel of n, pointing up: white with smooth edges, a little darker towards the centre;
    // the line between the sectors is left free. top = pixels from the wheel centre up to the texture's top edge.
    static Texture2D MakeWedge(int n, float outer, float inner, float gap, out float top)
    {
        float half = Mathf.PI / Mathf.Max(1, n);
        float sin = Mathf.Sin(half);
        float cos = Mathf.Cos(half);

        float xMax, yMin;
        if (n <= 1)
        {
            xMax = outer;
            yMin = -outer;
        }
        else if (n == 2)
        {
            xMax = outer;
            yMin = 0f;
        }
        else
        {
            xMax = outer * sin;
            yMin = inner * cos;
        }

        const int pad = 2;
        int w = Mathf.CeilToInt(xMax * 2f) + pad * 2;
        int h = Mathf.CeilToInt(outer - yMin) + pad * 2;
        top = outer + pad;
        float bottom = top - h;

        Color32[] px = new Color32[w * h];
        for (int j = 0; j < h; j++)
        {
            float y = bottom + j + 0.5f;   // texture rows go up
            for (int i = 0; i < w; i++)
            {
                float x = Mathf.Abs(i + 0.5f - w * 0.5f);
                float rho = Mathf.Sqrt(x * x + y * y);
                float d = Mathf.Min(outer - rho, rho - inner);
                if (n > 1) d = Mathf.Min(d, sin * y - cos * x - gap * 0.5f);   // inside both side lines
                float a = Mathf.Clamp01(d + 0.5f);
                float shade = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01((rho - inner) / Mathf.Max(1f, outer - inner)));
                px[j * w + i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * shade * 255f));
            }
        }
        return MakeTexture(w, h, px, "Buy menu sector", false);
    }

    // The team emblem: a gold disc with a dark border, a star and crossed knives (T) or a steel disc with a star,
    // a ring and an arc (CT).
    // 4x4 samples per pixel for smooth edges, mipmaps for the small copies in the team panel.
    static Texture2D MakeEmblem(int team, int size, float radius)
    {
        bool ct = team == 1;
        const int ss = 4;
        float half = size * 0.5f;
        Color32[] px = new Color32[size * size];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float r = 0f, g = 0f, b = 0f, a = 0f;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        Vector2 p = new Vector2((i + (sx + 0.5f) / ss - half) / radius, (j + (sy + 0.5f) / ss - half) / radius);
                        Color c = EmblemAt(p, ct);
                        r += c.r * c.a;
                        g += c.g * c.a;
                        b += c.b * c.a;
                        a += c.a;
                    }
                if (a <= 0f) continue;
                px[j * size + i] = new Color(r / a, g / a, b / a, a / (ss * ss));
            }
        return MakeTexture(size, size, px, ct ? "Buy menu emblem CT" : "Buy menu emblem T", true);
    }

    // p: -1..1 inside the emblem, y up
    static Color EmblemAt(Vector2 p, bool ct)
    {
        float d = p.magnitude;
        if (d > 1f) return Color.clear;
        if (d > 0.88f) return new Color(0.08f, 0.076f, 0.07f, 0.95f);

        Color light = ct ? new Color(0.86f, 0.87f, 0.84f, 1f) : new Color(0.95f, 0.85f, 0.6f, 1f);
        Color dark = ct ? new Color(0.48f, 0.55f, 0.62f, 1f) : new Color(0.66f, 0.51f, 0.29f, 1f);
        Color ink = ct ? new Color(0.11f, 0.16f, 0.24f, 1f) : new Color(0.24f, 0.16f, 0.08f, 1f);

        bool mark = (ct && d > 0.75f && d < 0.8f) || InStar(p, 0f, ct ? 0.14f : 0.3f, 0.36f);
        if (!mark && ct) mark = d > 0.5f && d < 0.58f && p.y < -0.02f && Mathf.Abs(p.x) > 0.1f;
        // T: two crossed knives under the star, the blades up and out
        if (!mark && !ct) mark = InKnife(p, -0.25f, -0.32f, 139f) || InKnife(new Vector2(-p.x, p.y), -0.25f, -0.32f, 139f);
        if (mark) return ink;
        return Color.Lerp(light, dark, Mathf.Clamp01(0.4f - p.y * 0.45f + d * 0.3f));
    }

    // a knife around (cx, cy) along 'degrees' (counterclockwise from +x, from the handle to the tip):
    // the handle, the guard and a blade with a curved tip
    static bool InKnife(Vector2 p, float cx, float cy, float degrees)
    {
        float a = degrees * Mathf.Deg2Rad;
        float ax = Mathf.Cos(a), ay = Mathf.Sin(a);
        float x = p.x - cx, y = p.y - cy;
        float u = x * ax + y * ay;    // along the knife
        float v = -x * ay + y * ax;   // across it
        if (u < -0.6f || u > 0.6f) return false;
        if (u <= -0.21f) return Mathf.Abs(v) <= (u < -0.55f ? 0.052f : 0.045f);
        if (u <= -0.15f) return Mathf.Abs(v) <= 0.11f && (Mathf.Abs(v) <= 0.03f || u > -0.18f);
        float t = (u + 0.15f) / 0.75f;
        float spine = -0.05f * (1f - Mathf.Pow(Mathf.Max(0f, (t - 0.6f) / 0.4f), 1.5f));
        float edge = 0.075f * (1f - Mathf.Pow(t, 2.2f));
        return v >= spine && v <= edge;
    }

    // a white silhouette of an item that is not in the game yet, 3x3 samples per pixel
    static Texture2D MakeShapeIcon(int shape)
    {
        const int size = 128, ss = 3;
        Color32[] px = new Color32[size * size];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                int hit = 0;
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        Vector2 p = new Vector2(((i + (sx + 0.5f) / ss) / size * 2f - 1f) * 1.08f, ((j + (sy + 0.5f) / ss) / size * 2f - 1f) * 1.08f);
                        if (ItemShape(shape, p)) hit++;
                    }
                if (hit > 0) px[j * size + i] = new Color32(255, 255, 255, (byte)(hit * 255 / (ss * ss)));
            }
        return MakeTexture(size, size, px, "Buy menu item icon", true);
    }

    // p: -1..1, y up
    static bool ItemShape(int shape, Vector2 p)
    {
        switch (shape)
        {
            case ShapeVest:
                return Vest(p);
            case ShapeVestHelmet:
                return Vest(new Vector2((p.x - 0.3f) / 0.72f, (p.y + 0.08f) / 0.72f))
                    || Helmet(new Vector2((p.x + 0.5f) / 0.8f, (p.y + 0.05f) / 0.8f));
            case ShapeDefuser:
                return InBox(p, 0f, -0.2f, 0.62f, 0.4f, 0.1f)
                    || (InBox(p, 0f, 0.32f, 0.3f, 0.16f, 0.06f) && !InBox(p, 0f, 0.28f, 0.18f, 0.1f, 0f));
            case ShapeShield:
                return InBox(p, 0f, 0f, 0.44f, 0.88f, 0.2f) && !InBox(p, 0f, 0.48f, 0.27f, 0.055f, 0f);
            case ShapeMolotov:
                return InBox(p, 0f, -0.42f, 0.3f, 0.48f, 0.13f) || InBox(p, 0f, 0.22f, 0.11f, 0.22f, 0f)
                    || InRotBox(p, 0.06f, 0.6f, 0.07f, 0.22f, -25f);
            case ShapeIncendiary:
                return (InBox(p, 0f, -0.2f, 0.3f, 0.6f, 0.08f) && !InBox(p, 0f, -0.05f, 0.31f, 0.045f, 0f))
                    || InBox(p, 0f, 0.5f, 0.17f, 0.1f, 0f) || InRotBox(p, 0.26f, 0.3f, 0.05f, 0.3f, -12f);
            case ShapeDecoy:
                return InBox(p, 0.12f, -0.2f, 0.22f, 0.58f, 0.08f) || InBox(p, 0.12f, 0.46f, 0.13f, 0.1f, 0f)
                    || InCircle(p, -0.48f, 0.55f, 0.08f) || InCircle(p, -0.6f, 0.2f, 0.06f) || InCircle(p, -0.38f, 0.82f, 0.05f);
            case ShapeFlash:
                return InBox(p, 0f, -0.25f, 0.2f, 0.58f, 0.07f) || InBox(p, 0f, 0.4f, 0.12f, 0.1f, 0f) || Rays(p, 0f, 0.62f);
            case ShapeHe:
                return InCircle(p, 0f, -0.25f, 0.5f) || InBox(p, 0f, 0.33f, 0.15f, 0.14f, 0f)
                    || InRotBox(p, 0.25f, 0.3f, 0.05f, 0.32f, -25f);
            case ShapeSmoke:
                return (InBox(p, 0f, -0.15f, 0.33f, 0.62f, 0.1f) && !InBox(p, 0f, 0.15f, 0.34f, 0.04f, 0f))
                    || InBox(p, 0f, 0.55f, 0.2f, 0.08f, 0f);
        }
        return false;
    }

    static bool Vest(Vector2 p)
    {
        return InBox(p, 0f, -0.08f, 0.52f, 0.72f, 0.16f) && !InCircle(p, 0f, 0.74f, 0.3f)
            && !InCircle(p, 0.66f, 0.36f, 0.26f) && !InCircle(p, -0.66f, 0.36f, 0.26f);
    }

    static bool Helmet(Vector2 p)
    {
        return (InCircle(p, 0f, -0.05f, 0.5f) && p.y >= -0.05f) || InBox(p, 0f, -0.07f, 0.62f, 0.06f, 0.03f);
    }

    // short rays over a flashbang
    static bool Rays(Vector2 p, float cx, float cy)
    {
        float x = p.x - cx, y = p.y - cy;
        float r = Mathf.Sqrt(x * x + y * y);
        if (r < 0.2f || r > 0.36f || y < -0.02f) return false;
        float m = Mathf.Repeat(Mathf.Atan2(y, x) * Mathf.Rad2Deg, 45f);
        return Mathf.Min(m, 45f - m) < 7f;
    }

    // a box with round corners (radius r) around (cx, cy), half sizes hx, hy
    static bool InBox(Vector2 p, float cx, float cy, float hx, float hy, float r)
    {
        float dx = Mathf.Max(0f, Mathf.Abs(p.x - cx) - (hx - r));
        float dy = Mathf.Max(0f, Mathf.Abs(p.y - cy) - (hy - r));
        return dx * dx + dy * dy <= r * r;
    }

    static bool InCircle(Vector2 p, float cx, float cy, float r)
    {
        float dx = p.x - cx, dy = p.y - cy;
        return dx * dx + dy * dy <= r * r;
    }

    // a box turned by 'degrees' (counterclockwise) around its centre
    static bool InRotBox(Vector2 p, float cx, float cy, float hx, float hy, float degrees)
    {
        float a = -degrees * Mathf.Deg2Rad;
        float c = Mathf.Cos(a), s = Mathf.Sin(a);
        float x = p.x - cx, y = p.y - cy;
        float rx = x * c - y * s;
        float ry = x * s + y * c;
        return Mathf.Abs(rx) <= hx && Mathf.Abs(ry) <= hy;
    }

    // the corners of a five-pointed star of radius 1, the first one up
    static readonly Vector2[] StarCorners = MakeStarCorners();

    static Vector2[] MakeStarCorners()
    {
        Vector2[] c = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float a = (90f + i * 36f) * Mathf.Deg2Rad;
            float r = (i & 1) == 0 ? 1f : 0.4f;
            c[i] = new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        return c;
    }

    static bool InStar(Vector2 p, float cx, float cy, float radius)
    {
        float x = (p.x - cx) / radius, y = (p.y - cy) / radius;
        if (x * x + y * y > 1f) return false;
        bool inside = false;
        for (int i = 0, j = 9; i < 10; j = i++)
        {
            Vector2 a = StarCorners[i], b = StarCorners[j];
            if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x) inside = !inside;
        }
        return inside;
    }

    // a soft round spot, white (tinted when drawn): the shadow behind the wheel, the light behind the 3D view
    static Texture2D MakeSpot(int n)
    {
        Color32[] px = new Color32[n * n];
        float c = n * 0.5f;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                float dx = i + 0.5f - c;
                float dy = j + 0.5f - c;
                float t = Mathf.Sqrt(dx * dx + dy * dy) / c;
                float a = Mathf.Clamp01((1f - t) / 0.45f);
                a = a * a * (3f - 2f * a);
                px[j * n + i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
        return MakeTexture(n, n, px, "Buy menu spot", false);
    }

    // white, opaque at the left (right = true) or at the top (right = false), clear at the other end
    static Texture2D MakeFade(int n, bool right)
    {
        int w = right ? n : 1, h = right ? 1 : n;
        Color32[] px = new Color32[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (n - 1f);
            float a = right ? 1f - t : t;   // texture rows go up: the last row is the top
            a = a * a * (3f - 2f * a);
            px[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
        }
        return MakeTexture(w, h, px, "Buy menu fade", false);
    }

    static Texture2D MakeTexture(int w, int h, Color32[] px, string name, bool mips)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, mips);
        t.name = name;
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear;
        t.SetPixels32(px);
        t.Apply(mips, true);
        return t;
    }
}
