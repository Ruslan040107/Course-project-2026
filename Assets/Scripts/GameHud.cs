using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// CS2-like HUD of the human player (IMGUI). Sizes scale with the screen height (made for 1080p).
// - Bottom left: health with a cross (red below 25) and, with the bhop upgrade, the speed bonus;
//   above them the money ("$ 800", only with the economy on) with "+$300  Убийство противника" popups
//   (Economy.MoneyChanged) and the buy hint "B закупка 0:15" while Economy.CanBuy (always in solo; no time
//   after the buy time with the console's "buyanywhere"); in the buy time outside the own buy zone a dim
//   "вне зоны закупки" instead. The hint hides while the buy menu or the developer console is open.
// - Bottom right: ammo (shotgun shells are just the number), weapon name and the reload bar; the fire mode
//   of the Glock-18 / FAMAS ("ОЧЕРЕДЬ" / "АВТО" / "ПОЛУАВТО"), the charge bar of the Zeus. PlayerLoadout draws
//   the weapon slots above it for a moment after a switch. While the dead player watches a teammate,
//   that teammate's health and ammo are shown.
// - Top centre: round timer (in the freeze time at the round start: the freeze seconds, blue), team scores
//   (T red on the left, CT blue on the right), one square per player (bright = alive, dark = dead), the round
//   message under it, "SOLO" in solo mode; under them a small "Боты заморожены" tag (RoundManager.BotsFrozen)
//   and the "Смена сторон" banner for a few seconds after RoundManager.Halftime.
// - Upper middle, in the freeze time: "Раунд начнётся через 3" (RoundManager.InFreezeTime / FreezeTimeLeft).
// - Centre: the crosshair of the settings (style, gap, thickness, length, outline; the dynamic one opens up
//   with the spread of the next shot), red arcs pointing at whoever has just hurt the player,
//   a short note when the fire mode is switched.
// While it runs, the old labels of Health, Bhop, RoundManager and PlayerHud are off and the canvas crosshair is hidden.
// It adds itself to every scene with a player, never in simulations. Nothing is drawn while a menu pauses the game.
public class GameHud : MonoBehaviour
{
    // true while the HUD runs: the old labels and the canvas crosshair check it
    public static bool Active { get; private set; }

    // layout shared with PlayerLoadout (the slot list sits above the ammo box), pixels at 1080p
    public const float Margin = 20f;
    public const float BoxHeight = 60f;
    public const float AmmoBoxWidth = 300f;
    public const float HealthBoxWidth = 210f;

    public static float Scale => Screen.height / 1080f;
    // y of the top edge of the bottom boxes, in screen pixels
    public static float BottomBoxTop => Screen.height - (Margin + BoxHeight) * Scale;

    [Header("Dynamic crosshair (the shape itself is in the settings, see GameSettings)")]
    // small: Weapon.CurrentSpread already grows with every shot of the player (the real inaccuracy of the next one)
    [Tooltip("extra gap for every shot, pixels at 1080p, it closes again quickly")] public float shotBloom = 1.5f;
    public float maxShotBloom = 6f;
    [Tooltip("pixels per second")] public float bloomRecovery = 45f;

    [Header("Damage direction")]
    [Tooltip("seconds an arc stays on the screen")] public float damageArcTime = 1f;
    [Tooltip("distance from the screen centre at 1080p")] public float damageArcRadius = 170f;

    [Header("Colours")]
    public Color tColor = new Color(1f, 0.35f, 0.35f);
    public Color ctColor = new Color(0.4f, 0.6f, 1f);
    public Color lowColor = new Color(1f, 0.25f, 0.2f);
    public Color bhopColor = new Color(1f, 0.72f, 0.25f);
    public Color reloadColor = new Color(1f, 0.85f, 0.4f);
    public Color moneyColor = new Color(0.45f, 0.9f, 0.4f);
    public Color freezeColor = new Color(0.55f, 0.85f, 1f);

    [Header("Money")]
    [Tooltip("seconds a '+$300' popup stays")] public float moneyPopupTime = 2.5f;

    [Header("Round start / halftime")]
    [Tooltip("seconds the 'Смена сторон' banner stays")] public float halftimeBannerTime = 4f;

    public static readonly Color PanelColor = new Color(0.03f, 0.04f, 0.05f, 0.7f);
    public static readonly Color DimText = new Color(1f, 1f, 1f, 0.6f);

    // the shape of a damage arc: radius 1 at the outer edge
    const float ArcInner = 0.86f;
    const float ArcHalfAngle = 26f;

    static GameHud instance;
    static Texture2D fadeTexture;
    static readonly string[] numberCache = new string[1000];
    static readonly GUIContent tempContent = new GUIContent();
    static readonly string[] bhopTexts = { "BHOP 0", "BHOP 1", "BHOP 2", "BHOP 3" };

    public PlayerController player;
    Health health;
    PlayerShoot shoot;
    PlayerLoadout loadout;
    Weapon weapon;
    Bhop bhop;
    Spectator spectator;
    RoundManager rounds;
    Camera playerCam;
    Health hooked;

    // the teammate the dead player watches, and his weapon
    Health watchedCache;
    Weapon watchedWeapon;

    struct DamageArc
    {
        public Health attacker;
        public Vector3 from;     // where the attacker was at the hit (not followed: stealth must stay stealth)
        public float time;
        public float strength;
    }

    readonly List<DamageArc> arcs = new List<DamageArc>();
    readonly List<Health> team0 = new List<Health>();
    readonly List<Health> team1 = new List<Health>();

    float bloom;
    Texture2D arcTexture;
    GUIStyle bigStyle, timerStyle, midStyle, smallStyle, messageStyle;

    // the round banner gets the colour of the team whose score went up when the message appeared
    string lastMessage = "";
    int bannerTeam = -1;
    int lastScore0, lastScore1;

    int timerValue = -1;
    string timerText = "";
    int bhopPercent = -1;
    string bhopPercentText = "";

    // money: the shown sum, a short flash after a change and the "+$300 убийство" popups
    struct MoneyPopup
    {
        public string text;
        public bool gain;
        public float time;
    }
    readonly List<MoneyPopup> moneyPopups = new List<MoneyPopup>();
    int moneyShown = -1;
    string moneyText = "";
    float moneyFlashTime = -10f;
    bool moneyFlashGain;
    int buySeconds = -1;
    string buyText = "";

    // the fire mode note in the middle after a right click on the Glock-18 / FAMAS
    float modeSwitchTime = -10f;
    string modeSwitchText = "";
    int chargeSeconds = -1;
    string chargeText = "";

    // freeze time countdown and the halftime banner
    int freezeSeconds = -1;
    string freezeText = "";
    float halftimeTime = -100f;
    int halftimeTeam = -1;

    GUIStyle tinyStyle, keyStyle, hugeStyle;

    // ---------------- bootstrap ----------------

    // Play mode starts without a domain reload: no flags left over from the last play in the editor
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        SetDrawing(false);
        // the ring textures of the last play are not saved with anything: free them
        for (int i = 0; i < ringSlots.Length; i++)
        {
            if (ringSlots[i] != null && ringSlots[i].tex != null) DestroyImmediate(ringSlots[i].tex);
            ringSlots[i] = null;
        }
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

    static bool SimulationFlag()
    {
        return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-sim") >= 0;
    }

