using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.AI;
using System.Collections;

public class RoundManager : MonoBehaviour
{
    [Header("Scene")]
    public Transform spawnRoot0;
    public Transform spawnRoot1;
    public GameObject botPrefab;
    public Health player;
    public Camera overviewCamera;
    public Material[] teamMaterials;

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
            csvPath = Path.Combine(Application.persistentDataPath, "sim_" + stamp + ".csv");
            File.WriteAllText(csvPath, "round,winner,alive0,alive1,size0,size1,duration\n");
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
    }

    void Update()
    {
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
        for (int team = 0; team < 2; team++)
        {
            int count = botsPerTeam;
            if (team == 0 && player != null && !simulation) count--;

            Transform root = team == 0 ? spawnRoot0 : spawnRoot1;

            for (int i = 0; i < count; i++)
            {
                Transform point = root.GetChild(i % root.childCount);
                GameObject bot = Instantiate(botPrefab, point.position + Vector3.up, point.rotation);
                bot.name = "Bot_T" + team + "_" + i;
                bot.GetComponent<Health>().team = team;

                if (teamMaterials != null && team < teamMaterials.Length)
                    foreach (Renderer r in bot.GetComponentsInChildren<Renderer>())
                        r.sharedMaterial = teamMaterials[team];
            }
        }
    }

    void StartRound()
    {
        Spawn(0, spawnRoot0);
        Spawn(1, spawnRoot1);
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
        message = winner >= 0 ? "Team " + winner + " wins the round" : "Draw";

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
            "{0},{1},{2},{3},{4},{5},{6:F2}\n",
            roundsPlayed, winner, alive0, alive1, Size(0), Size(1), duration);
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
            style.alignment = TextAnchor.UpperCenter;
            style.normal.textColor = Color.white;
        }

        string top = simulation
            ? "SIM " + roundsPlayed + "/" + roundsToSimulate + "   " + score[0] + " : " + score[1]
            : score[0] + " : " + score[1] + "    " + Mathf.CeilToInt(Mathf.Max(timer, 0f)) + " s";

        GUI.Label(new Rect(Screen.width / 2f - 300, 10, 600, 50), top, style);

        if (message != "")
            GUI.Label(new Rect(Screen.width / 2f - 300, 70, 600, 100), message, style);
    }
}