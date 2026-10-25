using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;

// Rounds 5v5 (or the solo mode, or a background simulation).
// Normal 5v5 games have the CS money (Economy): kill rewards, round win / loss money, a buy time at the round start,
// BuyMenu for the player, BotBuyer for the bots; survivors keep their weapons, the dead get knife + default pistol.
// Simulations keep the fixed weapons (-weapon0 / -weapon1) and full ammo every round, without money.
// Everybody spawns looking at the middle between the two spawns (the map centre), not along the spawn point.
// Normal 5v5 games only (never simulations, never the solo mode):
// - freeze time: the first freezeTime seconds of every round nobody moves or shoots (looking around and buying work),
//   the round timer starts after it; the buy time (Economy.BuyTimeLength) starts with the freeze;
// - halftime: after half of maxRounds (or halftimeAfterRound) everybody changes side once per match (T <-> CT):
//   scores swap with the groups, money $800, loss streaks reset, default weapons of the new side, bots renamed;
// - buy zones: one rectangle along the world axes around each spawn (its points and the spots of a big team,
//   buyZoneMargin wider on each side, at least minBuyZoneSize across), buying only inside it;
// - frozen bots (main menu option, botsFrozen in the Inspector or -freezebots): bots stand still and never shoot;
// - the number of players per team comes from the main menu (GameSettings.PendingBotsPerTeam) when it was chosen.
// Spawn places: every player gets his own spot on the floor, at least SpawnSpacing from everybody placed in the same
// round (both teams), never on top of anybody: the first players of a team stand on the spawn points, the players of a
// team bigger than its spawn points stand behind and beside the points (on the NavMesh, on the same floor, reachable
// from the point in a straight line, not inside a wall or a box). Simulations with no more players than spawn points
// keep exactly the old places (the points themselves).
// The developer console uses: Current, RestartMatch, EndRoundNow, KillTeam, SpawnBotsNear, SetBotsFrozen.
public class RoundManager : MonoBehaviour
{
    public static event System.Action RoundStarted;

    // ---- shared with other scripts (batch 4 contract) ----
    // the first seconds of a round: everybody stands still and cannot shoot, buying is allowed (never in simulations)
    public static bool InFreezeTime;
    public static float FreezeTimeLeft;
    // bots stand still and never shoot, but can be killed (main menu option / Inspector / -freezebots); never in simulations
    public static bool BotsFrozen;
    // the teams swapped sides (T <-> CT) at halftime; Health.team of every player has already changed.
    // Raised in StartRound after everybody was revived and placed at the new spawn with the default weapons of the
    // new side (every player object is active then), before the bots buy and before RoundStarted.
    // A new match (R) after the halftime puts everybody back on the first side and raises it again.
    public static event System.Action Halftime;
    // a round ended: the winner team (-1 = a draw), the round's MVP (null = none) and his kills in this round
    public static event System.Action<int, Health, int> RoundEnded;
    // where each team may buy (around its spawn); Economy.InBuyZone and the radar use it
    public struct BuyZone
    {
        public int team;
        public Vector3 center;
        // the zone is a rectangle along the world axes: size.x across X, size.y across Z (metres);
        // radius = half of its diagonal, for code that only needs a rough circle
        public Vector2 size;
        public float radius;

        public bool Contains(Vector3 p)
        {
            Vector3 d = p - center;
            if (size.x > 0f && size.y > 0f) return Mathf.Abs(d.x) <= size.x * 0.5f && Mathf.Abs(d.z) <= size.y * 0.5f;
            d.y = 0f;
            return d.sqrMagnitude <= radius * radius;
        }
    }
    public static readonly System.Collections.Generic.List<BuyZone> BuyZones = new System.Collections.Generic.List<BuyZone>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetSharedStatics()
    {
        RoundStarted = null;
        RoundEnded = null;
        InFreezeTime = false;
        FreezeTimeLeft = 0f;
        BotsFrozen = false;
        Halftime = null;
        BuyZones.Clear();
        current = null;
    }

    // raises Halftime (after the side switch)
    static void RaiseHalftime() { Halftime?.Invoke(); }

    // the RoundManager of the loaded scene (null between scenes)
    static RoundManager current;

    // the RoundManager of the loaded scene, null when the scene has none (for the developer console and the menus)
    public static RoundManager Current
    {
        get
        {
            if (current == null) current = FindAnyObjectByType<RoundManager>();
            return current;
        }
    }

    // players per team (the player included) set in the Inspector of the loaded scene, before the main menu
    // choice or -bots changed botsPerTeam; the main menu shows it as the default "N × N"
    public static int DefaultBotsPerTeam
    {
        get
        {
            if (current == null) current = FindAnyObjectByType<RoundManager>();
            return current != null ? current.SceneBotsPerTeam : 5;
        }
    }

    // the same for this scene: botsPerTeam as it was saved in the scene
    public int SceneBotsPerTeam => sceneBotsPerTeam > 0 ? sceneBotsPerTeam : botsPerTeam;
    int sceneBotsPerTeam;
    [Header("Scene")]
    public Transform spawnRoot0;
    public Transform spawnRoot1;
    public GameObject botPrefab;
    public Health player;
    public Camera overviewCamera;
    public Material[] teamMaterials;
    [Header("Stealth")]
    public int stealthLevel0;
    public int stealthLevel1;
    public float speedPenalty = 0.1f;
    public float radiusFactor = 0.5f;
    public float revealTime = 1f;

    [Header("Bots")]
    [Tooltip("chance that a bot sneaks (Shift) when it goes to check an enemy; -sneak on the command line")]
    [Range(0f, 1f)] public float botSneakChance = 0.6f;
    [Tooltip("chance that a bot crouches in a fight at medium/long range; -crouch on the command line (0 = never, like before)")]
    [Range(0f, 1f)] public float botCrouchChance = 0.4f;

    [Header("Bhop for bots (paid movement upgrade, 0 = off)")]
    [Tooltip("the player's level is set on the Bhop component of the Player object")]
    public int bhopLevel0;
    public int bhopLevel1;

    [Header("Weapons of the bots in simulations (leave empty to keep the prefab weapon); in normal games they buy")]
    public WeaponData weaponTeam0;
    public WeaponData weaponTeam1;

    [Header("Solo test mode")]
    [Tooltip("you alone: no bots, no timer, infinite ammo, a training target")] public bool soloMode;
    public GameObject trainingTarget;

    [Header("Rules")]
    [Tooltip("players per team, the player included (the main menu can change it for a match)")]
    public int botsPerTeam = 5;
    public float roundTime = 60f;
    public float breakTime = 3f;
    // the length of a match in rounds, like CS (MR12 = 24): the sides switch after half of them, the match is won with
    // more than half (30 -> switch after 15, 16 wins) and ends in a draw when all are played at equal score.
    // (It used to be "rounds to win"; the value saved in the scenes is kept.)
    [UnityEngine.Serialization.FormerlySerializedAs("roundsToWin")]
    [Tooltip("match length in rounds: the sides switch after half of them, more than half wins (30 -> switch after 15, 16 wins)")]
    public int maxRounds = 5;

