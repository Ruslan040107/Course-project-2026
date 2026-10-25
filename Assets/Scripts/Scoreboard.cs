using System.Collections.Generic;
using UnityEngine;

// Hold TAB to see the score table. Collects kills, deaths, assists, headshots and damage; shows the MVP stars of the match.
public class Scoreboard : MonoBehaviour
{
    public KeyCode key = KeyCode.Tab;

    [Header("Texts")]
    public string titleT = "TERRORISTS";
    public string titleCT = "COUNTER-TERRORISTS";
    public string colKills = "K";
    public string colDeaths = "D";
    public string colAssists = "A";
    public string colHeadshots = "HS%";
    public string colDamage = "DMG";
    public string youName = "You";
    public string botName = "Bot";
    public string aliveText = "Alive";

    class Stats
    {
        public int kills;
        public int deaths;
        public int assists;
        public int headshots;
        public float damage;
    }

    class Row
    {
        public Health h;
        public string name;
        public Stats st;
    }

    readonly Dictionary<Health, Stats> stats = new Dictionary<Health, Stats>();

    // a new match (RoundManager.ResetMatch) starts with an empty table
    public void ResetStats() { stats.Clear(); }

    RoundManager rounds;
    PlayerController player;
    Health playerHealth;

    GUIStyle bigStyle, titleStyle, smallStyle, nameStyle, numStyle;

    static readonly Color tColor = new Color(1f, 0.45f, 0.4f);
    static readonly Color ctColor = new Color(0.5f, 0.7f, 1f);

    // centers of the number columns, in pixels at 1080p
    static readonly float[] colX = { 560f, 650f, 740f, 840f, 930f };

    void OnEnable()
    {
        Health.AnyKilled += OnKilled;
        Health.AnyDamaged += OnDamaged;
    }

    void OnDisable()
    {
        Health.AnyKilled -= OnKilled;
        Health.AnyDamaged -= OnDamaged;
    }

    Stats Get(Health h)
    {
        if (!stats.TryGetValue(h, out Stats s))
        {
            s = new Stats();
            stats[h] = s;
        }
        return s;
    }

    void OnDamaged(Health victim, Health attacker, float amount, bool head)
    {
        if (victim == null || attacker == null || attacker == victim) return;
        if (attacker.team == victim.team || victim.invulnerable) return;

        Get(attacker).damage += amount;
    }

    void OnKilled(KillInfo info)
    {
        Kill(info.victim, info.killer, info.headshot, info.assister);
    }

    void Kill(Health victim, Health killer, bool head, Health assister)
    {
        if (victim == null || victim.invulnerable) return;

        Get(victim).deaths++;

        if (killer != null && killer != victim && killer.team != victim.team)
        {
            Stats k = Get(killer);
            k.kills++;
            if (head) k.headshots++;
        }

        if (assister != null && assister != victim) Get(assister).assists++;
    }

    List<Row> Roster(int team)
    {
        List<Row> list = new List<Row>();
        int bot = 0;

        foreach (Health h in Health.All)
        {
            if (h == null || h.team != team || h.invulnerable) continue;

            bool isPlayer = h == playerHealth;
            list.Add(new Row
            {
                h = h,
                name = isPlayer ? youName : botName + " " + (++bot),
                st = Get(h)
            });
        }

        list.Sort((a, b) =>
        {
            int c = b.st.kills.CompareTo(a.st.kills);
            return c != 0 ? c : b.st.damage.CompareTo(a.st.damage);
        });

        return list;
    }

