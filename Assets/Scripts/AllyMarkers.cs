using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// CS-like markers over the living teammates of the human player: the name (T_2) above the head and a small arrow
// under it pointing down at him, in the team colour (T orange, CT blue).
// - The farther the teammate, the more transparent: fully visible at nearDistance or closer, farAlpha at
//   farDistance or farther.
// - Nothing for a teammate behind the camera; through walls the marker is still shown, but dimmer.
// - Never for enemies. While the dead player watches a teammate, the one the camera is in has no marker.
// - Teams and colours come from Health.team every frame: after the halftime (RoundManager.Halftime, T <-> CT) the
//   new teammates get markers in the colour of their new side.
// It adds itself to every scene with a RoundManager and a player, never in simulations or without graphics.
public class AllyMarkers : MonoBehaviour
{
    [Header("Fade")]
    [Tooltip("fully visible up to this distance, m")] public float nearDistance = 8f;
    [Tooltip("faded to farAlpha from this distance, m")] public float farDistance = 45f;
    [Range(0f, 1f)] public float farAlpha = 0.15f;
    [Tooltip("alpha multiplier when a wall is between the camera and the teammate")] [Range(0f, 1f)] public float behindWallFactor = 0.55f;
    [Tooltip("seconds between the line of sight checks of one teammate")] public float checkInterval = 0.15f;

    [Header("Look")]
    [Tooltip("meters above the top of the head")] public float heightAboveHead = 0.22f;
    [Tooltip("font size at 1080p, it scales with the screen height")] public float fontSize = 17f;
    [Tooltip("arrow width at 1080p, pixels")] public float arrowSize = 13f;
    public Color tColor = new Color(1f, 0.55f, 0.15f, 1f);
    public Color ctColor = new Color(0.35f, 0.62f, 1f, 1f);

    class Mark
    {
        public bool clear = true;   // no wall between the camera and the teammate
        public float nextCheck;
    }

    static AllyMarkers instance;

    public PlayerController player;
    Health playerHealth;
    readonly Dictionary<Health, Mark> marks = new Dictionary<Health, Mark>();
    readonly List<Health> gone = new List<Health>();
    readonly RaycastHit[] rayHits = new RaycastHit[16];
    Texture2D arrow;
    GUIStyle style;
    readonly GUIContent content = new GUIContent();

    // ---------------- bootstrap ----------------

    // Play mode starts without a domain reload
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
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

