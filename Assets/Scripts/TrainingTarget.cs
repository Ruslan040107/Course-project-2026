using System.Collections.Generic;
using UnityEngine;

// Immortal target dummy for solo test mode: shows every hit and some stats.
[RequireComponent(typeof(Health))]
public class TrainingTarget : MonoBehaviour
{
    public int team = 1;
    public float popupTime = 1.2f;
    public float dpsWindow = 1f;
    public KeyCode resetKey = KeyCode.T;

    struct Popup
    {
        public string text;
        public Vector3 pos;
        public float born;
        public bool head;
    }

    struct Hit
    {
        public float time;
        public float damage;
    }

    Health health;
    PlayerController player;
    readonly List<Popup> popups = new List<Popup>();
    readonly List<Hit> window = new List<Hit>();

    float lastDamage;
    bool lastHead;
    float totalDamage;
    int hits;
    int shots;

    GUIStyle panelStyle;
    GUIStyle popupStyle;

    void Awake()
    {
        health = GetComponent<Health>();
        health.team = team;
        health.invulnerable = true;
    }

    void OnEnable()
    {
        health.DamageTaken += OnDamage;
        Weapon.AnyShot += OnShot;
    }

    void OnDisable()
    {
        health.DamageTaken -= OnDamage;
        Weapon.AnyShot -= OnShot;
    }

    void Start()
    {
        player = FindAnyObjectByType<PlayerController>();
    }

    void OnShot(ShotInfo s)
    {
        if (s.shooter == null || s.shooter.team == health.team) return;
        shots++;
    }

    void OnDamage(float amount, Health attacker, bool head)
    {
        lastDamage = amount;
        lastHead = head;
        totalDamage += amount;
        hits++;

        window.Add(new Hit { time = Time.time, damage = amount });

        popups.Add(new Popup
        {
            text = Mathf.RoundToInt(amount).ToString(),
            pos = transform.position + Vector3.up * 1.2f + Random.insideUnitSphere * 0.3f,
            born = Time.time,
            head = head
        });
    }

    void Update()
    {
        if (Input.GetKeyDown(resetKey)) ResetStats();

        window.RemoveAll(h => Time.time - h.time > dpsWindow);
        popups.RemoveAll(p => Time.time - p.born > popupTime);
    }

    void ResetStats()
    {
        lastDamage = 0f;
        lastHead = false;
        totalDamage = 0f;
        hits = 0;
        shots = 0;
        window.Clear();
        popups.Clear();
    }

    void OnGUI()
    {
        if (panelStyle == null)
        {
            panelStyle = new GUIStyle(GUI.skin.label);
            panelStyle.fontSize = 20;
            panelStyle.normal.textColor = Color.white;

            popupStyle = new GUIStyle(GUI.skin.label);
            popupStyle.fontSize = 26;
            popupStyle.fontStyle = FontStyle.Bold;
            popupStyle.alignment = TextAnchor.MiddleCenter;
        }

        DrawPopups();
        DrawPanel();
    }

    void DrawPopups()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        foreach (Popup p in popups)
        {
            Vector3 sp = cam.WorldToScreenPoint(p.pos);
            if (sp.z <= 0f) continue;

            float age = (Time.time - p.born) / popupTime;
            float x = sp.x;
            float y = Screen.height - sp.y - age * 70f;

            Color c = p.head ? new Color(1f, 0.85f, 0.2f) : Color.white;
            c.a = 1f - age;

            popupStyle.normal.textColor = c;
            GUI.Label(new Rect(x - 60f, y - 20f, 120f, 40f), p.text, popupStyle);
        }
    }

    void DrawPanel()
    {
        float dps = 0f;
        foreach (Hit h in window) dps += h.damage;
        dps /= Mathf.Max(dpsWindow, 0.01f);

        string shotsToKill = lastDamage > 0f ? Mathf.CeilToInt(health.maxHealth / lastDamage).ToString() : "-";
        string accuracy = shots > 0 ? Mathf.RoundToInt(100f * hits / shots) + "%" : "-";
        string distance = player != null ? Vector3.Distance(player.transform.position, transform.position).ToString("F1") + " m" : "-";
        string last = lastDamage > 0f ? Mathf.RoundToInt(lastDamage) + (lastHead ? "  (HEAD)" : "") : "-";

        Rect box = new Rect(20f, 80f, 330f, 190f);
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(box, Texture2D.whiteTexture);
        GUI.color = Color.white;

        string text =
            "TRAINING TARGET\n" +
            "Last hit: " + last + "\n" +
            "Shots to kill (100 HP): " + shotsToKill + "\n" +
            "Total damage: " + Mathf.RoundToInt(totalDamage) + "\n" +
            "Hits / shots: " + hits + " / " + shots + "  (" + accuracy + ")\n" +
            "Damage in last " + dpsWindow.ToString("F0") + " s: " + Mathf.RoundToInt(dps) + "\n" +
            "Distance: " + distance + "\n" +
            "[" + resetKey + "] reset";

        GUI.Label(new Rect(box.x + 10f, box.y + 6f, box.width - 20f, box.height - 12f), text, panelStyle);
    }
}