    // rounds a team needs to win the match
    public int RoundsToWin => Mathf.Max(1, maxRounds / 2 + 1);
    [Tooltip("seconds at the start of every round when nobody can move or shoot, buying works (normal games only)")]
    public float freezeTime = 3f;
    [Tooltip("the teams change sides (T <-> CT) once per match (normal games only)")]
    public bool halftimeSwitch = true;
    [Tooltip("the side switch comes after this many rounds; 0 = half of maxRounds (30 -> after 15)")]
    public int halftimeAfterRound;
    [Tooltip("the buy zone (a rectangle) is this much wider than the spawn points on each side (metres)")]
    public float buyZoneMargin = 4.5f;
    [Tooltip("the smallest width and depth of a buy zone (metres)")]
    public float minBuyZoneSize = 10f;

    [Header("Training")]
    [Tooltip("bots stand still and never shoot, but can be killed (also the main menu option and -freezebots); never in simulations")]
    public bool botsFrozen;

    [Header("Simulation")]
    public bool simulation;
    public float simTimeScale = 10f;
    public int roundsToSimulate = 200;
    public float simBreakTime = 0.2f;

    public int[] score = new int[2];

    bool Solo => soloMode && !simulation;

    float timer;
    bool inBreak;
    bool matchOver;
    int roundsPlayed;
    string message = "";
    string csvPath;
    GUIStyle style;

    // economy (normal 5v5 games only): lost rounds in a row of each team, for the loss bonus
    readonly int[] lossesInRow = new int[2];
    // the next round gives everybody the default weapons (match start / reset / halftime)
    bool freshLoadouts = true;

    // halftime: done in this match, everybody plays the other side now, Halftime must be raised at the next spawn
    bool halftimeDone;
    bool sidesSwapped;
    bool teamsChanged;
    // added to the message of the last round before the side switch
    const string HalftimeText = "Смена сторон";

    // -freezebots on the command line
    bool freezeBotsArg;

    // the renderers of every bot that got a team material at its spawn (repainted when the sides change)
    readonly System.Collections.Generic.Dictionary<Health, Renderer[]> teamRenderers =
        new System.Collections.Generic.Dictionary<Health, Renderer[]>();

    static bool mapLoaded;

    // the main menu choice is clamped to 1..10 players per team
    public const int MaxPlayersPerTeam = 10;

    // the freeze time, the halftime, the buy zones: normal 5v5 games only
    bool NormalGame => !simulation && !Solo;

    // a background simulation (bot vs bot, -sim): no menus, no console, no money
    public bool IsSimulation => simulation;

    void Awake()
    {
        sceneBotsPerTeam = botsPerTeam;
        current = this;
    }

    void Start()
    {
        // -map <scene name>: play (or simulate) on another map; the scene must be in the build list
        string map = Arg("-map");
        if (map != null && !mapLoaded && map != SceneManager.GetActiveScene().name)
        {
            mapLoaded = true;
            SceneManager.LoadScene(map);
            return;
        }

        ApplyCommandLine();

        // the mode chosen in the main menu (never in simulations)
        if (!simulation && GameSettings.PendingSolo.HasValue) soloMode = GameSettings.PendingSolo.Value;
        // players per team chosen in the main menu, the player counts on his team: N means N × N
        if (!simulation && GameSettings.PendingBotsPerTeam.HasValue)
            botsPerTeam = Mathf.Clamp(GameSettings.PendingBotsPerTeam.Value, 1, MaxPlayersPerTeam);

        current = this;
        freezeBotsArg = Flag("-freezebots");
        if (!simulation) GameSettings.Load();
        BotsFrozen = !simulation && (GameSettings.BotsFrozen || botsFrozen || freezeBotsArg);
        InFreezeTime = false;
        FreezeTimeLeft = 0f;
        BuildBuyZones();

        if (overviewCamera != null) overviewCamera.gameObject.SetActive(simulation);

        Weapon.InfiniteAmmo = Solo;

        // money only in normal 5v5 games; in the solo mode everything is free, simulations have fixed weapons
        Economy.Enabled = !simulation && !Solo;
        Economy.BuyTimeEnd = 0f;
        Economy.ResetAll();

        if (!simulation && FindAnyObjectByType<Scoreboard>() == null) gameObject.AddComponent<Scoreboard>();
        if (!simulation && player != null && FindAnyObjectByType<BuyMenu>() == null) gameObject.AddComponent<BuyMenu>();
        if (trainingTarget != null)
        {
            Health th = trainingTarget.GetComponent<Health>();
            if (!Solo && th != null) th.Unregister();
            trainingTarget.SetActive(Solo);
        }

        if (simulation)
        {
            if (player != null)
            {
                player.Unregister();
                player.gameObject.SetActive(false);
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = simTimeScale;
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            string dir;
            #if UNITY_EDITOR
            dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Analysis", "data");
            #else
            dir = Application.persistentDataPath;
            #endif
            Directory.CreateDirectory(dir);

            string customCsv = Arg("-csv");
            if (customCsv != null)
            {
                csvPath = customCsv;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(customCsv)));
            }
            else
            {
                csvPath = Path.Combine(dir, "sim_" + stamp + ".csv");
            }
            File.WriteAllText(csvPath, "round,winner,alive0,alive1,size0,size1,stealth0,stealth1,penalty,radius,reveal,duration,weapon0,weapon1,bhop0,bhop1,map,penetration,ff,sneak,crouch\n");
            Debug.Log("Simulation results: " + csvPath);
        }