    // a HUD for a scene with a human player, never in a simulation or without graphics
    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        PlayerController pc = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<GameHud>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
            if (pc == null) pc = root.GetComponentInChildren<PlayerController>(true);
        }
        if (pc == null || (rm != null && rm.simulation)) return;

        GameObject go = new GameObject("GameHud");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<GameHud>().player = pc;
    }

    void Awake()
    {
        // background runs pass -sim on the command line; RoundManager reads it only in its Start
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        // a second HUD in the same scene stays off; the HUD of the previous scene may still exist while this one loads
        bool second = instance != null && instance != this && instance.gameObject.scene == gameObject.scene;
        if ((rm != null && rm.simulation) || SimulationFlag() || Application.isBatchMode || second)
        {
            enabled = false;
            return;
        }
        instance = this;
    }

    void OnEnable()
    {
        if (instance != this) return;
        SetDrawing(true);
        Weapon.AnyShot += OnShot;
        Weapon.AnyModeSwitched += OnModeSwitched;
        RoundManager.RoundStarted += OnRoundStarted;
        RoundManager.Halftime += OnHalftime;
        Economy.MoneyChanged += OnMoneyChanged;
    }

    void OnDisable()
    {
        Weapon.AnyShot -= OnShot;
        Weapon.AnyModeSwitched -= OnModeSwitched;
        RoundManager.RoundStarted -= OnRoundStarted;
        RoundManager.Halftime -= OnHalftime;
        Economy.MoneyChanged -= OnMoneyChanged;
        if (instance == this) SetDrawing(false);
    }

    void OnDestroy()
    {
        Hook(null);
        if (instance == this) instance = null;
        if (arcTexture != null) Destroy(arcTexture);
    }

    // the old labels stop while this HUD draws the same things
    static void SetDrawing(bool on)
    {
        Active = on;
        RoundManager.HudDrawsScore = on;
        Health.HudDrawsHealth = on;
        Bhop.HudDraws = on;
    }

    void Start()
    {
        if (player == null) player = FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            enabled = false;
            return;
        }

        health = player.GetComponent<Health>();
        shoot = player.GetComponent<PlayerShoot>();
        loadout = player.GetComponent<PlayerLoadout>();
        spectator = player.GetComponent<Spectator>();
        bhop = player.GetComponent<Bhop>();
        rounds = FindAnyObjectByType<RoundManager>();
        if (player.cameraTransform != null) playerCam = player.cameraTransform.GetComponent<Camera>();
        weapon = FindPlayerWeapon();
        Hook(health);

        arcTexture = MakeArcTexture();
    }

    Weapon FindPlayerWeapon()
    {
        if (shoot != null && shoot.weapon != null) return shoot.weapon;
        PlayerHud hud = player.GetComponent<PlayerHud>();
        if (hud != null && hud.weapon != null) return hud.weapon;
        return player.GetComponentInChildren<Weapon>(true);
    }

    void Hook(Health h)
    {
        if (hooked == h) return;
        if (hooked != null) hooked.DamageTaken -= OnDamageTaken;
        hooked = h;
        if (hooked != null) hooked.DamageTaken += OnDamageTaken;
    }

    void Update()
    {
        if (rounds != null && rounds.simulation)
        {
            enabled = false;
            return;
        }

        bloom = Mathf.MoveTowards(bloom, 0f, bloomRecovery * Time.deltaTime);

        for (int i = moneyPopups.Count - 1; i >= 0; i--)
            if (Time.time - moneyPopups[i].time > moneyPopupTime) moneyPopups.RemoveAt(i);

        if (player == null) return;
        if (weapon == null) weapon = FindPlayerWeapon();
        // RoundManager adds the Bhop component at the first spawn if the player has none
        if (bhop == null) bhop = player.GetComponent<Bhop>();
    }

    void LateUpdate()
    {
        // the crosshair is drawn here: the old canvas image stays hidden (Spectator turns it on when the
        // watching ends, this runs after all Updates, before the frame is drawn)
        HideCanvasCrosshair(shoot != null ? shoot.crosshair : null);
        HideCanvasCrosshair(spectator != null ? spectator.crosshair : null);
    }

    static void HideCanvasCrosshair(GameObject go)
    {
        if (go != null && go.activeSelf) go.SetActive(false);
    }

    void OnShot(ShotInfo s)
    {
        // one shotgun shell opens the crosshair once, not once per pellet
        if (health == null || s.shooter != health || s.melee || s.pellet) return;
        float r = Mathf.Clamp(s.recoil > 0f ? s.recoil : 1f, 0.5f, 2f);
        bloom = Mathf.Min(bloom + shotBloom * r, maxShotBloom);
    }

    // right click on the Glock-18 / FAMAS: a note under the crosshair for a moment
    void OnModeSwitched(Weapon w)
    {
        if (w == null || w != weapon) return;
        modeSwitchTime = Time.time;
        modeSwitchText = w.BurstMode ? "Режим: очередь" : w.automatic ? "Режим: автоматический" : "Режим: одиночный";
    }

    void OnMoneyChanged(Health h, int change, string reason)
    {
        if (h == null || h != health || change == 0) return;

        string what = Economy.ReasonText(reason);
        string sum = (change > 0 ? "+$" : "−$") + Mathf.Abs(change);
        if (moneyPopups.Count >= 5) moneyPopups.RemoveAt(0);
        moneyPopups.Add(new MoneyPopup
        {
            text = what.Length > 0 ? sum + "  " + what : sum,
            gain = change > 0,
            time = Time.time
        });
        moneyFlashTime = Time.time;
        moneyFlashGain = change > 0;
    }

    void OnRoundStarted()
    {
        arcs.Clear();
        bloom = 0f;
    }

    // the teams have swapped sides: the banner tells the player his new side (Health.team is already the new one)
    void OnHalftime()
    {
        halftimeTime = Time.time;
        halftimeTeam = health != null ? health.team : -1;
    }

    void OnDamageTaken(float amount, Health attacker, bool headshot)
    {
        if (health == null || attacker == null || attacker == health) return;
        // a teammate's shot hurts only with friendly fire
        if (attacker.team == health.team && !Weapon.FriendlyFire) return;

        float strength = Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(amount / 40f));
        Vector3 from = attacker.transform.position;

        for (int i = 0; i < arcs.Count; i++)
        {
            if (arcs[i].attacker != attacker) continue;
            DamageArc a = arcs[i];
            a.from = from;
            a.time = Time.time;
            a.strength = Mathf.Max(strength, a.strength);
            arcs[i] = a;
            return;
        }

        if (arcs.Count >= 4) arcs.RemoveAt(0);
        arcs.Add(new DamageArc { attacker = attacker, from = from, time = Time.time, strength = strength });
    }

    // ---------------- drawing ----------------

    void OnGUI()
    {
        // under the scoreboard, the killfeed and the menus
        GUI.depth = 5;
        if (Event.current.type != EventType.Repaint) return;
        if (Time.timeScale == 0f || player == null || !player.gameObject.activeInHierarchy) return;
        // the buy menu covers the screen like in CS:GO: no HUD under it (it shows the money and the time itself)
        if (BuyMenu.IsOpen) return;

        float k = Scale;
        BuildStyles(k);

        bool dead = health != null && health.IsDead;
        // the top of the boxes at the bottom left: the money goes above them
        float leftTop = BottomBoxTop;

        if (!dead)
        {
            DrawDamageArcs(k);
            if (loadout != null && loadout.InGrenadeSlot)
            {
                DrawCrosshair(k);
                if (health != null) leftTop = DrawHealth(k, health, true);
                DrawGrenadeAmmo(k, loadout.Grenades);
            }
            else
            {
                DrawCrosshair(k);
                DrawModeSwitch(k);
                if (health != null) leftTop = DrawHealth(k, health, true);
                if (weapon != null) DrawAmmo(k, weapon);
                DrawCarriedGrenadesStrip(k);
            }
        }
        else
        {
            // like in CS: the watched teammate's health and ammo
            Health watched = Watched();
            if (watched != null)
            {
                leftTop = DrawHealth(k, watched, false);
                Weapon w = WeaponOf(watched);
                if (w != null) DrawAmmo(k, w);
            }
        }

        DrawMoney(k, leftTop, !dead);
        if (rounds != null)
        {
            // top centre, from the top down: timer and score, round message, the bots tag, the halftime banner
            float y = DrawTop(k);
            if (!rounds.IsSolo)
            {
                y = DrawBotsFrozenTag(k, y);
                y = DrawHalftimeBanner(k, y);
                DrawFreezeCountdown(k, y);
            }
        }

        GUI.color = Color.white;
    }

    void BuildStyles(float k)
    {
        if (bigStyle == null)
        {
            bigStyle = MakeStyle(FontStyle.Bold);
            timerStyle = MakeStyle(FontStyle.Bold);
            midStyle = MakeStyle(FontStyle.Bold);
            smallStyle = MakeStyle(FontStyle.Bold);
            messageStyle = MakeStyle(FontStyle.Bold);
        }

        bigStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(44f * k));
        timerStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(30f * k));
        midStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(24f * k));
        smallStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(18f * k));
        messageStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(26f * k));

        if (tinyStyle == null)
        {
            tinyStyle = MakeStyle(FontStyle.Bold);
            keyStyle = MakeStyle(FontStyle.Bold);
            hugeStyle = MakeStyle(FontStyle.Bold);
        }
        tinyStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(14f * k));
        keyStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(16f * k));
        hugeStyle.fontSize = Mathf.Max(10, Mathf.RoundToInt(52f * k));
    }

    // the camera of the dead player sits on the watched teammate (see Spectator.Watch)
    Health Watched()
    {
        Transform c = player.cameraTransform;
        if (c == null || c.parent == null) return null;
        Health h = c.parent.GetComponentInParent<Health>();
        return h != null && h != health && !h.IsDead ? h : null;
    }

    Weapon WeaponOf(Health h)
    {
        if (h != watchedCache)
        {
            watchedCache = h;
            BotController bot = h.GetComponent<BotController>();
            watchedWeapon = bot != null && bot.weapon != null ? bot.weapon : h.GetComponentInChildren<Weapon>();
        }
        return watchedWeapon;
    }

    // ---- bottom left: health ----

    // returns the y of the top edge of what it drew (the bhop box sits above the health)
    float DrawHealth(float k, Health h, bool own)
    {
        float m = Margin * k;
        float bh = BoxHeight * k;
        float w = HealthBoxWidth * k;
        Rect box = new Rect(m, Screen.height - m - bh, w, bh);
        DrawPanel(box, PanelColor, true);

        int hp = Mathf.Max(0, Mathf.CeilToInt(h.current));
        Color c = hp < 25 ? lowColor : Color.white;

        float pad = 16f * k;
        float cross = Mathf.Round(28f * k);
        float crossY = Mathf.Round(box.y + (bh - 6f * k - cross) * 0.5f);
        DrawCross(new Rect(Mathf.Round(box.x + pad), crossY, cross, cross), c);
        Text(new Rect(box.x + pad + cross + 12f * k, box.y, w, bh - 6f * k), Num(hp), bigStyle, c, TextAnchor.MiddleLeft);

        Rect bar = new Rect(box.x + pad, box.yMax - 8f * k, w - pad * 2.5f, Mathf.Max(2f, Mathf.Round(3f * k)));
        float frac = h.maxHealth > 0f ? Mathf.Clamp01(h.current / h.maxHealth) : 0f;
        Fill(bar, new Color(1f, 1f, 1f, 0.15f));
        Fill(new Rect(bar.x, bar.y, bar.width * frac, bar.height), c);

        if (own && bhop != null && bhop.level > 0) return DrawBhop(k, box);
        return box.y;
    }

    // a plus sign like the CS health icon
    static void DrawCross(Rect r, Color c)
    {
        float arm = Mathf.Round(r.width / 3f);
        float mid = Mathf.Round((r.width - arm) * 0.5f);
        Fill(new Rect(r.x + mid, r.y, arm, r.height), c);
        Fill(new Rect(r.x, r.y + mid, r.width, arm), c);
    }

    float DrawBhop(float k, Rect healthBox)
    {
        float h = 30f * k;
        Rect box = new Rect(healthBox.x, healthBox.y - 6f * k - h, healthBox.width, h);
        DrawPanel(box, PanelColor, true);

        int level = Mathf.Clamp(bhop.level, 0, bhopTexts.Length - 1);
        float max = bhop.maxBonus != null && bhop.maxBonus.Length > 0 ? bhop.maxBonus[Mathf.Clamp(level, 0, bhop.maxBonus.Length - 1)] : 0f;
        float frac = max > 0f ? Mathf.Clamp01(bhop.Bonus / max) : 0f;

        int percent = Mathf.RoundToInt(bhop.Bonus * 100f);
        if (percent != bhopPercent)
        {
            bhopPercent = percent;
            bhopPercentText = "+" + percent + "%";
        }

        float pad = 16f * k;
        Text(new Rect(box.x + pad, box.y, 80f * k, h), bhopTexts[level], smallStyle, bhopColor, TextAnchor.MiddleLeft);

        Rect bar = new Rect(box.x + 92f * k, Mathf.Round(box.center.y - 2f * k), 56f * k, Mathf.Max(2f, Mathf.Round(4f * k)));
        Fill(bar, new Color(1f, 1f, 1f, 0.15f));
        Fill(new Rect(bar.x, bar.y, bar.width * frac, bar.height), bhopColor);

        Text(new Rect(bar.xMax + 8f * k, box.y, 60f * k, h), bhopPercentText, smallStyle,
            percent > 0 ? Color.white : DimText, TextAnchor.MiddleLeft);
        return box.y;
    }

    // ---- bottom left, above the health: money and the buy hint ----

    void DrawMoney(float k, float bottom, bool alive)
    {
        bool showMoney = Economy.Enabled && health != null;
        // the same rule as BuyMenu: Economy.CanBuy (without the economy, in solo, buying never ends; otherwise
        // the buy time, the freeze time included, inside the own buy zone) in a running round
        // (the console's "buyanywhere" also between rounds). In the buy time outside the zone a dim note instead.
        bool free = !Economy.Enabled;
        bool running = free || Economy.BuyAnywhere || rounds == null || (!rounds.InBreak && !rounds.MatchOver);
        bool canBuy = alive && health != null && running && Economy.CanBuy(health);
        bool away = alive && !free && health != null && running && Economy.InBuyTime && !Economy.InBuyZone(health);
        // hidden while the buy menu or the developer console is open
        bool buy = (canBuy || away) && !BuyMenu.IsOpen && !DevConsole.IsOpen;
        bool inZone = canBuy;
        if (!showMoney && !buy) return;

        float m = Margin * k;
        float h = Mathf.Round(40f * k);
        float pad = 16f * k;
        Rect box = new Rect(m, Mathf.Round(bottom - 6f * k - h), HealthBoxWidth * k, h);
        float hintX = box.x;

        if (showMoney)
        {
            DrawPanel(box, PanelColor, true);

            int money = Economy.MoneyOf(health);
            if (money != moneyShown)
            {
                moneyShown = money;
                moneyText = money.ToString();
            }

            // the sum flashes green (got money) or red (spent it) for a moment
            float flash = Mathf.Clamp01(1f - (Time.time - moneyFlashTime) / 0.6f);
            Color sumColor = Color.Lerp(Color.white, moneyFlashGain ? moneyColor : lowColor, flash);
            float dollar = Width(midStyle, "$");
            Text(new Rect(box.x + pad, box.y, dollar, h), "$", midStyle, moneyColor, TextAnchor.MiddleLeft);
            Text(new Rect(box.x + pad + dollar + 8f * k, box.y, box.width, h), moneyText, midStyle, sumColor, TextAnchor.MiddleLeft);

            DrawMoneyPopups(k, box);
            hintX = box.xMax + 6f * k;
        }

        if (buy) DrawBuyHint(k, new Rect(hintX, box.y, 0f, h), free, inZone);
    }

    // "+$300  убийство" lines over the money box: the newest at the bottom, they float up and fade out
    void DrawMoneyPopups(float k, Rect moneyBox)
    {
        float life = Mathf.Max(0.5f, moneyPopupTime);
        float lineH = Mathf.Round(24f * k);
        int row = 0;

        for (int i = moneyPopups.Count - 1; i >= 0; i--)
        {
            MoneyPopup p = moneyPopups[i];
            float age = Time.time - p.time;
            if (age > life || age < 0f) continue;

            float alpha = age < 0.15f ? age / 0.15f : age > life - 0.7f ? (life - age) / 0.7f : 1f;
            float rise = Mathf.Min(age / 0.3f, 1f) * 6f * k;
            float y = moneyBox.y - 4f * k - (row + 1) * lineH - rise;
            Color c = p.gain ? moneyColor : lowColor;
            c.a = Mathf.Clamp01(alpha);
            Text(new Rect(moneyBox.x + 16f * k, Mathf.Round(y), moneyBox.width * 2f, lineH), p.text, smallStyle, c, TextAnchor.MiddleLeft);
            row++;
        }
    }

    // a key cap with "B" and "закупка 0:15" (no time in solo, where buying never ends);
    // outside the own buy zone a dim "вне зоны закупки" without the key cap
    void DrawBuyHint(float k, Rect r, bool free, bool inZone)
    {
        float pad = 10f * k;

        if (!inZone)
        {
            const string away = "вне зоны закупки";
            float aw = Width(smallStyle, away);
            Rect dim = new Rect(r.x, r.y, pad + aw + pad * 1.5f, r.height);
            DrawPanel(dim, new Color(PanelColor.r, PanelColor.g, PanelColor.b, PanelColor.a * 0.6f), true);
            Text(new Rect(dim.x + pad, dim.y, aw + pad, dim.height), away, smallStyle, new Color(1f, 1f, 1f, 0.45f), TextAnchor.MiddleLeft);
            return;
        }

        string text = "закупка";
        // "buyanywhere" of the console keeps buying open after the buy time: no "0:00" then
        if (!free && Economy.InBuyTime)
        {
            int s = Mathf.CeilToInt(Economy.BuyTimeLeft);
            if (s != buySeconds)
            {
                buySeconds = s;
                buyText = "закупка " + (s / 60) + ":" + (s % 60).ToString("00");
            }
            text = buyText;
        }

        float key = Mathf.Round(24f * k);
        float tw = Width(smallStyle, text);
        Rect box = new Rect(r.x, r.y, pad + key + 8f * k + tw + pad * 1.5f, r.height);
        DrawPanel(box, PanelColor, true);

        Rect cap = new Rect(Mathf.Round(box.x + pad), Mathf.Round(box.y + (box.height - key) * 0.5f), key, key);
        Fill(cap, new Color(1f, 1f, 1f, 0.9f));
        Text(cap, "B", keyStyle, new Color(0.05f, 0.05f, 0.06f, 1f), TextAnchor.MiddleCenter);
        Text(new Rect(cap.xMax + 8f * k, box.y, tw + pad, box.height), text, smallStyle, Color.white, TextAnchor.MiddleLeft);
    }

    // ---- bottom right: ammo ----

    void DrawAmmo(float k, Weapon w)
    {
        float m = Margin * k;
        float bh = BoxHeight * k;
        float bw = AmmoBoxWidth * k;
        Rect box = new Rect(Screen.width - m - bw, Screen.height - m - bh, bw, bh);
        DrawPanel(box, PanelColor, false);

        float pad = 16f * k;
        float right = box.xMax - pad;
        float nameRight = right;
        float textH = bh - 6f * k;
        // a second small line under the weapon name: the fire mode or the Zeus charge
        string sub = null;
        Color subColor = DimText;

        if (w.IsTaser)
        {
            // the Zeus: one charge and no reserve, the bar fills while it charges again
            float charge = w.ChargeProgress;
            bool ready = charge >= 1f;
            string mag = Num(w.Ammo);
            float mw = Width(bigStyle, mag);
            Text(new Rect(right - mw, box.y, mw, textH), mag, bigStyle, ready ? Color.white : DimText, TextAnchor.MiddleRight);
            nameRight = right - mw - 12f * k;

            Bar(k, box, pad, charge, ready ? moneyColor : reloadColor);
            if (ready)
            {
                sub = "ЗАРЯЖЕН";
                subColor = moneyColor;
            }
            else
            {
                int s = Mathf.CeilToInt((1f - charge) * Mathf.Max(w.rechargeTime, 0f));
                if (s != chargeSeconds)
                {
                    chargeSeconds = s;
                    chargeText = "ЗАРЯД " + s + " с";
                }
                sub = chargeText;
                subColor = reloadColor;
            }
        }
        else if (!w.IsMelee)
        {
            // shotgun shells are counted like rounds, a shell reload fills the bar shell by shell
            int ammo = w.Ammo;
            bool reloading = w.IsReloading;
            string reserve = Weapon.InfiniteAmmo ? "/ ∞" : "/ " + Num(w.Reserve);
            string mag = Num(ammo);

            float rw = Width(midStyle, reserve);
            float mw = Width(bigStyle, mag);

            bool low = !Weapon.InfiniteAmmo && ammo <= Mathf.Max(1, Mathf.CeilToInt(w.magazineSize * 0.2f));
            Color magColor = reloading ? DimText : low ? lowColor : Color.white;

            // the small reserve text sits on the same baseline as the big number
            Text(new Rect(right - rw, box.y + 7f * k, rw, textH), reserve, midStyle, DimText, TextAnchor.MiddleRight);
            Text(new Rect(right - rw - 8f * k - mw, box.y, mw, textH), mag, bigStyle, magColor, TextAnchor.MiddleRight);
            nameRight = right - rw - 8f * k - mw - 12f * k;

            if (reloading) Bar(k, box, pad, w.ReloadProgress, reloadColor);

            // Glock-18 / FAMAS: the right mouse button switches the burst mode
            if (w.HasBurstMode)
            {
                sub = w.BurstMode ? "ОЧЕРЕДЬ" : w.automatic ? "АВТО" : "ПОЛУАВТО";
                subColor = w.BurstMode ? reloadColor : DimText;
            }
        }

        float nameX = box.x + pad;
        float nameW = Mathf.Max(0f, nameRight - nameX);
        bool hasName = !string.IsNullOrEmpty(w.weaponName);
        if (sub == null)
        {
            if (hasName) Text(new Rect(nameX, box.y, nameW, textH), w.weaponName, smallStyle, DimText, TextAnchor.MiddleLeft);
        }
        else
        {
            // two lines: the name over the middle, the mode under it
            float half = Mathf.Round(textH * 0.5f);
            if (hasName) Text(new Rect(nameX, box.y + 2f * k, nameW, half), w.weaponName, smallStyle, DimText, TextAnchor.LowerLeft);
            Text(new Rect(nameX, box.y + half + 4f * k, nameW, half - 4f * k), sub, tinyStyle, subColor, TextAnchor.UpperLeft);
        }
    }

    readonly Dictionary<string, Texture2D> grenadeIcons = new Dictionary<string, Texture2D>();

    Texture2D GrenadeIcon(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!grenadeIcons.TryGetValue(name, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + name);
            grenadeIcons[name] = tex;
        }
        return tex;
    }

    void DrawCarriedGrenadesStrip(float k)
    {
        if (loadout == null || loadout.Grenades == null || !loadout.Grenades.HasAny) return;
        float m = Margin * k;
        float bh = BoxHeight * k;
        float bw = AmmoBoxWidth * k;
        float ih = Mathf.Round(24f * k);
        float x = Screen.width - m - bw;
        float y = Screen.height - m - bh - ih - 8f * k;
        // Draw small icons right-aligned just above the ammo box when slot list is hidden
        float right = Screen.width - m - 8f * k;
        for (int i = loadout.Grenades.Items.Count - 1; i >= 0; i--)
        {
            GrenadeData gd = loadout.Grenades.Items[i];
            if (gd == null) continue;
            Texture2D icon = GrenadeIcon(gd.grenadeName);
            if (icon == null || icon.height <= 0) continue;
            float iw = Mathf.Round(ih * icon.width / icon.height);
            GUI.color = new Color(1f, 1f, 1f, 0.72f);
            GUI.DrawTexture(new Rect(right - iw, y, iw, ih), icon);
            right -= iw + 8f * k;
        }
        GUI.color = Color.white;
        if (right < x) { }
    }

    void DrawGrenadeAmmo(float k, PlayerGrenades pg)
    {
        if (pg == null || pg.Selected == null) return;
        GrenadeData g = pg.Selected;

        float m = Margin * k;
        float bh = BoxHeight * k;
        float bw = AmmoBoxWidth * k;
        Rect box = new Rect(Screen.width - m - bw, Screen.height - m - bh, bw, bh);
        DrawPanel(box, PanelColor, false);

        float pad = 16f * k;
        float textH = bh - 6f * k;
        float right = box.xMax - pad;

        string countStr = Weapon.InfiniteAmmo ? "∞" : pg.Count + "/" + PlayerGrenades.MaxTotal;
        float cw = Width(midStyle, countStr);
        Text(new Rect(right - cw, box.y + 7f * k, cw, textH), countStr, midStyle, DimText, TextAnchor.MiddleRight);

        float iconRight = right - cw - 10f * k;
        for (int i = pg.Items.Count - 1; i >= 0; i--)
        {
            GrenadeData item = pg.Items[i];
            if (item == null) continue;
            Texture2D ic = GrenadeIcon(item.grenadeName);
            if (ic == null || ic.height <= 0) continue;
            float ih = Mathf.Round(24f * k);
            float iw = Mathf.Round(ih * ic.width / ic.height);
            GUI.color = item == g ? reloadColor : new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(iconRight - iw, Mathf.Round(box.center.y - ih * 0.5f), iw, ih), ic);
            iconRight -= iw + 6f * k;
        }
        GUI.color = Color.white;

        float nameX = box.x + pad;
        float nameW = Mathf.Max(0f, iconRight - nameX - 6f * k);
        float half = Mathf.Round(textH * 0.5f);
        Text(new Rect(nameX, box.y + 2f * k, nameW, half), g.displayName, smallStyle, Color.white, TextAnchor.LowerLeft);

        string sub = "ЛКМ СИЛЬНЫЙ · ЛКМ+ПКМ СРЕДН. · ПКМ СЛАБЫЙ";
        Color subCol = DimText;
        if (pg.State == PlayerGrenades.ThrowState.Pulling || pg.State == PlayerGrenades.ThrowState.Holding)
        {
            sub = pg.CurrentStrength == ThrowStrength.High ? "БРОСОК: СИЛЬНЫЙ (ЛКМ)"
                : pg.CurrentStrength == ThrowStrength.Mid ? "БРОСОК: СРЕДНИЙ (ЛКМ+ПКМ)"
                : "БРОСОК: СЛАБЫЙ (ПКМ)";
            subCol = reloadColor;
        }
        Text(new Rect(nameX, box.y + half + 4f * k, nameW + 100f * k, half - 4f * k), sub, tinyStyle, subCol, TextAnchor.UpperLeft);
    }

    // a thin bar along the bottom of a box (reload, Zeus charge)
    void Bar(float k, Rect box, float pad, float t, Color c)
    {
        Rect bar = new Rect(box.x + pad, box.yMax - 8f * k, box.width - pad * 2f, Mathf.Max(2f, Mathf.Round(3f * k)));
        Fill(bar, new Color(1f, 1f, 1f, 0.15f));
        Fill(new Rect(bar.x, bar.y, bar.width * Mathf.Clamp01(t), bar.height), c);
    }

    // ---- top centre: timer, score, players, message ----

    // returns the y under what it drew
    float DrawTop(float k)
    {
        float cx = Mathf.Round(Screen.width * 0.5f);
        float top = Mathf.Round(10f * k);
        float h = Mathf.Round(46f * k);
        float tw = Mathf.Round(112f * k);
        Rect timerBox = new Rect(cx - tw * 0.5f, top, tw, h);
        Color solid = new Color(0.03f, 0.04f, 0.05f, 0.72f);

        if (rounds.IsSolo)
        {
            Fill(timerBox, solid);
            Text(timerBox, "SOLO", timerStyle, Color.white, TextAnchor.MiddleCenter);
            return DrawMessage(k, cx, timerBox.yMax + 8f * k);
        }

        // timer m:ss, red in the last 10 seconds, grey between rounds; in the freeze time it counts the freeze down (blue)
        bool freeze = RoundManager.InFreezeTime && !rounds.MatchOver;
        int seconds = freeze ? Mathf.CeilToInt(Mathf.Max(0f, RoundManager.FreezeTimeLeft)) : Mathf.CeilToInt(rounds.TimeLeft);
        if (seconds != timerValue)
        {
            timerValue = seconds;
            timerText = (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }
        bool idle = rounds.InBreak || rounds.MatchOver;
        Color timerColor = freeze ? freezeColor : idle ? DimText : seconds <= 10 ? lowColor : Color.white;
        Fill(timerBox, solid);
        Text(timerBox, timerText, timerStyle, timerColor, TextAnchor.MiddleCenter);

        float sw = Mathf.Round(64f * k);
        float gap = Mathf.Max(2f, Mathf.Round(4f * k));
        Rect tBox = new Rect(timerBox.x - gap - sw, top, sw, h);
        Rect ctBox = new Rect(timerBox.xMax + gap, top, sw, h);
        int s0 = rounds.score != null && rounds.score.Length > 0 ? rounds.score[0] : 0;
        int s1 = rounds.score != null && rounds.score.Length > 1 ? rounds.score[1] : 0;
        DrawScore(k, tBox, s0, tColor, solid);
        DrawScore(k, ctBox, s1, ctColor, solid);

        CollectTeams();
        DrawPlayers(k, team0, tBox.x - gap, top, h, false, tColor, solid);
        DrawPlayers(k, team1, ctBox.xMax + gap, top, h, true, ctColor, solid);

        return DrawMessage(k, cx, top + h + 8f * k);
    }

    void DrawScore(float k, Rect box, int score, Color team, Color back)
    {
        Fill(box, back);
        Fill(new Rect(box.x, box.yMax - Mathf.Max(2f, Mathf.Round(3f * k)), box.width, Mathf.Max(2f, Mathf.Round(3f * k))), team);
        Text(box, Num(score), timerStyle, team, TextAnchor.MiddleCenter);
    }

    void CollectTeams()
    {
        team0.Clear();
        team1.Clear();
        foreach (Health h in Health.All)
        {
            if (h == null || h.invulnerable) continue;   // the training target is not a player
            if (h.team == 0) team0.Add(h);
            else if (h.team == 1) team1.Add(h);
        }
    }

    // a row of squares next to the score: bright = alive, dark = dead; the player's own square has a white frame
    void DrawPlayers(float k, List<Health> list, float edge, float top, float h, bool toRight, Color team, Color back)
    {
        int n = list.Count;
        if (n == 0) return;

        float pad = Mathf.Round(6f * k);
        float gap = Mathf.Max(1f, Mathf.Round(4f * k));
        float maxRow = 120f * k;
        float sq = Mathf.Floor(Mathf.Min(16f * k, (maxRow - gap * (n - 1)) / n));
        sq = Mathf.Max(3f, sq);
        float rowW = n * sq + (n - 1) * gap + pad * 2f;

        Rect box = toRight ? new Rect(edge, top, rowW, h) : new Rect(edge - rowW, top, rowW, h);
        Fill(box, new Color(back.r, back.g, back.b, back.a * 0.75f));

        float y = Mathf.Round(top + (h - sq) * 0.5f);
        float frame = Mathf.Max(1f, Mathf.Round(2f * k));

        for (int i = 0; i < n; i++)
        {
            // the first square is the one next to the score
            float x = toRight ? box.x + pad + i * (sq + gap) : box.xMax - pad - (i + 1) * sq - i * gap;
            Rect r = new Rect(Mathf.Round(x), y, sq, sq);
            Health p = list[i];
            bool alive = !p.IsDead && p.gameObject.activeInHierarchy;

            if (alive)
            {
                Fill(r, team);
            }
            else
            {
                Fill(r, new Color(0.16f, 0.16f, 0.17f, 0.85f));
                DrawFrame(r, 1f, new Color(1f, 1f, 1f, 0.15f));
            }

            if (p == health) DrawFrame(r, frame, alive ? Color.white : new Color(1f, 1f, 1f, 0.5f));
        }
    }

    // returns the y under the message (y itself when there is none)
    float DrawMessage(float k, float cx, float y)
    {
        string msg = rounds.Message ?? "";

        if (msg != lastMessage)
        {
            if (msg.Length > 0 && lastMessage.Length == 0 && rounds.score != null && rounds.score.Length > 1)
                bannerTeam = rounds.score[0] > lastScore0 ? 0 : rounds.score[1] > lastScore1 ? 1 : -1;
            lastMessage = msg;
        }
        if (msg.Length == 0)
        {
            bannerTeam = -1;
            if (rounds.score != null && rounds.score.Length > 1)
            {
                lastScore0 = rounds.score[0];
                lastScore1 = rounds.score[1];
            }
            return y;
        }

        tempContent.text = msg;
        Vector2 size = messageStyle.CalcSize(tempContent);
        float w = Mathf.Min(size.x + 48f * k, Screen.width - 40f * k);
        float h = size.y + 16f * k;
        Rect r = new Rect(Mathf.Round(cx - w * 0.5f), Mathf.Round(y), Mathf.Round(w), Mathf.Round(h));

        Fill(r, new Color(0.03f, 0.04f, 0.05f, 0.75f));
        if (bannerTeam >= 0)
            Fill(new Rect(r.x, r.y, r.width, Mathf.Max(2f, Mathf.Round(3f * k))), bannerTeam == 0 ? tColor : ctColor);
        Text(r, msg, messageStyle, Color.white, TextAnchor.MiddleCenter);
        return r.yMax + 8f * k;
    }

    // a small "Боты заморожены" pill while the bots stand still (main menu option); returns the y under it
    float DrawBotsFrozenTag(float k, float y)
    {
        if (!RoundManager.BotsFrozen) return y;

        const string text = "Боты заморожены";
        float h = Mathf.Round(26f * k);
        float w = Mathf.Round(Width(smallStyle, text) + 28f * k);
        Rect r = new Rect(Mathf.Round(Screen.width * 0.5f - w * 0.5f), Mathf.Round(y), w, h);
        Fill(r, new Color(0.03f, 0.04f, 0.05f, 0.6f));
        DrawFrame(r, 1f, new Color(freezeColor.r, freezeColor.g, freezeColor.b, 0.45f));
        Text(r, text, smallStyle, freezeColor, TextAnchor.MiddleCenter);
        return r.yMax + 8f * k;
    }

    // "СМЕНА СТОРОН" and "Теперь ты за CT" for a few seconds after the halftime; returns the y under it
    float DrawHalftimeBanner(float k, float y)
    {
        float life = Mathf.Max(0.5f, halftimeBannerTime);
        float age = Time.time - halftimeTime;
        if (age < 0f || age > life) return y;

        float alpha = age < 0.2f ? age / 0.2f : age > life - 0.8f ? (life - age) / 0.8f : 1f;
        alpha = Mathf.Clamp01(alpha);

        int team = halftimeTeam;
        Color side = team == 0 ? tColor : team == 1 ? ctColor : Color.white;
        string sub = team == 0 ? "Теперь ты за террористов (T)" : team == 1 ? "Теперь ты за спецназ (CT)" : "";

        float w = Mathf.Min(Mathf.Round(560f * k), Screen.width - 40f * k);
        float h = Mathf.Round((sub.Length > 0 ? 96f : 66f) * k);
        Rect r = new Rect(Mathf.Round(Screen.width * 0.5f - w * 0.5f), Mathf.Round(y + 8f * k), w, h);

        Fill(r, new Color(0.03f, 0.04f, 0.05f, 0.8f * alpha));
        // red and blue halves on top, swapped: the sides have changed
        float line = Mathf.Max(2f, Mathf.Round(4f * k));
        Fill(new Rect(r.x, r.y, r.width * 0.5f, line), new Color(ctColor.r, ctColor.g, ctColor.b, alpha));
        Fill(new Rect(r.x + r.width * 0.5f, r.y, r.width * 0.5f, line), new Color(tColor.r, tColor.g, tColor.b, alpha));

        Text(new Rect(r.x, r.y + 8f * k, r.width, 52f * k), "СМЕНА СТОРОН", bigStyle, new Color(1f, 1f, 1f, alpha), TextAnchor.MiddleCenter);
        if (sub.Length > 0)
            Text(new Rect(r.x, r.y + 58f * k, r.width, 30f * k), sub, midStyle, new Color(side.r, side.g, side.b, alpha), TextAnchor.MiddleCenter);
        return r.yMax + 8f * k;
    }

    // "Раунд начнётся через 3" in the freeze time at the round start (not over the buy menu, the timer shows it too)
    void DrawFreezeCountdown(float k, float y)
    {
        if (!RoundManager.InFreezeTime || rounds.MatchOver || BuyMenu.IsOpen) return;

        int s = Mathf.Max(1, Mathf.CeilToInt(RoundManager.FreezeTimeLeft));
        if (s != freezeSeconds)
        {
            freezeSeconds = s;
            freezeText = "Раунд начнётся через " + s;
        }

        // a little pulse on every new second
        float frac = RoundManager.FreezeTimeLeft - Mathf.Floor(RoundManager.FreezeTimeLeft);
        float pulse = 1f + 0.06f * Smooth(0.75f, 1f, frac);

        float top = Mathf.Max(y + 12f * k, Mathf.Round(Screen.height * 0.26f));
        float h = Mathf.Round(70f * k);
        Rect r = new Rect(0f, top, Screen.width, h);

        Matrix4x4 old = GUI.matrix;
        GUIUtility.ScaleAroundPivot(new Vector2(pulse, pulse), r.center);
        Text(r, freezeText, hugeStyle, Color.white, TextAnchor.MiddleCenter);
        GUI.matrix = old;

        if (Economy.Enabled && Economy.CanBuy(health))
            Text(new Rect(0f, r.yMax, Screen.width, Mathf.Round(30f * k)), "Закупайтесь: B — меню закупки", smallStyle,
                freezeColor, TextAnchor.MiddleCenter);
    }

    // ---- centre: crosshair, fire mode note and damage arcs ----

    void DrawCrosshair(float k)
    {
        if (weapon == null) return;
        if (shoot != null && shoot.IsScoped) return;
        // sniper rifles have no crosshair, only the scope (the SG 553 / AUG keep it until they zoom)
        if (weapon.HasSniperScope) return;
        if (GameSettings.CrosshairStyle == CrosshairStyle.None) return;

        // the dynamic crosshair opens up to the real spread of the next shot (stance, burst mode, shotgun pellets)
        // and a little more after every shot; the bullet goes up to that many degrees off,
        // turned into pixels with the field of view of the camera
        float spreadPx = 0f;
        if (GameSettings.CrosshairDynamic)
        {
            float spreadDeg = Mathf.Clamp(weapon.CurrentSpread, 0f, 30f);
            float vfov = playerCam != null ? playerCam.fieldOfView : 60f;
            spreadPx = Screen.height * 0.5f * Mathf.Tan(spreadDeg * Mathf.Deg2Rad) / Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad)
                       + bloom * k;
        }

        DrawCrosshairShape(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f), k, spreadPx, GameSettings.CrosshairColorValue);
    }

    static readonly Rect[] crossPieces = new Rect[5];

    // the crosshair of the settings (style, size, gap, thickness, length, outline) around the point c;
    // k = the screen scale, spreadPx = the extra gap of the dynamic crosshair (ignored when it is off).
    // MainMenu draws the live preview of the settings with it.
    public static void DrawCrosshairShape(Vector2 c, float k, float spreadPx, Color color)
    {
        CrosshairStyle style = GameSettings.CrosshairStyle;
        if (style == CrosshairStyle.None) return;

        float size = Mathf.Clamp(GameSettings.CrosshairSize, GameSettings.MinCrosshairSize, GameSettings.MaxCrosshairSize);
        float len = Mathf.Max(1f, Mathf.Round(GameSettings.CrosshairLength * k * size));
        float thick = Mathf.Max(1f, Mathf.Round(GameSettings.CrosshairThickness * k * Mathf.Sqrt(size)));
        float extra = GameSettings.CrosshairDynamic ? Mathf.Max(0f, spreadPx) : 0f;
        float gap = Mathf.Min(Mathf.Round(GameSettings.CrosshairGap * k * size + extra), Screen.height * 0.2f);
        float outline = GameSettings.CrosshairOutline ? Mathf.Max(1f, Mathf.Round(k)) : 0f;
        Color dark = new Color(0f, 0f, 0f, 0.6f * color.a);

        // the lines are thick pixels wide around the centre pixel row and column
        float cx = Mathf.Floor(c.x);
        float cy = Mathf.Floor(c.y);
        float half = Mathf.Floor(thick * 0.5f);
        float x0 = cx - half, y0 = cy - half;   // the top left corner of the centre square

        if (style == CrosshairStyle.Circle)
        {
            // a ring where the lines would be, and the dot
            Vector2 mid = new Vector2(x0 + thick * 0.5f, y0 + thick * 0.5f);
            float radius = Mathf.Max(gap + thick * 0.5f + len * 0.5f, thick * 2f);
            if (outline > 0f) DrawRing(mid, radius, thick * 0.5f + outline, dark);
            DrawRing(mid, radius, thick * 0.5f, color);
        }

        int n = 0;
        switch (style)
        {
            case CrosshairStyle.Cross:
                // one long horizontal and one vertical line, no gap in the middle
                crossPieces[n++] = new Rect(x0 - len, y0, len * 2f + thick, thick);
                crossPieces[n++] = new Rect(x0, y0 - len, thick, len * 2f + thick);
                break;
            case CrosshairStyle.Classic:
            case CrosshairStyle.LinesDot:
            case CrosshairStyle.TShape:
                crossPieces[n++] = new Rect(x0 + thick + gap, y0, len, thick);   // right
                crossPieces[n++] = new Rect(x0 - gap - len, y0, len, thick);     // left
                crossPieces[n++] = new Rect(x0, y0 + thick + gap, thick, len);   // down
                if (style != CrosshairStyle.TShape)
                    crossPieces[n++] = new Rect(x0, y0 - gap - len, thick, len); // up
                if (style == CrosshairStyle.LinesDot) crossPieces[n++] = new Rect(x0, y0, thick, thick);
                break;
            case CrosshairStyle.Dot:
                // alone the dot is a bit bigger than the line thickness, so it is easy to see
                float grow = Mathf.Max(1f, Mathf.Round(k * size));
                crossPieces[n++] = new Rect(x0 - grow, y0 - grow, thick + grow * 2f, thick + grow * 2f);
                break;
            case CrosshairStyle.Circle:
                crossPieces[n++] = new Rect(x0, y0, thick, thick);
                break;
        }

        // the dark outline first, then the lines over it
        if (outline > 0f)
            for (int i = 0; i < n; i++) Fill(Grow(crossPieces[i], outline), dark);
        for (int i = 0; i < n; i++) Fill(crossPieces[i], color);
    }

    // ring textures of the Circle crosshair: a few slots, a slot is redrawn only when its ring changes
    class RingSlot
    {
        public Texture2D tex;      // allocated once with room to spare (cap x cap), the ring uses its bottom-left size x size
        public Color32[] pixels;
        public int key;
        public int size;
        public int used;
    }
    static readonly RingSlot[] ringSlots = new RingSlot[4];
    static int ringUse;

    // a smooth ring around c: radius to the middle of the line, halfWidth = half of the line width (pixels)
    static void DrawRing(Vector2 c, float radius, float halfWidth, Color color)
    {
        float outer = radius + halfWidth + 1f;
        float left = Mathf.Floor(c.x - outer);
        float top = Mathf.Floor(c.y - outer);
        // sizes go in steps of 8 pixels, so a growing ring reuses its texture
        int size = Mathf.CeilToInt(Mathf.Max(c.x + outer - left, c.y + outer - top));
        size = Mathf.Max(8, (size + 7) / 8 * 8);
        float fx = c.x - left, fy = c.y - top;   // the centre inside the texture

        int rq = Mathf.RoundToInt(radius * 4f);
        int hq = Mathf.Clamp(Mathf.RoundToInt(halfWidth * 4f), 0, 255);
        int px = Mathf.RoundToInt((c.x - Mathf.Floor(c.x)) * 2f);
        int py = Mathf.RoundToInt((c.y - Mathf.Floor(c.y)) * 2f);
        int key = ((rq * 256 + hq) * 3 + px) * 3 + py;

        // the slot with this ring, else an empty one, else the one used longest ago
        RingSlot slot = null;
        int free = -1;
        int oldest = -1;
        for (int i = 0; i < ringSlots.Length; i++)
        {
            RingSlot s = ringSlots[i];
            if (s == null || s.tex == null)
            {
                if (free < 0) free = i;
                continue;
            }
            if (s.key == key && s.size == size)
            {
                slot = s;
                break;
            }
            if (oldest < 0 || s.used < ringSlots[oldest].used) oldest = i;
        }

        if (slot == null)
        {
            int index = free >= 0 ? free : Mathf.Max(0, oldest);
            slot = ringSlots[index] ?? new RingSlot();
            ringSlots[index] = slot;
            // a dynamic ring changes its size often: the texture only grows (in steps of 64 px), it is never made smaller
            if (slot.tex == null || slot.tex.width < size)
            {
                if (slot.tex != null) Destroy(slot.tex);
                int cap = (size + 63) / 64 * 64;
                slot.tex = new Texture2D(cap, cap, TextureFormat.RGBA32, false);
                slot.tex.wrapMode = TextureWrapMode.Clamp;
                slot.tex.filterMode = FilterMode.Point;
                slot.tex.hideFlags = HideFlags.HideAndDontSave;
                slot.pixels = new Color32[cap * cap];
            }
            slot.key = key;
            slot.size = size;

            int stride = slot.tex.width;
            System.Array.Clear(slot.pixels, 0, slot.pixels.Length);
            // only the pixels near the ring: per row, the span between the outer and the inner circle
            float ro = radius + halfWidth + 1f;
            float ri = radius - halfWidth - 1f;
            for (int y = 0; y < size; y++)
            {
                // texture row 0 is the bottom of the rect
                float gy = size - y - 0.5f - fy;
                if (Mathf.Abs(gy) > ro) continue;
                float xo = Mathf.Sqrt(ro * ro - gy * gy);
                float xi = ri > 0f && Mathf.Abs(gy) < ri ? Mathf.Sqrt(ri * ri - gy * gy) : -1f;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(fx - xo - 0.5f));
                int x1 = Mathf.Min(size - 1, Mathf.CeilToInt(fx + xo - 0.5f));
                for (int x = x0; x <= x1; x++)
                {
                    float gx = x + 0.5f - fx;
                    if (Mathf.Abs(gx) < xi) continue;
                    float d = Mathf.Sqrt(gx * gx + gy * gy);
                    float a = Mathf.Clamp01(halfWidth + 0.5f - Mathf.Abs(d - radius));
                    if (a > 0f) slot.pixels[y * stride + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }
            slot.tex.SetPixels32(slot.pixels);
            slot.tex.Apply(false, false);
        }

        slot.used = ++ringUse;
        GUI.color = color;
        float uv = slot.size / (float)slot.tex.width;
        GUI.DrawTextureWithTexCoords(new Rect(left, top, size, size), slot.tex, new Rect(0f, 0f, uv, uv));
        GUI.color = Color.white;
    }

    // "Режим: очередь" under the crosshair for a moment after the right click on the Glock-18 / FAMAS
    void DrawModeSwitch(float k)
    {
        float age = Time.time - modeSwitchTime;
        if (age < 0f || age > 1.5f || modeSwitchText.Length == 0) return;
        float alpha = age < 1f ? 1f : 1f - (age - 1f) / 0.5f;
        Rect r = new Rect(0f, Mathf.Round(Screen.height * 0.5f + 70f * k), Screen.width, Mathf.Round(30f * k));
        Text(r, modeSwitchText, smallStyle, new Color(1f, 1f, 1f, alpha), TextAnchor.MiddleCenter);
    }

    void DrawDamageArcs(float k)
    {
        if (arcs.Count == 0 || arcTexture == null) return;

        Vector3 forward = player.transform.forward;
        forward.y = 0f;
        Vector3 pos = player.transform.position;

        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        float radius = damageArcRadius * k;
        float halfW = Mathf.Sin(ArcHalfAngle * Mathf.Deg2Rad);
        float bottom = ArcInner * Mathf.Cos(ArcHalfAngle * Mathf.Deg2Rad);
        // the arc pointing up (straight ahead); it is turned around the screen centre for each attacker
        Rect r = new Rect(cx - halfW * radius, cy - radius, halfW * 2f * radius, (1f - bottom) * radius);

        Matrix4x4 old = GUI.matrix;
        float life = Mathf.Max(0.05f, damageArcTime);

        for (int i = arcs.Count - 1; i >= 0; i--)
        {
            float age = Time.time - arcs[i].time;
            if (age > life)
            {
                arcs.RemoveAt(i);
                continue;
            }

            Vector3 d = arcs[i].from - pos;
            d.y = 0f;
            if (d.sqrMagnitude < 0.01f || forward.sqrMagnitude < 0.0001f) continue;

            float angle = Vector3.SignedAngle(forward, d, Vector3.up);
            float fade = age < life * 0.5f ? 1f : 1f - (age - life * 0.5f) / (life * 0.5f);

            GUI.matrix = old;
            GUIUtility.RotateAroundPivot(angle, new Vector2(cx, cy));
            GUI.color = new Color(1f, 1f, 1f, fade * arcs[i].strength);
            GUI.DrawTexture(r, arcTexture);
        }

        GUI.matrix = old;
        GUI.color = Color.white;
    }

    // a red ring piece with soft edges: brighter at the outer edge and in the middle
    static Texture2D MakeArcTexture()
    {
        float halfW = Mathf.Sin(ArcHalfAngle * Mathf.Deg2Rad);
        float bottom = ArcInner * Mathf.Cos(ArcHalfAngle * Mathf.Deg2Rad);
        const int w = 256;
        int h = Mathf.Max(8, Mathf.CeilToInt(w * (1f - bottom) / (2f * halfW)));

        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        Color32[] px = new Color32[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // texture row 0 is the bottom of the rect, the side nearer to the screen centre
                float ux = Mathf.Lerp(-halfW, halfW, (x + 0.5f) / w);
                float uy = Mathf.Lerp(bottom, 1f, (y + 0.5f) / h);
                float rad = Mathf.Sqrt(ux * ux + uy * uy);
                float ang = Mathf.Abs(Mathf.Atan2(ux, uy) * Mathf.Rad2Deg) / ArcHalfAngle;

                float a = 0f;
                if (rad >= ArcInner && rad <= 1f && ang <= 1f)
                {
                    float t = Mathf.InverseLerp(ArcInner, 1f, rad);
                    float edges = Smooth(0f, 0.2f, t) * Smooth(0f, 0.12f, 1f - t);
                    float sides = 1f - Smooth(0.5f, 1f, ang);
                    a = edges * sides * Mathf.Lerp(0.55f, 1f, t);
                }
                px[y * w + x] = new Color32(235, 32, 26, (byte)Mathf.RoundToInt(a * 255f));
            }
        }

        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    // ---------------- shared helpers (PlayerLoadout and Spectator use them too) ----------------

    public static GUIStyle MakeStyle(FontStyle fontStyle)
    {
        GUIStyle s = new GUIStyle(GUI.skin.label);
        s.fontStyle = fontStyle;
        s.wordWrap = false;
        s.clipping = TextClipping.Overflow;
        s.richText = false;
        s.padding = new RectOffset(0, 0, 0, 0);
        s.normal.textColor = Color.white;
        return s;
    }

    // a text with a soft dark shadow, readable on any background
    public static void Text(Rect r, string text, GUIStyle style, Color color, TextAnchor anchor)
    {
        style.alignment = anchor;
        float o = Mathf.Max(1f, Mathf.Round(Scale * 1.5f));

        GUI.color = Color.white;
        style.normal.textColor = new Color(0f, 0f, 0f, 0.55f * color.a);
        GUI.Label(new Rect(r.x + o, r.y + o, r.width, r.height), text, style);
        style.normal.textColor = color;
        GUI.Label(r, text, style);
    }

    public static void Fill(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    // a frame of the given thickness along the inner edge of the rect
    public static void DrawFrame(Rect r, float thick, Color c)
    {
        Fill(new Rect(r.x, r.y, r.width, thick), c);
        Fill(new Rect(r.x, r.yMax - thick, r.width, thick), c);
        Fill(new Rect(r.x, r.y + thick, thick, r.height - thick * 2f), c);
        Fill(new Rect(r.xMax - thick, r.y + thick, thick, r.height - thick * 2f), c);
    }

    // a dark box that fades out towards the middle of the screen, like the CS2 bottom panels;
    // opaqueLeft: the left side is the dark one (boxes on the left of the screen)
    public static void DrawPanel(Rect r, Color c, bool opaqueLeft)
    {
        if (fadeTexture == null) fadeTexture = MakeFadeTexture();
        GUI.color = c;
        if (opaqueLeft) GUI.DrawTexture(r, fadeTexture);
        else GUI.DrawTextureWithTexCoords(r, fadeTexture, new Rect(1f, 0f, -1f, 1f));
        GUI.color = Color.white;
    }

    static Texture2D MakeFadeTexture()
    {
        const int n = 64;
        Texture2D tex = new Texture2D(n, 1, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.HideAndDontSave;
        for (int x = 0; x < n; x++)
        {
            float t = x / (n - 1f);
            tex.SetPixel(x, 0, new Color(1f, 1f, 1f, Mathf.Lerp(1f, 0.35f, Smooth(0.4f, 1f, t))));
        }
        tex.Apply(false, true);
        return tex;
    }

    // cached number texts (the HUD draws them every frame)
    public static string Num(int n)
    {
        if (n >= 0 && n < numberCache.Length) return numberCache[n] ??= n.ToString();
        return n.ToString();
    }

    public static Vector2 Measure(GUIStyle style, string text)
    {
        tempContent.text = text;
        return style.CalcSize(tempContent);
    }

    public static float Width(GUIStyle style, string text)
    {
        return Measure(style, text).x;
    }

    static Rect Grow(Rect r, float d)
    {
        return new Rect(r.x - d, r.y - d, r.width + d * 2f, r.height + d * 2f);
    }

    static float Smooth(float from, float to, float x)
    {
        float t = Mathf.Clamp01((x - from) / (to - from));
        return t * t * (3f - 2f * t);
    }
}