    void BuildStyles(float s)
    {
        if (bigStyle == null)
        {
            bigStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
            smallStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
            nameStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft };
            numStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter };
        }

        bigStyle.fontSize = Mathf.RoundToInt(60f * s);
        titleStyle.fontSize = Mathf.RoundToInt(28f * s);
        smallStyle.fontSize = Mathf.RoundToInt(20f * s);
        nameStyle.fontSize = Mathf.RoundToInt(24f * s);
        numStyle.fontSize = Mathf.RoundToInt(24f * s);
    }

    static void Fill(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void OnGUI()
    {
        // Tab completes commands in the developer console while it is open
        if (!Input.GetKey(key) || DevConsole.IsOpen) return;

        if (rounds == null) rounds = FindAnyObjectByType<RoundManager>();
        if (player == null)
        {
            player = FindAnyObjectByType<PlayerController>();
            playerHealth = player != null ? player.GetComponent<Health>() : null;
        }

        float s = Screen.height / 1080f;
        BuildStyles(s);

        int myTeam = playerHealth != null ? playerHealth.team : 0;
        List<Row> mine = Roster(myTeam);
        List<Row> other = Roster(1 - myTeam);

        float rowH = 38f * s;
        float headH = 78f * s;
        float colH = 30f * s;
        float gap = 22f * s;
        float pad = 26f * s;
        float w = 1000f * s;

        int blocks = (mine.Count > 0 ? 1 : 0) + (other.Count > 0 ? 1 : 0);
        float h = blocks * (headH + colH) + (mine.Count + other.Count) * rowH + (blocks > 1 ? gap : 0f);

        float x = (Screen.width - w) * 0.5f;
        float y = (Screen.height - h) * 0.5f;

        Fill(new Rect(x - pad, y - pad, w + pad * 2f, h + pad * 2f), new Color(0.05f, 0.06f, 0.07f, 0.88f));

        if (mine.Count > 0) DrawTeam(x, ref y, w, s, myTeam, mine);
        if (mine.Count > 0 && other.Count > 0) y += gap;
        if (other.Count > 0) DrawTeam(x, ref y, w, s, 1 - myTeam, other);
    }

    void DrawTeam(float x, ref float y, float w, float s, int team, List<Row> rows)
    {
        Color col = team == 0 ? tColor : ctColor;
        float rowH = 38f * s;
        float headH = 78f * s;
        float colH = 30f * s;

        int alive = 0;
        foreach (Row r in rows) if (!r.h.IsDead) alive++;

        int score = 0;
        if (rounds != null && rounds.score != null && team < rounds.score.Length) score = rounds.score[team];

        bigStyle.normal.textColor = col;
        GUI.Label(new Rect(x, y, 110f * s, headH), score.ToString(), bigStyle);

        titleStyle.normal.textColor = col;
        GUI.Label(new Rect(x + 125f * s, y + 6f * s, 600f * s, 40f * s), team == 0 ? titleT : titleCT, titleStyle);

        smallStyle.alignment = TextAnchor.MiddleLeft;
        smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
        GUI.Label(new Rect(x + 125f * s, y + 44f * s, 400f * s, 28f * s), aliveText + " " + alive + "/" + rows.Count, smallStyle);
        smallStyle.alignment = TextAnchor.MiddleCenter;
        y += headH;

        string[] heads = { colKills, colDeaths, colAssists, colHeadshots, colDamage };
        smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.55f);
        for (int i = 0; i < heads.Length; i++)
            GUI.Label(new Rect(x + (colX[i] - 45f) * s, y, 90f * s, colH), heads[i], smallStyle);
        y += colH;

        foreach (Row r in rows)
        {
            bool me = r.h == playerHealth;
            bool dead = r.h.IsDead;

            if (me) Fill(new Rect(x, y, w, rowH), new Color(1f, 1f, 1f, 0.12f));

            Color tc = dead ? new Color(1f, 1f, 1f, 0.4f) : Color.white;
            nameStyle.normal.textColor = tc;
            numStyle.normal.textColor = tc;

            GUI.Label(new Rect(x + 125f * s, y, 400f * s, rowH), r.name, nameStyle);

            // MVP stars of the match (RoundManager: the best player of a won round), like CS
            int mvp = rounds != null ? rounds.MvpCount(r.h) : 0;
            if (mvp > 0)
            {
                numStyle.normal.textColor = dead ? new Color(1f, 0.82f, 0.3f, 0.45f) : new Color(1f, 0.82f, 0.3f, 1f);
                GUI.Label(new Rect(x + 440f * s, y, 70f * s, rowH), mvp > 1 ? "★" + mvp : "★", numStyle);
                numStyle.normal.textColor = tc;
            }

            int hs = r.st.kills > 0 ? Mathf.RoundToInt(100f * r.st.headshots / r.st.kills) : 0;
            string[] vals =
            {
                r.st.kills.ToString(),
                r.st.deaths.ToString(),
                r.st.assists.ToString(),
                hs + "%",
                Mathf.RoundToInt(r.st.damage).ToString()
            };

            for (int i = 0; i < vals.Length; i++)
                GUI.Label(new Rect(x + (colX[i] - 45f) * s, y, 90f * s, rowH), vals[i], numStyle);

            y += rowH;
        }
    }
}