        SpawnBots();
        StartRound();
        StartCoroutine(ResolveDamage());
    }
    // ---------------- command line (for background runs) ----------------
    // example: Game.exe -batchmode -nographics -sim -rounds 500 -stealth0 0 -stealth1 1 -weapon0 AK-47 -weapon1 M4A4 -csv out.csv

    static string Arg(string name)
    {
        string[] a = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < a.Length - 1; i++)
            if (a[i] == name) return a[i + 1];
        return null;
    }

    static bool Flag(string name)
    {
        foreach (string a in System.Environment.GetCommandLineArgs())
            if (a == name) return true;
        return false;
    }

    static int ArgInt(string name, int fallback)
    {
        string v = Arg(name);
        return v != null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int r) ? r : fallback;
    }

    static float ArgFloat(string name, float fallback)
    {
        string v = Arg(name);
        return v != null && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ? r : fallback;
    }

    WeaponData FindWeapon(string weaponName)
    {
        if (weaponName == "default") return null;

        WeaponData[] all = Resources.LoadAll<WeaponData>("Weapons");
        foreach (WeaponData w in all)
        {
            if (string.Equals(w.weaponName, weaponName, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.name, weaponName, System.StringComparison.OrdinalIgnoreCase))
                return w;
        }

        string names = "";
        foreach (WeaponData w in all) names += w.weaponName + ", ";
        Debug.LogError("Weapon not found: " + weaponName + ". Available (Assets/Resources/Weapons): " + names);
        return null;
    }

    void ApplyCommandLine()
    {
        if (Flag("-sim")) simulation = true;

        roundsToSimulate = ArgInt("-rounds", roundsToSimulate);
        simTimeScale = ArgFloat("-timescale", simTimeScale);
        botsPerTeam = ArgInt("-bots", botsPerTeam);
        roundTime = ArgFloat("-roundtime", roundTime);

        stealthLevel0 = ArgInt("-stealth0", stealthLevel0);
        stealthLevel1 = ArgInt("-stealth1", stealthLevel1);
        speedPenalty = ArgFloat("-penalty", speedPenalty);
        radiusFactor = ArgFloat("-radius", radiusFactor);
        revealTime = ArgFloat("-reveal", revealTime);

        // the scenes keep bhop 1 for the bots of normal games; a simulation has no bhop unless -bhop0/-bhop1 ask for it
        // (before 2026-10-20 the scene value leaked into every series: "bhop 1 vs 0" really ran 1 vs 1)
        bhopLevel0 = ArgInt("-bhop0", simulation ? 0 : bhopLevel0);
        bhopLevel1 = ArgInt("-bhop1", simulation ? 0 : bhopLevel1);

        string w0 = Arg("-weapon0");
        if (w0 != null) weaponTeam0 = FindWeapon(w0);

        string w1 = Arg("-weapon1");
        if (w1 != null) weaponTeam1 = FindWeapon(w1);

        string seed = Arg("-seed");
        if (seed != null && int.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sd))
            Random.InitState(sd);

        // bullets go through walls in the game; in simulations only with -penetration 1 (so old results stay comparable)
        Weapon.PenetrationEnabled = !simulation || ArgInt("-penetration", 0) == 1;
        // shots hurt teammates in the game; in simulations only with -ff 1 (old results stay comparable)
        Weapon.FriendlyFire = !simulation || ArgInt("-ff", 0) == 1;
        botSneakChance = Mathf.Clamp01(ArgFloat("-sneak", botSneakChance));
        botCrouchChance = Mathf.Clamp01(ArgFloat("-crouch", botCrouchChance));
    }

    IEnumerator ResolveDamage()
    {
        WaitForFixedUpdate wait = new WaitForFixedUpdate();
        while (true)
        {
            yield return wait;
            foreach (Health h in Health.All) h.ApplyPending();
        }
    }

    void OnDestroy()
    {
        Time.timeScale = 1f;
        Weapon.InfiniteAmmo = false;
        Economy.Enabled = false;
        Economy.BuyTimeEnd = 0f;

        // the freeze, the frozen bots and the zones must not outlive the scene (unless a newer RoundManager runs already)
        if (current == this || current == null)
        {
            current = null;
            InFreezeTime = false;
            FreezeTimeLeft = 0f;
            BotsFrozen = false;
            BuyZones.Clear();
        }
    }

    void OnEnable()
    {
        Health.AnyKilled += OnKilled;
        Health.AnyKilled += CountRoundKill;
        Health.AnyDamaged += CountRoundDamage;
    }

    void OnDisable()
    {
        Health.AnyKilled -= OnKilled;
        Health.AnyKilled -= CountRoundKill;
        Health.AnyDamaged -= CountRoundDamage;
    }

    // ---------------- MVP of the round ----------------
    // Like CS: the player of the winning team with the most kills of enemies in this round (a tie: more damage
    // to enemies in this round). Normal 5v5 games only; the MVP count of the match is shown in the TAB table.

    readonly System.Collections.Generic.Dictionary<Health, int> roundKills = new System.Collections.Generic.Dictionary<Health, int>();
    readonly System.Collections.Generic.Dictionary<Health, float> roundDamage = new System.Collections.Generic.Dictionary<Health, float>();
    readonly System.Collections.Generic.Dictionary<Health, int> mvps = new System.Collections.Generic.Dictionary<Health, int>();

    // the MVP of the last round (null = none) and his kills in it
    public Health LastMvp { get; private set; }
    public int LastMvpKills { get; private set; }

    // how many times this player was the MVP of a round in this match
    public int MvpCount(Health h)
    {
        return h != null && mvps.TryGetValue(h, out int n) ? n : 0;
    }

    bool CountsForMvp => !simulation && !Solo && !matchOver && !inBreak && !InFreezeTime;

    void CountRoundKill(KillInfo info)
    {
        if (!CountsForMvp) return;
        Health killer = info.killer, victim = info.victim;
        if (killer == null || victim == null || killer == victim || killer.team == victim.team) return;
        roundKills.TryGetValue(killer, out int n);
        roundKills[killer] = n + 1;
    }

    void CountRoundDamage(Health victim, Health attacker, float amount, bool head)
    {
        if (!CountsForMvp) return;
        if (attacker == null || victim == null || attacker == victim || attacker.team == victim.team) return;
        roundDamage.TryGetValue(attacker, out float d);
        roundDamage[attacker] = d + amount;
    }

    Health PickMvp(int winner, out int kills)
    {
        Health best = null;
        kills = 0;
        float bestDamage = 0f;
        foreach (Health h in Health.All)
        {
            if (h == null || h.team != winner || h.GetComponent<TrainingTarget>() != null) continue;
            roundKills.TryGetValue(h, out int k);
            roundDamage.TryGetValue(h, out float d);
            if (k <= 0) continue;
            if (best == null || k > kills || (k == kills && d > bestDamage))
            {
                best = h;
                kills = k;
                bestDamage = d;
            }
        }
        return best;
    }

    // 1 убийство, 2 убийства, 5 убийств
    static string KillsWord(int n)
    {
        int m100 = n % 100, m10 = n % 10;
        if (m100 >= 11 && m100 <= 14) return n + " убийств";
        if (m10 == 1) return n + " убийство";
        if (m10 >= 2 && m10 <= 4) return n + " убийства";
        return n + " убийств";
    }

    // kill reward of the killer weapon (knife $1500, SMG $600, AWP $100 ...), a teammate killed: -$300
    void OnKilled(KillInfo info)
    {
        if (!Economy.Enabled || simulation || matchOver) return;
        Health killer = info.killer;
        Health victim = info.victim;
        if (killer == null || victim == null || killer == victim) return;

        if (killer.team == victim.team) Economy.Add(killer, -Economy.TeamKillPenalty, Economy.ReasonTeamKill);
        else Economy.Add(killer, Economy.KillReward(info.weapon), Economy.ReasonKill);
    }

    // round end: the winners get $3250, the losers the loss bonus; a draw is a loss for both teams
    void PayRound(int winner)
    {
        if (!Economy.Enabled) return;

        for (int team = 0; team < 2; team++)
        {
            int amount;
            string reason;
            if (team == winner)
            {
                lossesInRow[team] = 0;
                amount = Economy.RoundWinReward;
                reason = Economy.ReasonRoundWin;
            }
            else
            {
                lossesInRow[team]++;
                amount = Economy.LossBonus(lossesInRow[team]);
                reason = Economy.ReasonRoundLoss;
            }

            foreach (Health h in Health.All)
                if (h.team == team) Economy.Add(h, amount, reason);
        }
    }

    // the money this team would get for losing the next round (for the buy menu and the HUD)
    public int NextLossBonus(int team)
    {
        if (team < 0 || team > 1) return 0;
        return Economy.LossBonus(lossesInRow[team] + 1);
    }

    void Update()
    {
        // the main menu option works at once, also from the pause menu
        if (!simulation) BotsFrozen = GameSettings.BotsFrozen || botsFrozen || freezeBotsArg;

        if (Solo)
        {
            // no rounds in solo mode, only respawn if the player dies (for example falls out of the map)
            if (player != null && player.IsDead)
            {
                BeginPlacement();
                Spawn(0, spawnRoot0);
                RoundStarted?.Invoke();
            }
            return;
        }

        if (matchOver)
        {
            if (!simulation && !MainMenu.IsOpen && !DevConsole.IsOpen && Input.GetKeyDown(KeyCode.R)) ResetMatch();
            return;
        }

        if (inBreak)
        {
            timer -= Time.deltaTime;
            if (timer <= 0f) StartRound();
            return;
        }

        // the freeze at the round start: the round timer waits, nobody moves or shoots (nobody can die either)
        if (InFreezeTime)
        {
            FreezeTimeLeft -= Time.deltaTime;
            if (FreezeTimeLeft > 0f) return;
            InFreezeTime = false;
            FreezeTimeLeft = 0f;
        }

        timer -= Time.deltaTime;

        int alive0 = Alive(0);
        int alive1 = Alive(1);
        if (alive0 == 0 || alive1 == 0 || timer <= 0f) EndRound(alive0, alive1);
    }

    void SpawnBots()
    {
        if (Solo) return;

        for (int team = 0; team < 2; team++)
        {
            // botsPerTeam counts the player on his own team (N × N players)
            int count = botsPerTeam;
            if (player != null && !simulation && player.team == team) count--;

            Transform root = team == 0 ? spawnRoot0 : spawnRoot1;

            for (int i = 0; i < count; i++)
            {
                Transform point = root.GetChild(i % root.childCount);
                CreateBot(team, i + 1, point.position + Vector3.up, point.rotation);
            }
        }
    }

    // a new bot of the team: the name T_n / CT_n, the stealth level of the team, the bot options of this match and the
    // team colour of its capsule (the spawn or SpawnBotsNear puts it on its place and gives it the weapons)
    GameObject CreateBot(int team, int number, Vector3 position, Quaternion rotation)
    {
        GameObject bot = Instantiate(botPrefab, position, rotation);
        bot.name = (team == 0 ? "T_" : "CT_") + number;
        bot.GetComponent<Health>().team = team;
        Stealth st = bot.GetComponent<Stealth>();
        if (st == null) st = bot.AddComponent<Stealth>();
        st.level = team == 0 ? stealthLevel0 : stealthLevel1;
        st.speedPenaltyPerLevel = speedPenalty;
        st.radiusFactor = radiusFactor;
        st.revealTime = revealTime;
        BotController bc = bot.GetComponent<BotController>();
        if (bc != null)
        {
            bc.sneakChance = botSneakChance;
            bc.crouchChance = botCrouchChance;
            if (botCrouchChance <= 0f) bc.coverCrouchChance = 0f;
        }

        if (teamMaterials != null && team < teamMaterials.Length)
        {
            Renderer[] own = bot.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in own)
                r.sharedMaterial = teamMaterials[team];
            // only these renderers change colour at the halftime (not the character model added later)
            if (!simulation) teamRenderers[bot.GetComponent<Health>()] = own;
        }
        return bot;
    }

    // ---------------- buy zones ----------------

    // One rectangle along the world axes around each spawn: the box of its spawn points and of the spots used at the
    // last round start (a team bigger than its points stands beside them), buyZoneMargin wider on each side, at least
    // minBuyZoneSize across, moved inside the outer walls (MapBounds). Built again at every round start (after the
    // spawn), so a bigger team (main menu, bots added by the console) gets a bigger zone. A zone keeps its side (team
    // index = spawnRoot0 / spawnRoot1): after the halftime everybody buys at his new spawn. None in solo / simulations.
    void BuildBuyZones()
    {
        BuyZones.Clear();
        if (!NormalGame) return;
        AddBuyZone(0, spawnRoot0);
        AddBuyZone(1, spawnRoot1);
    }

    void AddBuyZone(int team, Transform root)
    {
        if (root == null || root.childCount == 0) return;

        // the box of the points and of the spots of this side
        Vector3 min = root.GetChild(0).position;
        Vector3 max = min;
        for (int i = 1; i < root.childCount; i++)
        {
            min = Vector3.Min(min, root.GetChild(i).position);
            max = Vector3.Max(max, root.GetChild(i).position);
        }
        foreach (Vector3 p in sideSpots[team])
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        float margin = Mathf.Max(0f, buyZoneMargin);
        float minSize = Mathf.Max(1f, minBuyZoneSize);
        float x0 = min.x - margin, x1 = max.x + margin;
        float z0 = min.z - margin, z1 = max.z + margin;
        GrowTo(ref x0, ref x1, minSize);
        GrowTo(ref z0, ref z1, minSize);

        // not through the outer walls: MapBounds.halfSize is the middle of the 1 m thick walls around the map
        MapBounds bounds = FindAnyObjectByType<MapBounds>();
        if (bounds != null)
        {
            KeepInside(ref x0, ref x1, bounds.halfSizeX - 0.5f);
            KeepInside(ref z0, ref z1, bounds.halfSizeZ - 0.5f);
        }

        Vector2 size = new Vector2(x1 - x0, z1 - z0);
        BuyZones.Add(new BuyZone
        {
            team = team,
            center = new Vector3((x0 + x1) * 0.5f, SpawnCentre(root).y, (z0 + z1) * 0.5f),
            size = size,
            radius = size.magnitude * 0.5f
        });
    }

    // makes the range [lo, hi] at least this long, growing it on both sides
    static void GrowTo(ref float lo, ref float hi, float length)
    {
        float extra = length - (hi - lo);
        if (extra <= 0f) return;
        lo -= extra * 0.5f;
        hi += extra * 0.5f;
    }

    // moves the range [lo, hi] inside [-limit, limit] without making it shorter (cut only if it is longer than that)
    static void KeepInside(ref float lo, ref float hi, float limit)
    {
        if (limit <= 0f) return;
        if (hi - lo >= limit * 2f)
        {
            lo = Mathf.Max(lo, -limit);
            hi = Mathf.Min(hi, limit);
            return;
        }
        if (lo < -limit)
        {
            hi += -limit - lo;
            lo = -limit;
        }
        if (hi > limit)
        {
            lo -= hi - limit;
            hi = limit;
        }
    }

    // ---------------- halftime ----------------

    bool HalftimeEnabled => NormalGame && halftimeSwitch;

    // the side switch comes after this many rounds (0 = never)
    int HalftimeRound => halftimeAfterRound > 0 ? halftimeAfterRound : Mathf.Max(0, maxRounds / 2);

    bool HalftimeDue => HalftimeEnabled && !halftimeDone && HalftimeRound > 0 && roundsPlayed >= HalftimeRound;

    // everybody changes side (T <-> CT): the halftime, or back to the first side for a new match.
    // Each group keeps its score (score[0] stays the score of the T side), money and loss streaks start again,
    // the next spawn gives the default weapons of the new side, the bots get the names of their new side.
    void SwapTeams()
    {
        foreach (Health h in Health.All)
            if (h != null && (h.team == 0 || h.team == 1)) h.team = 1 - h.team;

        int s = score[0];
        score[0] = score[1];
        score[1] = s;

        Economy.ResetAll();
        lossesInRow[0] = 0;
        lossesInRow[1] = 0;
        freshLoadouts = true;
        sidesSwapped = !sidesSwapped;
        teamsChanged = true;

        // bots: T_1.. / CT_1.. in the old order, the stealth level and the capsule colour of the new side
        int[] number = new int[2];
        foreach (Health h in Health.All)
        {
            if (h == null || h.team < 0 || h.team > 1) continue;
            if (h.GetComponent<BotController>() == null) continue;

            number[h.team]++;
            h.gameObject.name = (h.team == 0 ? "T_" : "CT_") + number[h.team];

            Stealth st = h.GetComponent<Stealth>();
            if (st != null) st.level = h.team == 0 ? stealthLevel0 : stealthLevel1;

            if (teamMaterials != null && h.team < teamMaterials.Length && teamMaterials[h.team] != null &&
                teamRenderers.TryGetValue(h, out Renderer[] own))
                foreach (Renderer r in own)
                    if (r != null) r.sharedMaterial = teamMaterials[h.team];
        }
    }

    void StartRound()
    {
        roundKills.Clear();
        roundDamage.Clear();

        // halftime: after half of the match everybody plays the other side (once per match)
        bool halftime = HalftimeDue;
        if (halftime)
        {
            halftimeDone = true;
            SwapTeams();
        }

        BeginPlacement();
        Spawn(0, spawnRoot0);
        if (!Solo) Spawn(1, spawnRoot1);
        freshLoadouts = false;
        // the zones cover the spots used now (a bigger team stands on more of them)
        BuildBuyZones();

        // the sides changed: everybody is alive, active and at his new spawn, the listeners update now
        if (teamsChanged)
        {
            teamsChanged = false;
            RaiseHalftime();
        }

        // normal games: the bots buy (or save) and take their best weapon, then the buy time runs
        if (!simulation)
        {
            foreach (Health h in Health.All)
            {
                BotBuyer buyer = h.GetComponent<BotBuyer>();
                if (buyer == null) continue;
                if (Economy.Enabled) buyer.Buy();
                buyer.EquipBest();
            }
        }
        // the buy time starts with the freeze, the round timer after it
        Economy.BuyTimeEnd = Economy.Enabled ? Time.time + Economy.BuyTimeLength : 0f;
        InFreezeTime = NormalGame && freezeTime > 0f;
        FreezeTimeLeft = InFreezeTime ? freezeTime : 0f;

        RoundStarted?.Invoke();
        timer = roundTime;
        inBreak = false;
        // the halftime was announced in the message of the last round; GameHud shows its banner on Halftime
        message = "";
    }

    void Spawn(int team, Transform root)
    {
        // simulations with no more players than spawn points stand exactly where they always did (on the points)
        bool oldPlaces = simulation && Size(team) <= root.childCount;
        int i = 0;
        foreach (Health h in Health.All)
        {
            if (h.team != team) continue;

            Transform point = root.GetChild(i % root.childCount);
            // the first players of the team on the points, the others (a big team) on free spots beside them
            Vector3 position = oldPlaces ? point.position : SpawnSpot(root, i);
            i++;

            // like CS: a survivor keeps his weapons and their ammo, a dead (or new) player gets the default ones
            bool keep = !freshLoadouts && !h.IsDead;

            h.Revive();
            if (oldPlaces) PlaceAtPoint(h, point, position);
            else Place(h, position, SpawnFacing(position, point.eulerAngles.y));
            placed.Add(position);
            if (team == 0 || team == 1) sideSpots[team].Add(position);

            // bots take the team level, the player keeps the level set on his own Bhop component
            bool isPlayer = h.GetComponent<PlayerController>() != null;
            Bhop bh = h.GetComponent<Bhop>();
            if (bh == null) bh = h.gameObject.AddComponent<Bhop>();
            if (!isPlayer) bh.level = team == 0 ? bhopLevel0 : bhopLevel1;
            bh.ResetBonus();

            // the player: knife + pistol or the weapons he kept, the buy menu adds the rest
            PlayerLoadout loadout = h.GetComponent<PlayerLoadout>();
            if (loadout != null)
            {
                loadout.NewRound(keep);
                continue;
            }

            // bots of a normal game buy their weapons (StartRound calls BotBuyer.Buy after the spawn)
            if (!simulation && h.GetComponent<BotController>() != null)
            {
                BotBuyer buyer = h.GetComponent<BotBuyer>();
                if (buyer == null) buyer = h.gameObject.AddComponent<BotBuyer>();
                buyer.NewRound(keep);
                continue;
            }

            // simulations: the fixed weapon of the team (-weapon0 / -weapon1), full ammo every round
            Weapon w = h.GetComponentInChildren<Weapon>(true);
            if (w != null)
            {
                WeaponData d = team == 0 ? weaponTeam0 : weaponTeam1;
                if (d != null) w.Equip(d);
                w.ResetAmmo();
            }
        }
    }

    // the point the spawned players look at: the middle between the two spawns (the map centre on the
    // symmetric maps), so nobody starts looking at a wall whatever the rotation of the spawn point is
    Vector3 SpawnLookTarget()
    {
        return (SpawnCentre(spawnRoot0) + SpawnCentre(spawnRoot1)) * 0.5f;
    }

    // the middle of a spawn's points; the map centre (0, 0, 0) without them
    static Vector3 SpawnCentre(Transform root)
    {
        if (root == null || root.childCount == 0) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < root.childCount; i++) sum += root.GetChild(i).position;
        return sum / root.childCount;
    }

    // looking from this place at the map centre; fallbackYaw when the place is the centre itself
    Quaternion SpawnFacing(Vector3 from, float fallbackYaw)
    {
        Vector3 to = SpawnLookTarget() - from;
        to.y = 0f;
        if (to.sqrMagnitude < 0.25f) return Quaternion.Euler(0f, fallbackYaw, 0f);
        return Quaternion.LookRotation(to.normalized, Vector3.up);
    }

    // the old placing of the simulations (exactly as before 2026-10-24): the agent is warped to the point itself
    void PlaceAtPoint(Health h, Transform point, Vector3 position)
    {
        NavMeshAgent agent = h.GetComponent<NavMeshAgent>();
        CharacterController cc = h.GetComponent<CharacterController>();

        if (agent != null)
        {
            agent.Warp(position);
        }
        else if (cc != null)
        {
            cc.enabled = false;
            h.transform.position = position + Vector3.up;
            cc.enabled = true;
        }
        else
        {
            h.transform.position = position + Vector3.up;
        }

        h.transform.rotation = SpawnFacing(point.position, point.eulerAngles.y);

        // the player also looks straight ahead, not up or down as at the end of the last round
        PlayerController pc = h.GetComponent<PlayerController>();
        if (pc != null) pc.ResetLook();
    }

    // puts a player or a bot with his feet on this floor spot: a bot's NavMeshAgent is warped there (it stands on the
    // NavMesh just above the floor), the player's CharacterController stands on the floor whatever his crouch height is
    void Place(Health h, Vector3 floor, Quaternion facing)
    {
        NavMeshAgent agent = h.GetComponent<NavMeshAgent>();
        CharacterController cc = h.GetComponent<CharacterController>();

        if (agent != null)
        {
            // no NavMesh under the spot (should not happen): at least the body stands there
            if (!agent.enabled || !agent.Warp(floor)) h.transform.position = floor + Vector3.up * agent.baseOffset;
        }
        else if (cc != null)
        {
            cc.enabled = false;
            // the bottom of the capsule a little above the floor (centre.y follows the crouch, the bottom stays)
            float bottom = cc.height * 0.5f - cc.center.y;
            h.transform.position = floor + Vector3.up * (bottom + cc.skinWidth + 0.02f);
            cc.enabled = true;
        }
        else
        {
            h.transform.position = floor + Vector3.up;
        }

        h.transform.rotation = facing;

        // the player also looks straight ahead, not up or down as at the end of the last round
        PlayerController pc = h.GetComponent<PlayerController>();
        if (pc != null) pc.ResetLook();
    }

    // ---------------- spawn spots ----------------

    // the smallest distance between two players placed at a round start (metres; a character is 1 m wide)
    public const float SpawnSpacing = 1.3f;
    // what must fit at a spot: a standing character (2 m tall, radius 0.5; a little thinner, so a spot next to a
    // wall still counts - the NavMesh keeps 0.5 m from walls anyway), from a bit above the floor
    const float SpotRadius = 0.45f;
    const float SpotHeight = 2f;
    const float SpotFloorGap = 0.2f;
    // the search goes around its start in rings: the first one this far, then every RingStep up to LastRing
    const float FirstRing = 1.8f;
    const float RingStep = 0.75f;
    const float LastRing = 9f;
    // the first direction of a ring, then turning to both sides by 30 degrees
    static readonly float[] RingAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };
    // the level without the capsules (Ignore Raycast), bodies and the player's own model (LocalBody)
    const int IgnoreRaycastLayer = 2;
    const int LocalBodyLayer = 9;
    const int WorldMask = Physics.DefaultRaycastLayers & ~(1 << IgnoreRaycastLayer) & ~(1 << LocalBodyLayer);
    // the most bots SpawnBotsNear adds at once
    const int MaxSpawnNear = 20;

    // the floor spots taken in this round (both teams; SpawnBotsNear: everybody alive) and the spots of each side
    // (team index) used at the last round start, for its buy zone
    readonly List<Vector3> placed = new List<Vector3>();
    readonly List<Vector3>[] sideSpots = { new List<Vector3>(), new List<Vector3>() };
    readonly Collider[] overlapHits = new Collider[64];
    readonly RaycastHit[] rayHits = new RaycastHit[64];

    // a new round (or the solo respawn) places everybody again
    void BeginPlacement()
    {
        placed.Clear();
        sideSpots[0].Clear();
        sideSpots[1].Clear();
    }

    // the floor spot of the index-th player of a team: his spawn point when it is free, else the nearest free spot
    // around it, trying behind the point first (away from the map centre), then beside it.
    // No Random: the same team on the same map always gets the same spots.
    Vector3 SpawnSpot(Transform root, int index)
    {
        int n = root.childCount;
        Transform point = root.GetChild(index % n);
        Vector3 away = point.position - SpawnLookTarget();
        away.y = 0f;
        if (away.sqrMagnitude < 0.01f) away = -point.forward;

        if (FindFreeSpot(point.position, point.position, away, out Vector3 spot)) return spot;
        // a very crowded spawn: around the other points of the team
        for (int k = 1; k < n; k++)
        {
            Transform other = root.GetChild((index + k) % n);
            if (FindFreeSpot(other.position, other.position, away, out spot)) return spot;
        }
        // nothing free at all (never on the maps): the point itself, on the floor
        return FloorOrSelf(point.position);
    }

    // a free floor spot near "centre", reachable in a straight line from "from" (no wall between): the centre itself,
    // then rings of growing radius, each one starting in the direction dir
    bool FindFreeSpot(Vector3 from, Vector3 centre, Vector3 dir, out Vector3 spot)
    {
        Vector3 fromFloor = FloorOrSelf(from);
        // with a NavMesh the spot must be on it (bots walk on it) and walkable from "from" in a straight line
        bool mesh = NavMesh.SamplePosition(fromFloor, out NavMeshHit fromHit, 2f, NavMesh.AllAreas);
        Vector3 fromNav = mesh ? fromHit.position : fromFloor;

        // the candidates are looked for at the height of the floor of "from"
        Vector3 start = new Vector3(centre.x, fromFloor.y, centre.z);
        dir.y = 0f;
        dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector3.forward;

        if (TrySpot(start, fromFloor, mesh, fromNav, out spot)) return true;
        for (float r = FirstRing; r <= LastRing + 0.01f; r += RingStep)
        {
            foreach (float angle in RingAngles)
            {
                Vector3 c = start + Quaternion.Euler(0f, angle, 0f) * dir * r;
                if (TrySpot(c, fromFloor, mesh, fromNav, out spot)) return true;
            }
        }
        spot = start;
        return false;
    }

    // one candidate: near the NavMesh (if there is one) on the same floor, reachable from "from", the real floor under
    // it, at least SpawnSpacing from every spot taken, and a standing character fits there (no wall, box or crate)
    bool TrySpot(Vector3 c, Vector3 fromFloor, bool mesh, Vector3 fromNav, out Vector3 spot)
    {
        spot = c;
        // cheap test first: the NavMesh moves the candidate by 0.7 m at most
        if (TooClose(c, SpawnSpacing - 0.7f)) return false;

        Vector3 ground = c;
        if (mesh)
        {
            // the NavMesh near the wanted place and on the same floor (a box or a platform is too far up)
            if (!NavMesh.SamplePosition(c, out NavMeshHit hit, 0.7f, NavMesh.AllAreas)) return false;
            // a straight walk from "from": no wall between (and not the other side of a wall)
            if (NavMesh.Raycast(fromNav, hit.position, out NavMeshHit _, NavMesh.AllAreas)) return false;
            ground = hit.position;
        }
        else if (WorldBlocked(fromFloor, c))
        {
            return false;
        }

        // the real floor (the NavMesh floats a little above it), not far from the NavMesh height
        if (!FloorBelow(ground + Vector3.up, 2f, out Vector3 floor)) return false;
        if (Mathf.Abs(floor.y - ground.y) > 0.5f || Mathf.Abs(floor.y - fromFloor.y) > 0.7f) return false;

        if (TooClose(floor, SpawnSpacing) || Blocked(floor)) return false;
        spot = floor;
        return true;
    }

    // closer than this (on the ground, height not counted) to a spot taken already
    bool TooClose(Vector3 p, float distance)
    {
        float d2 = distance * distance;
        foreach (Vector3 q in placed)
        {
            float dx = p.x - q.x, dz = p.z - q.z;
            if (dx * dx + dz * dz < d2) return true;
        }
        return false;
    }

    // a standing character at this floor spot would be inside the level (a wall, a box, a crate, a low ceiling)
    bool Blocked(Vector3 floor)
    {
        Vector3 bottom = floor + Vector3.up * (SpotRadius + SpotFloorGap);
        Vector3 top = floor + Vector3.up * (SpotHeight - SpotRadius);
        int n = Physics.OverlapCapsuleNonAlloc(bottom, top, SpotRadius, overlapHits, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (!IsCharacter(overlapHits[i])) return true;
        return false;
    }

    // something of the level between the two floor points, at the height of the waist (no NavMesh to check it)
    bool WorldBlocked(Vector3 a, Vector3 b)
    {
        Vector3 from = a + Vector3.up;
        Vector3 to = b + Vector3.up;
        Vector3 d = to - from;
        float length = d.magnitude;
        if (length < 0.01f) return false;
        int n = Physics.RaycastNonAlloc(from, d / length, rayHits, length, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (!IsCharacter(rayHits[i].collider)) return true;
        return false;
    }

    // the nearest floor under this point (players and bots do not count), up to distance below it
    bool FloorBelow(Vector3 origin, float distance, out Vector3 floor)
    {
        floor = origin;
        bool found = false;
        float best = float.MaxValue;
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, rayHits, distance, WorldMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            if (IsCharacter(rayHits[i].collider) || rayHits[i].distance >= best) continue;
            best = rayHits[i].distance;
            floor = rayHits[i].point;
            found = true;
        }
        return found;
    }

    // the floor under a place (a spawn point hangs 1 m above it, a character's centre too); the place without a floor
    Vector3 FloorOrSelf(Vector3 p)
    {
        return FloorBelow(p + Vector3.up * 0.5f, 3.5f, out Vector3 floor) ? floor : p;
    }

    // a capsule or a hitbox of a player or a bot: not an obstacle for a spot (everybody is placed again anyway).
    // The training target never moves, so it is one.
    static bool IsCharacter(Collider c)
    {
        Health h = c.GetComponentInParent<Health>();
        return h != null && h.GetComponent<TrainingTarget>() == null;
    }

    int Alive(int team)
    {
        int count = 0;
        foreach (Health h in Health.All)
            if (h.team == team && !h.IsDead) count++;
        return count;
    }
    int Size(int team)
    {
        int count = 0;
        foreach (Health h in Health.All)
            if (h.team == team) count++;
        return count;
    }
    void EndRound(int alive0, int alive1)
    {
        int winner = -1;
        if (alive0 > 0 && alive1 == 0) winner = 0;
        else if (alive1 > 0 && alive0 == 0) winner = 1;
        FinishRound(winner, alive0, alive1);
    }

    // the round is over with this winner (-1 = a draw): score, money, MVP, message, the CSV line of a simulation
    void FinishRound(int winner, int alive0, int alive1)
    {
        if (winner >= 0) score[winner]++;
        roundsPlayed++;

        // the round is over: no more buying, the round money is paid at once (normal games only)
        Economy.BuyTimeEnd = 0f;
        if (!simulation) PayRound(winner);
        message = winner == 0 ? "Террористы победили"
        : winner == 1 ? "Спецназ победил"
        : "Ничья";

        // the MVP of the round (normal games); the round win sound and the HUD listen to RoundEnded
        LastMvp = null;
        LastMvpKills = 0;
        if (!simulation && !Solo && winner >= 0)
        {
            LastMvp = PickMvp(winner, out int mvpKills);
            LastMvpKills = mvpKills;
            if (LastMvp != null)
            {
                mvps.TryGetValue(LastMvp, out int count);
                mvps[LastMvp] = count + 1;
                message += "\n★ MVP раунда: " + (LastMvp == player ? "ты" : LastMvp.name) + " — " + KillsWord(mvpKills);
            }
        }
        RoundEnded?.Invoke(winner, LastMvp, LastMvpKills);

        if (simulation)
        {
            float duration = Mathf.Clamp(roundTime - timer, 0f, roundTime);
            LogRound(winner, alive0, alive1, duration);

            if (roundsPlayed >= roundsToSimulate)
            {
                FinishSimulation();
                return;
            }

            inBreak = true;
            timer = simBreakTime;
            return;
        }

        // more than half of the rounds won, or all rounds played (a draw at equal score)
        if (score[0] >= RoundsToWin || score[1] >= RoundsToWin || roundsPlayed >= Mathf.Max(1, maxRounds))
        {
            matchOver = true;
            if (score[0] == score[1]) message += "\nНичья";
            message += "\nМатч окончен. R — новый матч";
            return;
        }

        // the next round starts with the side switch
        if (HalftimeDue) message += "\n" + HalftimeText;

        inBreak = true;
        timer = breakTime;
    }

    void LogRound(int winner, int alive0, int alive1, float duration)
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F2},{9:F2},{10:F2},{11:F2},{12},{13},{14},{15},{16},{17},{18},{19:F2},{20:F2}\n",
            roundsPlayed, winner, alive0, alive1, Size(0), Size(1),
            stealthLevel0, stealthLevel1, speedPenalty, radiusFactor, revealTime, duration,
            weaponTeam0 != null ? weaponTeam0.weaponName : "default",
            weaponTeam1 != null ? weaponTeam1.weaponName : "default",
            bhopLevel0, bhopLevel1, SceneManager.GetActiveScene().name,
            Weapon.PenetrationEnabled ? 1 : 0,
            Weapon.FriendlyFire ? 1 : 0,
            botSneakChance,
            botCrouchChance);
        File.AppendAllText(csvPath, line);
    }

    void FinishSimulation()
    {
        matchOver = true;
        Time.timeScale = 1f;
        Debug.Log("Simulation done. Rounds: " + roundsPlayed + ". File: " + csvPath);

#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    void ResetMatch()
    {
        // the bots added by the console played only in the last match
        RetireAddedBots();

        // a new match starts on the first sides again (the next spawn raises Halftime for the listeners)
        if (sidesSwapped) SwapTeams();
        halftimeDone = false;

        score[0] = 0;
        score[1] = 0;
        mvps.Clear();
        LastMvp = null;
        roundsPlayed = 0;
        matchOver = false;

        // a new match: $800 and the default weapons for everybody again
        Economy.ResetAll();
        lossesInRow[0] = 0;
        lossesInRow[1] = 0;
        freshLoadouts = true;
        Scoreboard board = FindAnyObjectByType<Scoreboard>();
        if (board != null) board.ResetStats();
        StartRound();
    }

    // ---------------- for the developer console ----------------
    // RoundManager.Current gives the instance. Nothing here works in simulations (the console never runs there).

    // the bots added by SpawnBotsNear in this match
    readonly List<Health> addedBots = new List<Health>();
    readonly List<Health> victims = new List<Health>();

    // A new match, like R after the end of a match: the first sides again, score 0:0, $800 and the default weapons for
    // everybody, the bots added by the console leave. The solo mode only puts the player back on his spawn.
    // False in simulations.
    public bool RestartMatch()
    {
        if (simulation) return false;
        if (Solo)
        {
            BeginPlacement();
            Spawn(0, spawnRoot0);
            RoundStarted?.Invoke();
            return true;
        }
        ResetMatch();
        return true;
    }

    // Ends the current round at once: winner 0 = T, 1 = CT, -1 = a draw. The score, the round money, the MVP and the
    // halftime work like after a normal round end (the next round starts after the break).
    // False when there is no running round (simulation, solo mode, the break between rounds, the match is over).
    public bool EndRoundNow(int winner)
    {
        if (!NormalGame || inBreak || matchOver) return false;
        if (winner < -1 || winner > 1) winner = -1;
        InFreezeTime = false;
        FreezeTimeLeft = 0f;
        FinishRound(winner, Alive(0), Alive(1));
        return true;
    }

    // Kills the living bots of a team (0 = T, 1 = CT, -1 = all bots), never the player or the training target.
    // They die like falling out of the map (no killer, no kill money); in a normal game the round ends as usual when
    // a team has nobody left (after the freeze time, if it is running). Returns how many died.
    public int KillTeam(int team)
    {
        if (simulation) return 0;
        victims.Clear();
        foreach (Health h in Health.All)
        {
            if (h == null || h.IsDead || !h.gameObject.activeInHierarchy || h.invulnerable) continue;
            if (h.GetComponent<BotController>() == null) continue;
            if (team >= 0 && h.team != team) continue;
            victims.Add(h);
        }

        int killed = 0;
        foreach (Health h in victims)
        {
            if (h == null || h.IsDead) continue;
            h.TakeDamage(h.maxHealth * 10f);
            h.ApplyPending();
            if (h.IsDead) killed++;
        }
        victims.Clear();
        return killed;
    }

    // Adds count bots of a team (0 = T, 1 = CT) on free floor spots in front of position (forward = the view
    // direction; a few metres ahead, at least SpawnSpacing from everybody alive, not behind a wall). They are made like
    // the bots of the match start (name T_n / CT_n, stealth, bhop, team colour, BotBuyer with the default pistol; they
    // buy if the buy time runs), look back at position and play in their team until the end of the match (every round
    // they spawn with the team; a new match removes them). Returns how many were added (0 in simulations, without a
    // bot prefab, for a wrong team or when there is no free place).
    public int SpawnBotsNear(int team, int count, Vector3 position, Vector3 forward)
    {
        if (simulation || botPrefab == null || team < 0 || team > 1 || count <= 0) return 0;
        count = Mathf.Min(count, MaxSpawnNear);

        forward.y = 0f;
        forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);
        Quaternion facing = Quaternion.LookRotation(-forward, Vector3.up);
        Vector3 centre = position + forward * 3f;

        // everybody alive keeps his place: the new bots stand beside them, not inside
        placed.Clear();
        foreach (Health h in Health.All)
            if (h != null && !h.IsDead && h.gameObject.activeInHierarchy) placed.Add(h.transform.position);

        int added = 0;
        for (int k = 0; k < count; k++)
        {
            if (!FindFreeSpot(position, centre, side, out Vector3 spot)) break;

            GameObject bot = CreateBot(team, BotsInTeam(team) + 1, spot + Vector3.up, facing);
            Health h = bot.GetComponent<Health>();
            Place(h, spot, facing);
            placed.Add(spot);
            addedBots.Add(h);

            Bhop bh = bot.GetComponent<Bhop>();
            if (bh == null) bh = bot.AddComponent<Bhop>();
            bh.level = team == 0 ? bhopLevel0 : bhopLevel1;
            bh.ResetBonus();

            BotBuyer buyer = bot.GetComponent<BotBuyer>();
            if (buyer == null) buyer = bot.AddComponent<BotBuyer>();
            buyer.NewRound(false);
            if (Economy.Enabled && Economy.InBuyTime) buyer.Buy();
            buyer.EquipBest();

            // the character model at once (the bots of the match start get it at the round start)
            if (CharacterBody.VisualsAllowed() && bot.GetComponent<CharacterBody>() == null) bot.AddComponent<CharacterBody>();
            added++;
        }
        return added;
    }

    // true: the bots stand still and never shoot (they can be killed); false: they play again, and the main menu
    // option and -freezebots are off for this match too
    public void SetBotsFrozen(bool frozen)
    {
        if (simulation) return;
        botsFrozen = frozen;
        if (!frozen)
        {
            freezeBotsArg = false;
            GameSettings.BotsFrozen = false;
        }
        BotsFrozen = GameSettings.BotsFrozen || botsFrozen || freezeBotsArg;
    }

    // the bots of this team now (the player does not count)
    int BotsInTeam(int team)
    {
        int count = 0;
        foreach (Health h in Health.All)
            if (h != null && h.team == team && h.GetComponent<BotController>() != null) count++;
        return count;
    }

    // a new match: the bots added by the console leave. They are taken off the player list and hidden, like a dead bot
    // that never comes back - not destroyed, because other scripts keep players in their tables.
    void RetireAddedBots()
    {
        foreach (Health h in addedBots)
        {
            if (h == null) continue;
            h.Unregister();
            teamRenderers.Remove(h);
            mvps.Remove(h);
            h.gameObject.SetActive(false);
        }
        addedBots.Clear();
    }

    // GameHud draws the score, the timer and the round messages itself and sets this
    public static bool HudDrawsScore;

    // for the HUD
    public float TimeLeft => Mathf.Max(timer, 0f);
    public bool InBreak => inBreak;
    public bool MatchOver => matchOver;
    public string Message => message;
    public bool IsSolo => Solo;

    void OnGUI()
    {
        if (HudDrawsScore && !simulation) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 32;
            style.normal.textColor = Color.white;
        }

        float cx = Screen.width / 2f;

        if (Solo)
        {
            GUI.contentColor = Color.white;
            style.alignment = TextAnchor.UpperCenter;
            GUI.Label(new Rect(cx - 300, 10, 600, 50), "SOLO TEST", style);
            return;
        }
        string center = simulation
            ? roundsPlayed + "/" + roundsToSimulate
            : InFreezeTime ? "Закупка " + Mathf.CeilToInt(FreezeTimeLeft)
            : Mathf.CeilToInt(Mathf.Max(timer, 0f)) + " s";

        GUI.contentColor = new Color(1f, 0.35f, 0.35f);
        style.alignment = TextAnchor.UpperRight;
        GUI.Label(new Rect(cx - 260, 10, 180, 50), "T  " + score[0], style);

        GUI.contentColor = Color.white;
        style.alignment = TextAnchor.UpperCenter;
        GUI.Label(new Rect(cx - 100, 10, 200, 50), center, style);

        GUI.contentColor = new Color(0.4f, 0.6f, 1f);
        style.alignment = TextAnchor.UpperLeft;
        GUI.Label(new Rect(cx + 80, 10, 180, 50), score[1] + "  CT", style);

        GUI.contentColor = Color.white;
        style.alignment = TextAnchor.UpperCenter;
        if (message != "")
            GUI.Label(new Rect(cx - 300, 70, 600, 100), message, style);
    }
}