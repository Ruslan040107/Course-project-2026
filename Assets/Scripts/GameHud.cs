using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// CS2-like HUD of the human player (IMGUI). Sizes scale with the screen height (made for 1080p).
// - Bottom left: health with a cross (red below 25) and, with the bhop upgrade, the speed bonus.
// - Bottom right: ammo, weapon name and the reload bar. PlayerLoadout draws the weapon slots above it
//   for a moment after a switch. While the dead player watches a teammate, that teammate's health and ammo are shown.
// - Top centre: round timer, team scores (T red on the left, CT blue on the right), one square per player
//   (bright = alive, dark = dead), the round message under it, "SOLO" in solo mode.
// - Centre: a dynamic crosshair (the gap shows the real spread of the next shot) and red arcs pointing
//   at whoever has just hurt the player.
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

    [Header("Crosshair (pixels at 1080p, before the size setting)")]
    public float crosshairLength = 10f;
    public float crosshairThickness = 2f;
    public float crosshairBaseGap = 4f;
    [Tooltip("extra gap for every shot, it closes again quickly")] public float shotBloom = 4f;
    public float maxShotBloom = 14f;
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

    // ---------------- bootstrap ----------------

    // Play mode starts without a domain reload: no flags left over from the last play in the editor
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
        SetDrawing(false);
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
        RoundManager.RoundStarted += OnRoundStarted;
    }

    void OnDisable()
    {
        Weapon.AnyShot -= OnShot;
        RoundManager.RoundStarted -= OnRoundStarted;
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
        if (health == null || s.shooter != health || s.melee) return;
        float r = Mathf.Clamp(s.recoil > 0f ? s.recoil : 1f, 0.5f, 2f);
        bloom = Mathf.Min(bloom + shotBloom * r, maxShotBloom);
    }

    void OnRoundStarted()
    {
        arcs.Clear();
        bloom = 0f;
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

        float k = Scale;
        BuildStyles(k);

        bool dead = health != null && health.IsDead;

        if (!dead)
        {
            DrawDamageArcs(k);
            DrawCrosshair(k);
            if (health != null) DrawHealth(k, health, true);
            if (weapon != null) DrawAmmo(k, weapon);
        }
        else
        {
            // like in CS: the watched teammate's health and ammo
            Health watched = Watched();
            if (watched != null)
            {
                DrawHealth(k, watched, false);
                Weapon w = WeaponOf(watched);
                if (w != null) DrawAmmo(k, w);
            }
        }

        if (rounds != null) DrawTop(k);

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

    void DrawHealth(float k, Health h, bool own)
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

        if (own && bhop != null && bhop.level > 0) DrawBhop(k, box);
    }

    // a plus sign like the CS health icon
    static void DrawCross(Rect r, Color c)
    {
        float arm = Mathf.Round(r.width / 3f);
        float mid = Mathf.Round((r.width - arm) * 0.5f);
        Fill(new Rect(r.x + mid, r.y, arm, r.height), c);
        Fill(new Rect(r.x, r.y + mid, r.width, arm), c);
    }

    void DrawBhop(float k, Rect healthBox)
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

        if (!w.IsMelee)
        {
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

            if (reloading)
            {
                Rect bar = new Rect(box.x + pad, box.yMax - 8f * k, bw - pad * 2f, Mathf.Max(2f, Mathf.Round(3f * k)));
                Fill(bar, new Color(1f, 1f, 1f, 0.15f));
                Fill(new Rect(bar.x, bar.y, bar.width * w.ReloadProgress, bar.height), reloadColor);
            }
        }

        if (!string.IsNullOrEmpty(w.weaponName))
            Text(new Rect(box.x + pad, box.y, Mathf.Max(0f, nameRight - box.x - pad), textH), w.weaponName, smallStyle,
                DimText, TextAnchor.MiddleLeft);
    }

    // ---- top centre: timer, score, players, message ----

    void DrawTop(float k)
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
            DrawMessage(k, cx, timerBox.yMax + 8f * k);
            return;
        }

        // timer m:ss, red in the last 10 seconds, grey between rounds
        int seconds = Mathf.CeilToInt(rounds.TimeLeft);
        if (seconds != timerValue)
        {
            timerValue = seconds;
            timerText = (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }
        bool idle = rounds.InBreak || rounds.MatchOver;
        Color timerColor = idle ? DimText : seconds <= 10 ? lowColor : Color.white;
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

        DrawMessage(k, cx, top + h + 8f * k);
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

    void DrawMessage(float k, float cx, float y)
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
            return;
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
    }

    // ---- centre: crosshair and damage arcs ----

    void DrawCrosshair(float k)
    {
        if (weapon == null) return;
        if (shoot != null && shoot.IsScoped) return;
        // sniper rifles have no crosshair, only the scope
        if (weapon.data != null && weapon.data.scopeFov != null && weapon.data.scopeFov.Length > 0) return;

        float size = Mathf.Clamp(GameSettings.CrosshairSize, GameSettings.MinCrosshairSize, GameSettings.MaxCrosshairSize);
        Color color = GameSettings.CrosshairColorValue;

        // the real spread of the next shot (as in Weapon.TryFire): base spread x stance (crouch < Shift < stand < run < air);
        // the bullet goes up to that many degrees off, turned into pixels with the camera's field of view
        float spreadDeg = Mathf.Clamp(weapon.spreadAngle * player.SpreadMultiplier, 0f, 30f);
        float vfov = playerCam != null ? playerCam.fieldOfView : 60f;
        float spreadPx = Screen.height * 0.5f * Mathf.Tan(spreadDeg * Mathf.Deg2Rad) / Mathf.Tan(vfov * 0.5f * Mathf.Deg2Rad);

        float len = Mathf.Max(2f, Mathf.Round(crosshairLength * k * size));
        float thick = Mathf.Max(1f, Mathf.Round(crosshairThickness * k * Mathf.Sqrt(size)));
        float gap = Mathf.Round(crosshairBaseGap * k * size + spreadPx + bloom * k);
        gap = Mathf.Min(gap, Screen.height * 0.2f);
        float outline = Mathf.Max(1f, Mathf.Round(k));

        float cx = Mathf.Floor(Screen.width * 0.5f);
        float cy = Mathf.Floor(Screen.height * 0.5f);
        float half = Mathf.Floor(thick * 0.5f);

        Rect right = new Rect(cx + gap, cy - half, len, thick);
        Rect left = new Rect(cx - gap - len, cy - half, len, thick);
        Rect down = new Rect(cx - half, cy + gap, thick, len);
        Rect up = new Rect(cx - half, cy - gap - len, thick, len);

        // thin dark outline first, then the lines
        Color dark = new Color(0f, 0f, 0f, 0.6f * color.a);
        Fill(Grow(right, outline), dark);
        Fill(Grow(left, outline), dark);
        Fill(Grow(down, outline), dark);
        Fill(Grow(up, outline), dark);

        Fill(right, color);
        Fill(left, color);
        Fill(down, color);
        Fill(up, color);
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