    // markers for a scene with rounds and a human player, never in a simulation or without graphics
    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        PlayerController pc = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<AllyMarkers>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
            if (pc == null) pc = root.GetComponentInChildren<PlayerController>(true);
        }
        if (rm == null || pc == null || rm.simulation) return;

        GameObject go = new GameObject("Ally Markers");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<AllyMarkers>().player = pc;
    }

    void Awake()
    {
        // background runs pass -sim on the command line; RoundManager reads it only in its Start
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        if ((rm != null && rm.simulation) || SimulationFlag() || Application.isBatchMode)
        {
            enabled = false;
            return;
        }
        instance = this;
    }

    void Start()
    {
        if (player == null) player = FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            enabled = false;
            return;
        }
        playerHealth = player.GetComponent<Health>();
        arrow = MakeArrow(32);
    }

    void OnEnable()
    {
        RoundManager.Halftime += OnHalftime;
    }

    void OnDisable()
    {
        RoundManager.Halftime -= OnHalftime;
    }

    // the sides swapped: the old teammates are enemies now, the new ones get a fresh line of sight check
    void OnHalftime()
    {
        marks.Clear();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (arrow != null) Destroy(arrow);
    }

    // a triangle pointing down, white (tinted when drawn), with a soft edge
    static Texture2D MakeArrow(int n)
    {
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.HideAndDontSave;
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            // texture rows go up: the tip is at the bottom row, the wide side at the top
            float v = (y + 0.5f) / n;
            float half = 0.5f * v;
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n - 0.5f;
                float inside = Mathf.Clamp01((half - Mathf.Abs(u)) * n * 0.8f);
                px[y * n + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * inside));
            }
        }
        tex.SetPixels32(px);
        tex.Apply(false, true);
        return tex;
    }

    void Update()
    {
        // forget the teammates that are gone
        foreach (Health h in marks.Keys)
            if (h == null) gone.Add(h);
        foreach (Health h in gone) marks.Remove(h);
        gone.Clear();
    }

    // the teammates of the player the markers are drawn for
    bool IsAlly(Health h, Camera cam)
    {
        if (h == null || h == playerHealth || playerHealth == null) return false;
        if (h.team != playerHealth.team || h.IsDead || !h.gameObject.activeInHierarchy) return false;
        if (h.GetComponent<TrainingTarget>() != null) return false;
        // the teammate the camera is in (the dead player watches him)
        return !cam.transform.IsChildOf(h.transform);
    }

    // where the marker points: just above the head (it follows a crouch)
    static Vector3 HeadPoint(Health h)
    {
        CharacterBody cb = h.GetComponent<CharacterBody>();
        if (cb != null) return cb.HeadTop();
        Collider c = h.GetComponent<Collider>();
        if (c != null) return new Vector3(c.bounds.center.x, c.bounds.max.y, c.bounds.center.z);
        return h.transform.position + Vector3.up;
    }

    // nothing but characters between the camera and the point
    bool ClearLine(Vector3 from, Vector3 to)
    {
        Vector3 dir = to - from;
        float d = dir.magnitude;
        if (d < 0.01f) return true;
        int n = Physics.RaycastNonAlloc(from, dir / d, rayHits, d, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
            if (rayHits[i].collider.GetComponentInParent<Health>() == null) return false;
        return true;
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint || playerHealth == null || arrow == null) return;
        if (MainMenu.IsOpen || Time.timeScale <= 0f) return;
        Camera cam = Camera.main;
        if (cam == null) return;

        GUI.depth = 10;   // under the HUD
        float k = Screen.height / 1080f;
        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.LowerCenter;
            style.wordWrap = false;
            style.clipping = TextClipping.Overflow;
            style.padding = new RectOffset(0, 0, 0, 0);
        }

        Vector3 camPos = cam.transform.position;
        foreach (Health h in Health.All)
        {
            if (!IsAlly(h, cam)) continue;

            Vector3 head = HeadPoint(h);
            Vector3 p = head + Vector3.up * heightAboveHead;
            Vector3 sp = cam.WorldToScreenPoint(p);
            if (sp.z <= 0.05f) continue;   // behind the camera

            if (!marks.TryGetValue(h, out Mark m))
            {
                m = new Mark();
                marks[h] = m;
            }
            if (Time.time >= m.nextCheck)
            {
                m.nextCheck = Time.time + checkInterval;
                m.clear = ClearLine(camPos, head - Vector3.up * 0.1f);
            }

            float distance = Vector3.Distance(camPos, p);
            float far = Mathf.InverseLerp(nearDistance, farDistance, distance);
            float alpha = Mathf.Lerp(1f, farAlpha, far) * (m.clear ? 1f : behindWallFactor);
            float size = k * Mathf.Lerp(1f, 0.8f, far);

            Color team = h.team == 0 ? tColor : ctColor;
            float x = sp.x;
            float y = Screen.height - sp.y;

            // the arrow: its tip at the point above the head
            float aw = Mathf.Max(6f, arrowSize * size);
            float ah = aw * 0.75f;
            Rect ar = new Rect(Mathf.Round(x - aw * 0.5f), Mathf.Round(y - ah), Mathf.Round(aw), Mathf.Round(ah));
            GUI.color = new Color(0f, 0f, 0f, 0.5f * alpha);
            GUI.DrawTexture(new Rect(ar.x + 1f, ar.y + 1f, ar.width, ar.height), arrow);
            GUI.color = new Color(team.r, team.g, team.b, alpha);
            GUI.DrawTexture(ar, arrow);

            // the name above the arrow, with a shadow
            style.fontSize = Mathf.Max(8, Mathf.RoundToInt(fontSize * size));
            content.text = h.name;
            Vector2 ts = style.CalcSize(content);
            Rect tr = new Rect(Mathf.Round(x - ts.x * 0.5f), Mathf.Round(ar.y - ts.y - 1f * k), ts.x, ts.y);
            Color nameColor = Color.Lerp(Color.white, team, 0.45f);
            GUI.color = Color.white;
            style.normal.textColor = new Color(0f, 0f, 0f, 0.6f * alpha);
            GUI.Label(new Rect(tr.x + 1f, tr.y + 1f, tr.width, tr.height), content, style);
            style.normal.textColor = new Color(nameColor.r, nameColor.g, nameColor.b, alpha);
            GUI.Label(tr, content, style);
        }
        GUI.color = Color.white;
    }
}
