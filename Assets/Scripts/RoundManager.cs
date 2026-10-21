using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using System.Collections;

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
// - buy zones: one circle around each spawn (the farthest spawn point + buyZoneMargin), buying only inside it;
// - frozen bots (main menu option, botsFrozen in the Inspector or -freezebots): bots stand still and never shoot;
// - the number of players per team comes from the main menu (GameSettings.PendingBotsPerTeam) when it was chosen.
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
        public float radius;
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
    [Tooltip("buy zone radius = the farthest spawn point from the spawn centre + this (metres)")]
    public float buyZoneMargin = 5f;
    [Tooltip("the smallest buy zone radius (metres)")]
    public float minBuyZoneRadius = 6f;

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
                Spawn(0, spawnRoot0);
                RoundStarted?.Invoke();
            }
            return;
        }

        if (matchOver)
        {
            if (!simulation && !MainMenu.IsOpen && Input.GetKeyDown(KeyCode.R)) ResetMatch();
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
                GameObject bot = Instantiate(botPrefab, point.position + Vector3.up, point.rotation);
                bot.name = (team == 0 ? "T_" : "CT_") + (i + 1);
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
            }
        }
    }

    // ---------------- buy zones ----------------

    // one zone around each spawn: the centre of its points, the farthest point + buyZoneMargin (at least minBuyZoneRadius).
    // The zones stay with the side (team index): after the halftime everybody buys at his new spawn. None in solo / simulations.
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

        Vector3 centre = SpawnCentre(root);
        float farthest = 0f;
        for (int i = 0; i < root.childCount; i++)
        {
            Vector3 d = root.GetChild(i).position - centre;
            d.y = 0f;
            farthest = Mathf.Max(farthest, d.magnitude);
        }

        BuyZones.Add(new BuyZone
        {
            team = team,
            center = centre,
            radius = Mathf.Max(farthest + buyZoneMargin, minBuyZoneRadius)
        });
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

        Spawn(0, spawnRoot0);
        if (!Solo) Spawn(1, spawnRoot1);
        freshLoadouts = false;

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
        int i = 0;
        foreach (Health h in Health.All)
        {
            if (h.team != team) continue;

            Transform point = root.GetChild(i % root.childCount);
            // more players than spawn points (a big team from the main menu): the next ones stand around the points
            Vector3 position = simulation ? point.position : SpawnPosition(point, i / root.childCount, i);
            i++;

            // like CS: a survivor keeps his weapons and their ammo, a dead (or new) player gets the default ones
            bool keep = !freshLoadouts && !h.IsDead;

            h.Revive();
            Place(h, point, position);

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

    Quaternion SpawnFacing(Transform point)
    {
        Vector3 to = SpawnLookTarget() - point.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.25f) return Quaternion.Euler(0f, point.eulerAngles.y, 0f);
        return Quaternion.LookRotation(to.normalized, Vector3.up);
    }

    // lap 0: the spawn point itself; later laps: a free spot on the NavMesh 1.2 m (2.4 m ...) around it,
    // reachable from the point in a straight line (no wall between), else the point itself
    static Vector3 SpawnPosition(Transform point, int lap, int index)
    {
        Vector3 p = point.position;
        if (lap <= 0) return p;
        if (!NavMesh.SamplePosition(p, out NavMeshHit from, 2f, NavMesh.AllAreas)) return p;

        for (int k = 0; k < 8; k++)
        {
            float angle = index * 47f + k * 45f;
            Vector3 q = p + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * (1.2f * lap);
            if (!NavMesh.SamplePosition(q, out NavMeshHit hit, 0.6f, NavMesh.AllAreas)) continue;
            if (NavMesh.Raycast(from.position, hit.position, out NavMeshHit _, NavMesh.AllAreas)) continue;
            return new Vector3(hit.position.x, p.y, hit.position.z);
        }
        return p;
    }

    void Place(Health h, Transform point, Vector3 position)
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

        h.transform.rotation = SpawnFacing(point);

        // the player also looks straight ahead, not up or down as at the end of the last round
        PlayerController pc = h.GetComponent<PlayerController>();
        if (pc != null) pc.ResetLook();
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