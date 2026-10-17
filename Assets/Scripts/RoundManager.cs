using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
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

    void Start()
    {
        if (overviewCamera != null) overviewCamera.gameObject.SetActive(simulation);

        Weapon.InfiniteAmmo = Solo;
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

            string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            string dir;
            #if UNITY_EDITOR
            dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Analysis", "data");
            #else
            dir = Application.persistentDataPath;
            #endif
            Directory.CreateDirectory(dir);

            csvPath = Path.Combine(dir, "sim_" + stamp + ".csv");
            File.WriteAllText(csvPath, "round,winner,alive0,alive1,size0,size1,stealth0,stealth1,penalty,radius,reveal,duration,weapon0,weapon1\n");
            Debug.Log("Simulation results: " + csvPath);
        }

        SpawnBots();
        StartRound();
        StartCoroutine(ResolveDamage());
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
            if (!simulation && Input.GetKeyDown(KeyCode.R)) ResetMatch();
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
        message = winner == 0 ? "Terrorists win the round"
        : winner == 1 ? "Counter-Terrorists win the round"
        : "Draw";

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
            message += "\nMatch over. Press R";
            return;
        }

        inBreak = true;
        timer = breakTime;
    }

    void LogRound(int winner, int alive0, int alive1, float duration)
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3},{4},{5},{6},{7},{8:F2},{9:F2},{10:F2},{11:F2},{12},{13}\n",
            roundsPlayed, winner, alive0, alive1, Size(0), Size(1),
            stealthLevel0, stealthLevel1, speedPenalty, radiusFactor, revealTime, duration,
            weaponTeam0 != null ? weaponTeam0.weaponName : "default",
            weaponTeam1 != null ? weaponTeam1.weaponName : "default");
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

    void OnGUI()
    {
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