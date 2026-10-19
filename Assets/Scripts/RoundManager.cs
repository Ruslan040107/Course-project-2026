using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using System.Collections;

public class RoundManager : MonoBehaviour
{
    public static event System.Action RoundStarted;
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

    [Header("Bhop for bots (paid movement upgrade, 0 = off)")]
    [Tooltip("the player's level is set on the Bhop component of the Player object")]
    public int bhopLevel0;
    public int bhopLevel1;

    [Header("Weapons (bots; leave empty to keep the prefab weapon)")]
    public WeaponData weaponTeam0;
    public WeaponData weaponTeam1;

    [Header("Solo test mode")]
    [Tooltip("you alone: no bots, no timer, infinite ammo, a training target")] public bool soloMode;
    public GameObject trainingTarget;

    [Header("Rules")]
    public int botsPerTeam = 5;
    public float roundTime = 60f;
    public float breakTime = 3f;
    public int roundsToWin = 5;

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

    static bool mapLoaded;

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

        if (overviewCamera != null) overviewCamera.gameObject.SetActive(simulation);

        Weapon.InfiniteAmmo = Solo;

        if (!simulation && FindAnyObjectByType<Scoreboard>() == null) gameObject.AddComponent<Scoreboard>();
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
            File.WriteAllText(csvPath, "round,winner,alive0,alive1,size0,size1,stealth0,stealth1,penalty,radius,reveal,duration,weapon0,weapon1,bhop0,bhop1,map,penetration,ff,sneak\n");
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

        bhopLevel0 = ArgInt("-bhop0", bhopLevel0);
        bhopLevel1 = ArgInt("-bhop1", bhopLevel1);

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
    }

    void Update()
    {
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

        timer -= Time.deltaTime;

        if (inBreak)
        {
            if (timer <= 0f) StartRound();
            return;
        }

        int alive0 = Alive(0);
        int alive1 = Alive(1);
        if (alive0 == 0 || alive1 == 0 || timer <= 0f) EndRound(alive0, alive1);
    }

    void SpawnBots()
    {
        if (Solo) return;

        for (int team = 0; team < 2; team++)
        {
            int count = botsPerTeam;
            if (team == 0 && player != null && !simulation) count--;

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
                if (bc != null) bc.sneakChance = botSneakChance;

                if (teamMaterials != null && team < teamMaterials.Length)
                    foreach (Renderer r in bot.GetComponentsInChildren<Renderer>())
                        r.sharedMaterial = teamMaterials[team];
            }
        }
    }

    void StartRound()
    {
        Spawn(0, spawnRoot0);
        if (!Solo) Spawn(1, spawnRoot1);
        RoundStarted?.Invoke();
        timer = roundTime;
        inBreak = false;
        message = "";
    }

    void Spawn(int team, Transform root)
    {
        int i = 0;
        foreach (Health h in Health.All)
        {
            if (h.team != team) continue;

            Transform point = root.GetChild(i % root.childCount);
            i++;

            h.Revive();
            Place(h, point);

            // bots take the team level, the player keeps the level set on his own Bhop component
            bool isPlayer = h.GetComponent<PlayerController>() != null;
            Bhop bh = h.GetComponent<Bhop>();
            if (bh == null) bh = h.gameObject.AddComponent<Bhop>();
            if (!isPlayer) bh.level = team == 0 ? bhopLevel0 : bhopLevel1;
            bh.ResetBonus();
            Weapon w = h.GetComponentInChildren<Weapon>(true);
            if (w != null)
            {
                WeaponData d = team == 0 ? weaponTeam0 : weaponTeam1;
                if (d != null && h.GetComponent<PlayerLoadout>() == null) w.Equip(d);
                w.ResetAmmo();
            }
        }
    }

    void Place(Health h, Transform point)
    {
        NavMeshAgent agent = h.GetComponent<NavMeshAgent>();
        CharacterController cc = h.GetComponent<CharacterController>();

        if (agent != null)
        {
            agent.Warp(point.position);
        }
        else if (cc != null)
        {
            cc.enabled = false;
            h.transform.position = point.position + Vector3.up;
            cc.enabled = true;
        }
        else
        {
            h.transform.position = point.position + Vector3.up;
        }

        h.transform.rotation = Quaternion.Euler(0f, point.eulerAngles.y, 0f);
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
        message = winner == 0 ? "Террористы победили"
        : winner == 1 ? "Спецназ победил"
        : "Ничья";

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

        if (score[0] >= roundsToWin || score[1] >= roundsToWin)
        {
            matchOver = true;
            message += "\nМатч окончен. R — новый матч";
            return;
        }

        inBreak = true;
        timer = breakTime;
    }

    void LogRound(int winner, int alive0, int alive1, float duration)
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F2},{9:F2},{10:F2},{11:F2},{12},{13},{14},{15},{16},{17},{18},{19:F2}\n",
            roundsPlayed, winner, alive0, alive1, Size(0), Size(1),
            stealthLevel0, stealthLevel1, speedPenalty, radiusFactor, revealTime, duration,
            weaponTeam0 != null ? weaponTeam0.weaponName : "default",
            weaponTeam1 != null ? weaponTeam1.weaponName : "default",
            bhopLevel0, bhopLevel1, SceneManager.GetActiveScene().name,
            Weapon.PenetrationEnabled ? 1 : 0,
            Weapon.FriendlyFire ? 1 : 0,
            botSneakChance);
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
        score[0] = 0;
        score[1] = 0;
        roundsPlayed = 0;
        matchOver = false;
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
            : Mathf.CeilToInt(Mathf.Max(timer, 0f)) + " s";

        GUI.contentColor = new Color(1f, 0.35f, 0.35f);
        style.alignment = TextAnchor.UpperRight;
        GUI.Label(new Rect(cx - 260, 10, 180, 50), "T  " + score[0], style);

        GUI.contentColor = Color.white;
        style.alignment = TextAnchor.UpperCenter;
        GUI.Label(new Rect(cx - 70, 10, 140, 50), center, style);

        GUI.contentColor = new Color(0.4f, 0.6f, 1f);
        style.alignment = TextAnchor.UpperLeft;
        GUI.Label(new Rect(cx + 80, 10, 180, 50), score[1] + "  CT", style);

        GUI.contentColor = Color.white;
        style.alignment = TextAnchor.UpperCenter;
        if (message != "")
            GUI.Label(new Rect(cx - 300, 70, 600, 100), message, style);
    }
}