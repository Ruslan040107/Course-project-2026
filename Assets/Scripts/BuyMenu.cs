using System.Collections.Generic;
using UnityEngine;

// CS:GO-like radial buy menu of the player (IMGUI).
// B opens it (the cursor gets free), B again or Escape closes it (the cursor is locked again).
// Level 1: five category sectors around a circle (pistols, SMGs, rifles with the snipers, heavy, gear);
// the centre shows the money and the name of the selected category. Choosing a category does not close the menu,
// it opens level 2: a ring of the category's weapons with a "Назад" sector at the bottom.
// LMB on the centre, the "Назад" sector, RMB, Backspace or 0 go back to the categories.
// The mouse picks a sector by its direction from the centre (hover highlight), LMB chooses it; the mouse wheel moves
// the selection round the ring and Enter (or LMB) confirms it; the number keys choose a sector at once.
// Only the weapons of the player's team are shown (in the solo mode all of them, everything is free there).
// Buying needs Economy.CanBuy: alive, in the buy time and inside the team's buy zone. Otherwise the menu can still
// be opened to look: everything is grey and the centre says why. A bought weapon goes into its slot of PlayerLoadout
// (the HUD shows the money popup through Economy.MoneyChanged); the menu stays open.
// While it is open the cursor is free, so the player can not shoot, switch weapons or turn the camera;
// moving still works like in CS. MainMenu reads IsOpen, so the Escape that closes this menu does not open the pause.
// RoundManager adds it in normal games (never in simulations).
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

    RoundManager rounds;
    Health player;
    PlayerLoadout loadout;

    struct Category
    {
        public string title;
        public WeaponCategory a, b;
        public string iconT, iconCT;   // the weapon whose silhouette stands for the category
        public Category(string title, WeaponCategory a, WeaponCategory b, string iconT, string iconCT)
        {
            this.title = title;
            this.a = a;
            this.b = b;
            this.iconT = iconT;
            this.iconCT = iconCT;
        }
    }

    static readonly Category[] categories =
    {
        new Category("Пистолеты", WeaponCategory.Pistol, WeaponCategory.Pistol, "Desert Eagle", "Desert Eagle"),
        new Category("Пистолеты-пулемёты", WeaponCategory.SMG, WeaponCategory.SMG, "MAC-10", "MP9"),
        new Category("Винтовки", WeaponCategory.Rifle, WeaponCategory.Sniper, "AK-47", "M4A4"),
        new Category("Тяжёлое", WeaponCategory.Shotgun, WeaponCategory.MachineGun, "XM1014", "XM1014"),
        new Category("Снаряжение", WeaponCategory.Gear, WeaponCategory.Gear, "Zeus x27", "Zeus x27"),
    };

    // gear that is not in the game yet: grey sectors with "скоро" in the gear ring (team -1 = both teams)
    struct SoonItem
    {
        public string name;
        public int price;
        public int team;
        public SoonItem(string name, int price, int team)
        {
            this.name = name;
            this.price = price;
            this.team = team;
        }
    }

    static readonly SoonItem[] soonGear =
    {
        new SoonItem("Кевлар", 650, -1),
        new SoonItem("Кевлар + шлем", 1000, -1),
        new SoonItem("Дымовая граната", 300, -1),
        new SoonItem("Световая граната", 200, -1),
        new SoonItem("Осколочная граната", 300, -1),
        new SoonItem("Молотов", 400, 0),
        new SoonItem("Дефьюз-кит", 400, 1),
    };

    // one sector of the ring
    class Item
    {
        public string title;
        public int category = -1;   // level 1: the category it opens
        public WeaponData weapon;   // level 2: the weapon it buys
        public bool back;           // level 2: back to the categories
        public bool soon;           // gear that is not in the game yet
        public string priceText = "";
        public Texture2D icon;
    }

    readonly List<Item> ring = new List<Item>();
    int level;                      // 0 = the categories, 1 = the weapons of 'category'
    int category;
    int builtLevel = -1, builtCategory = -1, builtTeam = -2;

    int selected = -1;              // the highlighted sector, -1 = none (the mouse is in the centre)
    bool wheelMode;                 // the wheel / keys moved the selection; the mouse takes over when it moves
    bool centreHover;               // the mouse is in the centre circle
    Vector2 lastMouse;
    float openedAt, levelAt;

    // this frame, from Update
    bool canBuy;
    string blockedText = "";

    string message = "";
    Color messageColor = Color.white;
    float messageUntil;

    // look (accent like MainMenu)
    static readonly Color Accent = new Color(0.96f, 0.64f, 0.2f, 1f);
    static readonly Color TColor = new Color(0.88f, 0.31f, 0.23f, 1f);
    static readonly Color CTColor = new Color(0.29f, 0.55f, 0.94f, 1f);
    static readonly Color SectorColor = new Color(0.075f, 0.085f, 0.105f, 0.86f);
    static readonly Color SectorHover = new Color(0.22f, 0.17f, 0.1f, 0.95f);
    static readonly Color SectorOff = new Color(0.055f, 0.058f, 0.065f, 0.74f);
    static readonly Color SectorOffHover = new Color(0.14f, 0.145f, 0.155f, 0.86f);
    static readonly Color BackColor = new Color(0.12f, 0.13f, 0.155f, 0.88f);
    static readonly Color CentreColor = new Color(0.045f, 0.05f, 0.06f, 0.93f);
    static readonly Color EdgeColor = new Color(1f, 1f, 1f, 0.09f);
    static readonly Color OffRim = new Color(1f, 1f, 1f, 0.28f);
    static readonly Color DimText = new Color(0.66f, 0.69f, 0.73f, 1f);
    static readonly Color MoneyColor = new Color(0.55f, 0.9f, 0.45f, 1f);
    static readonly Color NoMoneyColor = new Color(0.95f, 0.4f, 0.35f, 1f);
    static readonly Color OwnedColor = new Color(0.45f, 0.85f, 0.45f, 1f);

    // geometry at 1080p (scaled by k)
    const float OuterAt1080 = 380f;
    const float InnerAt1080 = 132f;

    float k = 1f;
    Vector2 centre;
    float outerR = OuterAt1080, innerR = InnerAt1080, gap = 4f;

    float stylesFor = -1f;
    GUIStyle titleStyle, moneyStyle, moneySmallStyle, captionStyle, textStyle, smallStyle,
             catStyle, catSmallStyle, nameStyle, nameSmallStyle, priceStyle, keyStyle, arrowStyle;
    readonly GUIContent measure = new GUIContent();
    readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
    readonly Dictionary<WeaponData, string> stats = new Dictionary<WeaponData, string>();

    // cached texts
    int moneyShown = -1;
    string moneyText = "";
    int secondsShown = -1;
    string timeText = "";

    // the shapes are textures made for the exact size, so the edges stay smooth at any resolution
    class Shape
    {
        public Texture2D fill, rim;
        public float top;           // pixels from the ring centre up to the texture's top edge
    }

    readonly Dictionary<int, Shape> wedges = new Dictionary<int, Shape>();
    Shape disc;
    Texture2D shadow;
    float shapesOuter = -1f, shapesInner = -1f, shapesGap = -1f;

    void Awake()
    {
        rounds = GetComponent<RoundManager>();
        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
    }

    // the first ring and the centre are made now, so the first B press has no hitch
    void Start()
    {
        Layout();
        CheckShapes();
        Wedge(categories.Length);
        if (disc == null) disc = MakeDisc(innerR - gap, RimWidth);
    }

    void OnDisable()
    {
        if (open) Close(false);
    }

    void OnDestroy()
    {
        DestroyShapes();
        if (shadow != null) Destroy(shadow);
    }

    bool FindPlayer()
    {
        if (player == null && rounds != null) player = rounds.player;
        if (player != null && loadout == null) loadout = player.GetComponent<PlayerLoadout>();
        return player != null && loadout != null;
    }

    bool Free => !Economy.Enabled;

    int Team => player != null ? player.team : 0;

    bool PlayerAlive()
    {
        if (rounds != null && rounds.simulation) return false;
        return FindPlayer() && !player.IsDead && player.gameObject.activeInHierarchy;
    }

    // buying is allowed now: alive, in the buy time of a running round and inside the team's buy zone (solo: always)
    bool CanBuyNow()
    {
        if (!PlayerAlive() || !Economy.CanBuy(player)) return false;
        return Free || rounds == null || (!rounds.InBreak && !rounds.MatchOver);
    }

    // why buying is not possible now ("" = it is)
    string BlockedText()
    {
        if (CanBuyNow()) return "";
        if (!PlayerAlive()) return "Закупка недоступна";
        if (!Economy.InBuyTime || (rounds != null && (rounds.InBreak || rounds.MatchOver))) return "Время закупки истекло";
        if (!Economy.InBuyZone(player)) return "Вне зоны закупки";
        return "Закупка недоступна";
    }

    // ---------------- input ----------------

    void Update()
    {
        if (open)
        {
            if (!PlayerAlive() || MainMenu.IsOpen)
            {
                // the pause menu owns the cursor then; otherwise the game takes it back
                Close(!MainMenu.IsOpen);
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(key))
            {
                Close(true);
                return;
            }

            canBuy = CanBuyNow();
            blockedText = BlockedText();
            Layout();
            BuildRing();
            HandleInput();
            return;
        }

        if (Input.GetKeyDown(key) && Time.frameCount != closedFrame && !MainMenu.IsOpen && Time.timeScale > 0f && PlayerAlive())
            Open();
    }

    void LateUpdate()
    {
        // nothing may lock the cursor under the menu (for example MainMenu takes it back on a click)
        if (open && Cursor.lockState != CursorLockMode.None)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    void Open()
    {
        open = true;
        level = 0;
        selected = -1;
        wheelMode = false;
        centreHover = true;
        message = "";
        openedAt = Time.unscaledTime;
        levelAt = openedAt;
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        lastMouse = MouseGui();
        canBuy = CanBuyNow();
        blockedText = BlockedText();
        Layout();
        BuildRing();
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
    }

    // the ring in the middle of the screen, a little higher to leave room for the text under it
    void Layout()
    {
        k = Mathf.Min(Screen.height / 1080f, Screen.width / 1400f);
        outerR = Mathf.Round(OuterAt1080 * k);
        innerR = Mathf.Round(InnerAt1080 * k);
        gap = Mathf.Max(2f, Mathf.Round(4f * k));
        centre = new Vector2(Mathf.Round(Screen.width * 0.5f), Mathf.Round(Screen.height * 0.5f - 26f * k));
    }

    static Vector2 MouseGui()
    {
        Vector3 m = Input.mousePosition;
        return new Vector2(m.x, Screen.height - m.y);
    }

    void HandleInput()
    {
        int n = ring.Count;
        Vector2 mouse = MouseGui();
        if ((mouse - lastMouse).sqrMagnitude > 4f) wheelMode = false;   // the mouse moved: it picks again
        lastMouse = mouse;

        Vector2 d = mouse - centre;
        centreHover = d.sqrMagnitude < innerR * innerR;
        if (!wheelMode) selected = centreHover ? -1 : SectorAt(d);

        // the wheel goes round the ring (down = clockwise)
        float wheel = Input.mouseScrollDelta.y;
        if (wheel != 0f && n > 0)
        {
            int step = wheel < 0f ? 1 : -1;
            selected = selected < 0 ? (step > 0 ? 0 : n - 1) : ((selected + step) % n + n) % n;
            wheelMode = true;
        }

        // 1-9: a sector at once, 0 / Backspace / RMB: back
        for (int i = 0; i < 9; i++)
        {
            if (!Input.GetKeyDown(KeyCode.Alpha1 + i) && !Input.GetKeyDown(KeyCode.Keypad1 + i)) continue;
            if (i < NumberedCount())
            {
                selected = i;
                wheelMode = true;
                Activate(i);
            }
            return;
        }
        if (Input.GetKeyDown(KeyCode.Alpha0) || Input.GetKeyDown(KeyCode.Keypad0) ||
            Input.GetKeyDown(KeyCode.Backspace) || Input.GetMouseButtonDown(1))
        {
            Back();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (selected >= 0 && selected < n) Activate(selected);
            return;
        }

        if (Input.GetMouseButtonDown(0))
        {
            if (!wheelMode && centreHover) Back();
            else if (selected >= 0 && selected < n) Activate(selected);
        }
    }

    // level 1 starts at the top; level 2 has "Назад" at the bottom and the weapons go clockwise from it
    float StartAngle => level == 0 ? 0f : 180f;
    int Shift => level == 0 ? 0 : 1;

    // the direction of sector i, degrees clockwise from up
    float AngleOf(int i)
    {
        int n = ring.Count;
        return n > 0 ? StartAngle + ((i + Shift) % n) * 360f / n : 0f;
    }

    // the sector in the direction of d (screen pixels from the centre, y down)
    int SectorAt(Vector2 d)
    {
        int n = ring.Count;
        if (n == 0 || d.sqrMagnitude < 1f) return -1;
        float a = Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg;
        float w = 360f / n;
        int p = Mathf.FloorToInt(Mathf.Repeat(a - StartAngle + w * 0.5f, 360f) / w) % n;
        return ((p - Shift) % n + n) % n;
    }

    // the sectors with a number key (all but "Назад", which is the last one)
    int NumberedCount()
    {
        return level == 0 ? ring.Count : Mathf.Max(0, ring.Count - 1);
    }

    void Activate(int index)
    {
        if (index < 0 || index >= ring.Count) return;
        Item it = ring[index];
        if (it.back) Back();
        else if (it.category >= 0) OpenCategory(it.category);
        else if (it.soon) Say(it.title + " — скоро в игре", DimText);
        else if (it.weapon != null) Buy(it.weapon);
    }

    void OpenCategory(int c)
    {
        level = 1;
        category = c;
        levelAt = Time.unscaledTime;
        BuildRing();
        // keys and the wheel start on the first weapon; the mouse picks by itself
        selected = wheelMode ? 0 : centreHover ? -1 : SectorAt(lastMouse - centre);
    }

    void Back()
    {
        if (level == 0) return;
        int c = category;
        level = 0;
        levelAt = Time.unscaledTime;
        BuildRing();
        selected = wheelMode ? c : centreHover ? -1 : SectorAt(lastMouse - centre);
    }

    // ---------------- the ring ----------------

    void BuildRing()
    {
        int team = Free ? -1 : Team;
        if (builtLevel == level && builtCategory == category && builtTeam == team) return;
        builtLevel = level;
        builtCategory = category;
        builtTeam = team;
        ring.Clear();

        if (level == 0)
        {
            for (int i = 0; i < categories.Length; i++)
            {
                Category c = categories[i];
                ring.Add(new Item { title = c.title, category = i, icon = Icon(team == 1 ? c.iconCT : c.iconT) });
            }
        }
        else
        {
            Category c = categories[Mathf.Clamp(category, 0, categories.Length - 1)];
            // rifles before the snipers, shotguns before the machine guns; cheapest first
            AddWeapons(c.a, team);
            if (c.b != c.a) AddWeapons(c.b, team);
            if (c.a == WeaponCategory.Gear)
            {
                foreach (SoonItem s in soonGear)
                    if (team < 0 || s.team < 0 || s.team == team)
                        ring.Add(new Item { title = s.name, soon = true, priceText = "$" + s.price });
            }
            ring.Add(new Item { title = "Назад", back = true });
        }

        if (selected >= ring.Count) selected = -1;
    }

    // the weapons of this category the team can buy (team -1 = all of them)
    void AddWeapons(WeaponCategory cat, int team)
    {
        foreach (WeaponData d in Economy.Weapons)
        {
            if (d == null || !Economy.Buyable(d) || d.category != cat) continue;
            if (team >= 0 && !Economy.TeamCanBuy(d, team)) continue;
            ring.Add(new Item { title = d.weaponName, weapon = d, icon = Icon(d.weaponName), priceText = "$" + d.price });
        }
    }

    // ---------------- buying ----------------

    bool TooExpensive(WeaponData d)
    {
        return !Free && Economy.MoneyOf(player) < d.price;
    }

    void Buy(WeaponData d)
    {
        if (d == null || !Economy.Buyable(d) || !FindPlayer()) return;

        if (!CanBuyNow())
        {
            Say(BlockedText(), NoMoneyColor);
            return;
        }
        if (loadout.Owns(d))
        {
            Say("Уже есть: " + d.weaponName, DimText);
            return;
        }
        if (!Free && !Economy.TeamCanBuy(d, Team)) return;   // never in the ring: only the own team's weapons are there

        int money = Economy.MoneyOf(player);
        if (!Free && money < d.price)
        {
            Say("Не хватает $" + (d.price - money) + " на " + d.weaponName, NoMoneyColor);
            return;
        }
        if (!Economy.TrySpend(player, d.price, Economy.ReasonBuy)) return;

        if (!loadout.Give(d))
        {
            // the loadout could not take it: the money comes back
            if (!Free) Economy.Set(player, Economy.MoneyOf(player) + d.price);
            return;
        }
        Say("Куплено: " + d.weaponName + (Free ? "" : "   −$" + d.price), OwnedColor);
    }

    void Say(string text, Color color)
    {
        message = text;
        messageColor = color;
        messageUntil = Time.unscaledTime + 2f;
    }

    // ---------------- drawing ----------------

    void OnGUI()
    {
        GUI.depth = -60;   // above the HUD, the radar and the scoreboard, below the pause menu
        if (!open || player == null || loadout == null) return;
        if (Event.current.type != EventType.Repaint) return;   // the input is read in Update

        Layout();
        BuildStyles();
        BuildRing();
        CheckShapes();

        float appear = Mathf.Clamp01((Time.unscaledTime - openedAt) / 0.12f);
        float levelFade = Mathf.Clamp01((Time.unscaledTime - levelAt) / 0.12f);
        float ease = appear * appear * (3f - 2f * appear);

        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.38f * appear));

        // the ring grows a little when it opens
        Matrix4x4 baseMatrix = GUI.matrix;
        float s = Mathf.Lerp(0.93f, 1f, ease);
        GUIUtility.ScaleAroundPivot(new Vector2(s, s), centre);

        float sr = outerR + Mathf.Round(90f * k);
        if (shadow == null) shadow = MakeShadow(64);
        Draw(new Rect(centre.x - sr, centre.y - sr, sr * 2f, sr * 2f), shadow, new Color(1f, 1f, 1f, 0.65f * appear));

        DrawRing(appear * Mathf.Lerp(0.35f, 1f, levelFade));
        DrawCentre(appear);

        GUI.matrix = baseMatrix;
        DrawCaption(appear);
        GUI.color = Color.white;
    }

    void DrawRing(float alpha)
    {
        int n = ring.Count;
        if (n == 0) return;
        Shape shape = Wedge(n);
        float pop = Mathf.Round(7f * k);

        for (int i = 0; i < n; i++)
        {
            Item it = ring[i];
            bool hover = i == selected;
            float p = hover ? pop : 0f;
            float angle = AngleOf(i);
            SectorColors(it, hover, out Color fill, out Color rim);

            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, centre);
            Rect r = new Rect(centre.x - shape.fill.width * 0.5f, centre.y - shape.top - p, shape.fill.width, shape.fill.height);
            Draw(r, shape.fill, Fade(fill, alpha));
            Draw(r, shape.rim, Fade(rim, alpha));
            GUI.matrix = m;

            DrawSectorContent(it, i, angle, p, alpha, hover);
        }
    }

    // grey when buying is not possible (or the gear is not in the game yet); "Назад" always works
    bool IsOff(Item it)
    {
        return !it.back && (it.soon || !canBuy);
    }

    void SectorColors(Item it, bool hover, out Color fill, out Color rim)
    {
        bool owned = it.weapon != null && loadout.Owns(it.weapon);
        bool expensive = it.weapon != null && TooExpensive(it.weapon);

        if (IsOff(it))
        {
            fill = hover ? SectorOffHover : SectorOff;
            rim = hover ? OffRim : EdgeColor;
        }
        else
        {
            fill = hover ? SectorHover : it.back ? BackColor : SectorColor;
            rim = hover ? (expensive && !owned ? NoMoneyColor : Accent) : EdgeColor;
        }
        if (owned) rim = hover ? OwnedColor : Fade(OwnedColor, 0.7f);
    }

    void DrawSectorContent(Item it, int index, float angle, float pop, float alpha, bool hover)
    {
        int n = ring.Count;
        float rad = angle * Mathf.Deg2Rad;
        Vector2 axis = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
        float mid = (innerR + outerR) * 0.5f + pop;
        Vector2 p = centre + axis * mid;

        // the room for an upright block in the middle of the sector
        float half = n > 1 ? Mathf.Min(Mathf.PI / n, Mathf.PI * 0.5f) : Mathf.PI * 0.5f;
        float width = Mathf.Min(2f * mid * Mathf.Sin(half) - gap * 3f, (outerR - innerR) * 0.9f);
        bool off = IsOff(it);
        float a = alpha * (off ? 0.42f : 1f);

        // the number key near the outer edge
        int number = it.back ? 0 : index + 1;
        if (number <= 9)
        {
            Vector2 q = centre + axis * (outerR - Mathf.Round(18f * k) + pop);
            float kw = Mathf.Round(30f * k);
            Label(new Rect(q.x - kw * 0.5f, q.y - kw * 0.5f, kw, kw), number.ToString(), keyStyle,
                Fade(hover && !off ? Accent : DimText, alpha * (off ? 0.6f : 0.9f)), TextAnchor.MiddleCenter);
        }

        if (it.back)
        {
            float ah = Mathf.Round(30f * k);
            float th = Mathf.Round(24f * k);
            float top = p.y - (ah + th) * 0.5f;
            Color c = Fade(hover ? Accent : Color.white, alpha);
            Label(new Rect(p.x - width * 0.5f, top, width, ah), "◄", arrowStyle, c, TextAnchor.MiddleCenter);
            Label(new Rect(p.x - width * 0.5f, top + ah, width, th), it.title, catStyle, c, TextAnchor.MiddleCenter);
            return;
        }

        if (it.category >= 0)
        {
            string ownedText = OwnedIn(it.category);
            float ih = IconHeight(it.icon, Mathf.Min(width, Mathf.Round(170f * k)), Mathf.Round(50f * k));
            float th = Mathf.Round(28f * k);
            float oh = ownedText != null ? Mathf.Round(20f * k) : 0f;
            float spacing = Mathf.Round(8f * k);
            float top = Mathf.Round(p.y - (ih + spacing + th + oh) * 0.5f);

            DrawIcon(it.icon, new Vector2(p.x, top + ih * 0.5f), Mathf.Min(width, Mathf.Round(170f * k)), Mathf.Round(50f * k),
                Fade(Color.white, a * (hover ? 1f : 0.85f)));
            GUIStyle st = Fit(it.title, catStyle, catSmallStyle, width);
            Label(new Rect(p.x - width * 0.5f, top + ih + spacing, width, th), it.title, st,
                Fade(hover && !off ? Accent : Color.white, a), TextAnchor.MiddleCenter);
            if (ownedText != null)
                Label(new Rect(p.x - width * 0.5f, top + ih + spacing + th, width, oh), ownedText, smallStyle,
                    Fade(OwnedColor, a), TextAnchor.MiddleCenter);
            return;
        }

        float nameH = Mathf.Round(22f * k);
        float priceH = Mathf.Round(20f * k);

        if (it.soon)
        {
            float sh = Mathf.Round(18f * k);
            float top = Mathf.Round(p.y - (nameH + sh + priceH) * 0.5f);
            Label(new Rect(p.x - width * 0.5f, top, width, nameH), it.title, Fit(it.title, nameStyle, nameSmallStyle, width),
                Fade(Color.white, alpha * 0.5f), TextAnchor.MiddleCenter);
            Label(new Rect(p.x - width * 0.5f, top + nameH, width, sh), "скоро", smallStyle,
                Fade(Accent, alpha * 0.55f), TextAnchor.MiddleCenter);
            Label(new Rect(p.x - width * 0.5f, top + nameH + sh, width, priceH), it.priceText, priceStyle,
                Fade(DimText, alpha * 0.5f), TextAnchor.MiddleCenter);
            return;
        }

        // a weapon: the killfeed silhouette, the name, the price (or "Есть")
        WeaponData d = it.weapon;
        bool owned = loadout.Owns(d);
        bool expensive = TooExpensive(d);
        float maxW = Mathf.Min(width, Mathf.Round(150f * k));
        float maxH = Mathf.Round((n <= 8 ? 42f : 34f) * k);
        float iconH = IconHeight(it.icon, maxW, maxH);
        float space = iconH > 0f ? Mathf.Round(6f * k) : 0f;
        float blockTop = Mathf.Round(p.y - (iconH + space + nameH + priceH) * 0.5f);

        float dim = expensive && !owned ? 0.55f : 1f;
        DrawIcon(it.icon, new Vector2(p.x, blockTop + iconH * 0.5f), maxW, maxH, Fade(Color.white, a * dim * (hover ? 1f : 0.85f)));

        float y = blockTop + iconH + space;
        Label(new Rect(p.x - width * 0.5f, y, width, nameH), it.title, Fit(it.title, nameStyle, nameSmallStyle, width),
            Fade(hover && !off ? Accent : Color.white, a * dim), TextAnchor.MiddleCenter);

        string price = owned ? "Есть" : it.priceText;
        Color priceColor = owned ? OwnedColor : Free ? DimText : expensive ? NoMoneyColor : MoneyColor;
        Label(new Rect(p.x - width * 0.5f, y + nameH, width, priceH), price, priceStyle, Fade(priceColor, a), TextAnchor.MiddleCenter);
    }

    // "У вас: AK-47" for the category of a weapon the player has, null if he has none
    string OwnedIn(int cat)
    {
        Category c = categories[cat];
        for (int slot = 0; slot < PlayerLoadout.SlotCount; slot++)
        {
            if (slot == PlayerLoadout.KnifeSlot) continue;
            WeaponData d = loadout.InSlot(slot);
            if (d != null && (d.category == c.a || d.category == c.b)) return "У вас: " + d.weaponName;
        }
        return null;
    }

    void DrawCentre(float alpha)
    {
        if (disc == null) disc = MakeDisc(innerR - gap, RimWidth);
        float size = disc.fill.width;
        Rect r = new Rect(centre.x - size * 0.5f, centre.y - size * 0.5f, size, size);
        bool backHover = level == 1 && centreHover && !wheelMode;
        Draw(r, disc.fill, Fade(CentreColor, alpha));
        Draw(r, disc.rim, Fade(backHover ? Accent : EdgeColor, alpha));

        float w = Mathf.Round((innerR - gap) * 1.62f);
        float x = centre.x - w * 0.5f;
        float cy = centre.y;

        // the money (or "Бесплатно" in the solo mode)
        string money;
        GUIStyle moneySt;
        if (Free)
        {
            money = "Бесплатно";
            moneySt = moneySmallStyle;
        }
        else
        {
            int m = Economy.MoneyOf(player);
            if (m != moneyShown)
            {
                moneyShown = m;
                moneyText = "$" + m;
            }
            money = moneyText;
            moneySt = moneyStyle;
        }

        // why buying is not possible, or the time left to buy
        string status;
        Color statusColor;
        if (!canBuy)
        {
            status = blockedText;
            statusColor = NoMoneyColor;
        }
        else if (Free)
        {
            status = "Всё оружие бесплатно";
            statusColor = DimText;
        }
        else
        {
            int sec = Mathf.CeilToInt(Economy.BuyTimeLeft);
            if (sec != secondsShown)
            {
                secondsShown = sec;
                timeText = "Закупка: " + sec / 60 + ":" + (sec % 60).ToString("00");
            }
            status = timeText;
            statusColor = DimText;
        }

        if (level == 0)
        {
            string team = Free ? "Тренировка" : Team == 1 ? "Спецназ" : "Террористы";
            Color teamColor = Free ? DimText : Team == 1 ? CTColor : TColor;
            Label(new Rect(x, cy - Mathf.Round(76f * k), w, Mathf.Round(22f * k)), team, smallStyle, Fade(teamColor, alpha), TextAnchor.MiddleCenter);
            Label(new Rect(x, cy - Mathf.Round(54f * k), w, Mathf.Round(50f * k)), money, moneySt, Fade(MoneyColor, alpha), TextAnchor.MiddleCenter);

            Item sel = selected >= 0 && selected < ring.Count ? ring[selected] : null;
            string title = sel != null ? sel.title : "Выберите раздел";
            Label(new Rect(x, cy + Mathf.Round(2f * k), w, Mathf.Round(30f * k)), title, Fit(title, titleStyle, catSmallStyle, w),
                Fade(sel != null ? Color.white : DimText, alpha), TextAnchor.MiddleCenter);
            Label(new Rect(x, cy + Mathf.Round(34f * k), w, Mathf.Round(22f * k)), status, Fit(status, smallStyle, priceStyle, w),
                Fade(statusColor, alpha), TextAnchor.MiddleCenter);
        }
        else
        {
            string title = categories[Mathf.Clamp(category, 0, categories.Length - 1)].title;
            Label(new Rect(x, cy - Mathf.Round(78f * k), w, Mathf.Round(28f * k)), title, Fit(title, titleStyle, catSmallStyle, w),
                Fade(Accent, alpha), TextAnchor.MiddleCenter);
            Label(new Rect(x, cy - Mathf.Round(50f * k), w, Mathf.Round(50f * k)), money, moneySt, Fade(MoneyColor, alpha), TextAnchor.MiddleCenter);
            Label(new Rect(x, cy + Mathf.Round(4f * k), w, Mathf.Round(22f * k)), status, Fit(status, smallStyle, priceStyle, w),
                Fade(statusColor, alpha), TextAnchor.MiddleCenter);
            Label(new Rect(x, cy + Mathf.Round(34f * k), w, Mathf.Round(28f * k)), "◄ Назад", catSmallStyle,
                Fade(backHover ? Accent : DimText, alpha), TextAnchor.MiddleCenter);
        }
    }

    // under the ring: the message of the last action or the selected weapon, its numbers, the keys
    void DrawCaption(float alpha)
    {
        float w = Mathf.Min(Screen.width - 20f, Mathf.Round(1000f * k));
        float x = centre.x - w * 0.5f;
        float y = centre.y + outerR + Mathf.Round(16f * k);
        Rect line1 = new Rect(x, y, w, Mathf.Round(30f * k));
        Rect line2 = new Rect(x, line1.yMax, w, Mathf.Round(24f * k));
        Rect hint = new Rect(x, line2.yMax + Mathf.Round(8f * k), w, Mathf.Round(22f * k));

        Item sel = selected >= 0 && selected < ring.Count ? ring[selected] : null;
        if (message.Length > 0 && Time.unscaledTime < messageUntil)
        {
            Label(line1, message, captionStyle, Fade(messageColor, alpha), TextAnchor.MiddleCenter);
        }
        else if (sel != null && sel.weapon != null)
        {
            WeaponData d = sel.weapon;
            string state;
            Color color;
            if (loadout.Owns(d))
            {
                state = d.weaponName + " — уже есть";
                color = OwnedColor;
            }
            else if (!canBuy)
            {
                state = d.weaponName + " — " + blockedText;
                color = NoMoneyColor;
            }
            else if (TooExpensive(d))
            {
                state = d.weaponName + " — не хватает $" + (d.price - Economy.MoneyOf(player));
                color = NoMoneyColor;
            }
            else
            {
                state = Free ? d.weaponName + " — бесплатно" : d.weaponName + " — $" + d.price;
                color = Color.white;
            }
            Label(line1, state, captionStyle, Fade(color, alpha), TextAnchor.MiddleCenter);
        }
        else if (sel != null && sel.soon)
        {
            Label(line1, sel.title + " — скоро в игре", captionStyle, Fade(DimText, alpha), TextAnchor.MiddleCenter);
        }

        if (sel != null && sel.weapon != null)
            Label(line2, StatsOf(sel.weapon), textStyle, Fade(DimText, alpha), TextAnchor.MiddleCenter);

        string keys = level == 0
            ? "ЛКМ — открыть раздел     колесо — выбор, Enter — подтвердить     B / Esc — закрыть"
            : "ЛКМ — купить     колесо — выбор, Enter — подтвердить     ПКМ / центр — назад     B / Esc — закрыть";
        Label(hint, keys, Fit(keys, smallStyle, priceStyle, w), Fade(DimText, alpha * 0.85f), TextAnchor.MiddleCenter);
    }

    // damage, magazine, rate of fire and the kill reward of a weapon
    string StatsOf(WeaponData d)
    {
        if (stats.TryGetValue(d, out string s)) return s;
        if (d.taser)
        {
            s = "Дальность " + Mathf.RoundToInt(d.range) + " м     заряд " + Mathf.RoundToInt(d.rechargeTime) + " с";
        }
        else
        {
            string damage = Mathf.RoundToInt(d.damage).ToString();
            if (d.pellets > 1) damage += " × " + d.pellets;
            s = "Урон " + damage + "     магазин " + d.magazineSize + " / " + d.reserveAmmo +
                "     " + Mathf.RoundToInt(d.fireRate * 60f) + " выстр./мин";
        }
        if (Economy.Enabled) s += "     за убийство $" + d.killReward;
        stats[d] = s;
        return s;
    }

    // ---------------- helpers ----------------

    float RimWidth => Mathf.Max(1.5f, 2.5f * k);

    void BuildStyles()
    {
        if (titleStyle != null && Mathf.Approximately(stylesFor, k)) return;
        stylesFor = k;
        stats.Clear();   // the kill reward part depends on the mode

        titleStyle = MakeStyle(21, FontStyle.Bold);
        moneyStyle = MakeStyle(40, FontStyle.Bold);
        moneySmallStyle = MakeStyle(30, FontStyle.Bold);
        captionStyle = MakeStyle(21, FontStyle.Bold);
        textStyle = MakeStyle(17, FontStyle.Normal);
        smallStyle = MakeStyle(15, FontStyle.Normal);
        catStyle = MakeStyle(20, FontStyle.Bold);
        catSmallStyle = MakeStyle(16, FontStyle.Bold);
        nameStyle = MakeStyle(16, FontStyle.Bold);
        nameSmallStyle = MakeStyle(13, FontStyle.Bold);
        priceStyle = MakeStyle(14, FontStyle.Normal);
        keyStyle = MakeStyle(14, FontStyle.Bold);
        arrowStyle = MakeStyle(26, FontStyle.Bold);
    }

    GUIStyle MakeStyle(int size, FontStyle fontStyle)
    {
        GUIStyle s = new GUIStyle(GUI.skin.label);
        s.fontSize = Mathf.Max(9, Mathf.RoundToInt(size * k));
        s.fontStyle = fontStyle;
        s.wordWrap = false;
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

    static void Label(Rect r, string text, GUIStyle style, Color color, TextAnchor anchor)
    {
        style.alignment = anchor;
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

    // ---------------- shapes ----------------

    // a new screen size needs new shapes
    void CheckShapes()
    {
        if (Mathf.Approximately(shapesOuter, outerR) && Mathf.Approximately(shapesInner, innerR) && Mathf.Approximately(shapesGap, gap))
            return;
        DestroyShapes();
        shapesOuter = outerR;
        shapesInner = innerR;
        shapesGap = gap;
    }

    void DestroyShapes()
    {
        foreach (Shape s in wedges.Values) DestroyShape(s);
        wedges.Clear();
        DestroyShape(disc);
        disc = null;
    }

    static void DestroyShape(Shape s)
    {
        if (s == null) return;
        if (s.fill != null) Destroy(s.fill);
        if (s.rim != null) Destroy(s.rim);
    }

    Shape Wedge(int n)
    {
        if (!wedges.TryGetValue(n, out Shape s))
        {
            s = MakeWedge(n, outerR, innerR, gap, RimWidth);
            wedges[n] = s;
        }
        return s;
    }

    // One sector of a ring of n, pointing up: white with smooth edges (a little darker towards the centre),
    // and its border line of width rimW. The gap between the sectors is cut off both sides.
    static Shape MakeWedge(int n, float outer, float inner, float gap, float rimW)
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
        float top = outer + pad;
        float bottom = top - h;

        Color32[] fill = new Color32[w * h];
        Color32[] rim = new Color32[w * h];
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
                float shade = Mathf.Lerp(0.8f, 1f, Mathf.Clamp01((rho - inner) / Mathf.Max(1f, outer - inner)));
                byte v = (byte)Mathf.RoundToInt(shade * 255f);
                fill[j * w + i] = new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));

                float r = a * Mathf.Clamp01(rimW + 0.5f - d);
                rim[j * w + i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(r * 255f));
            }
        }

        return new Shape { fill = MakeTexture(w, h, fill, "Buy menu sector"), rim = MakeTexture(w, h, rim, "Buy menu sector rim"), top = top };
    }

    // the centre circle: lighter in the middle, with its border line
    static Shape MakeDisc(float radius, float rimW)
    {
        const int pad = 2;
        int size = Mathf.CeilToInt(radius * 2f) + pad * 2;
        float c = size * 0.5f;

        Color32[] fill = new Color32[size * size];
        Color32[] rim = new Color32[size * size];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                float dx = i + 0.5f - c;
                float dy = j + 0.5f - c;
                float rho = Mathf.Sqrt(dx * dx + dy * dy);
                float d = radius - rho;
                float a = Mathf.Clamp01(d + 0.5f);
                byte v = (byte)Mathf.RoundToInt(Mathf.Lerp(1f, 0.8f, Mathf.Clamp01(rho / Mathf.Max(1f, radius))) * 255f);
                fill[j * size + i] = new Color32(v, v, v, (byte)Mathf.RoundToInt(a * 255f));
                float r = a * Mathf.Clamp01(rimW + 0.5f - d);
                rim[j * size + i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(r * 255f));
            }

        return new Shape { fill = MakeTexture(size, size, fill, "Buy menu centre"), rim = MakeTexture(size, size, rim, "Buy menu centre rim"), top = c };
    }

    // a soft dark spot behind the ring, so it reads well over a bright level
    static Texture2D MakeShadow(int n)
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
                px[j * n + i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt(a * 255f));
            }
        return MakeTexture(n, n, px, "Buy menu shadow");
    }

    static Texture2D MakeTexture(int w, int h, Color32[] px, string name)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.RGBA32, false);
        t.name = name;
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }
}
