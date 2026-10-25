using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// CS-like radar (minimap) in the top left corner for the human player.
// - Teammates: a light blue dot for every living teammate, it goes away only when he dies.
// - Enemies: a red dot only while somebody of the player's team sees them (in his field of view, with a clear
//   line of sight). When nobody sees an enemy any more, the dot turns into a red "?" where he was last seen.
//   The "?" stays until he is seen again, dies or a new round starts, and fades out after about 15 seconds.
//   Spotting is computed the same way for both teams (IsSpotted), only the player's team radar is drawn.
// - The map under the dots is a top-down layout of the level (floor, raised places, walls, covered floor like
//   tunnels), made once at the start from physics queries, so players and bodies never appear in it.
// - The radar turns with the view (forward is up), the player is the white arrow in the centre.
//   While the dead player watches a teammate, the radar is centred on that teammate.
// - Buy zones (RoundManager.BuyZones, normal games only): the player's team zone green, the enemy zone red,
//   a see-through rectangle with an outline (BuyZone.size along the world X and Z), turned with the map,
//   drawn over the map and under the dots and cut at the radar's edge.
// - Settings: GameSettings.RadarSize scales the radar on the screen, GameSettings.RadarZoom brings the map closer
//   (more zoom = fewer metres to the edge). Both are read every frame, so a change shows at once. The radar never
//   grows under the score at the top centre or over the boxes at the bottom left.
// It adds itself to every scene with a RoundManager and a player, never in simulations.
public class Radar : MonoBehaviour
{
    [Header("Look")]
    [Tooltip("meters from the centre to the edge at zoom 1 (GameSettings.RadarZoom divides it)")] public float range = 30f;
    [Tooltip("diameter in pixels at 1080p and size 1 (GameSettings.RadarSize multiplies it), scales with the screen height")]
    public float sizeAt1080 = 220f;
    [Tooltip("gap to the screen corner in pixels at 1080p")] public float margin = 16f;
    [Range(0f, 1f)] public float opacity = 0.85f;
    public Color allyColor = new Color(0.35f, 0.78f, 1f, 1f);
    public Color enemyColor = new Color(1f, 0.2f, 0.18f, 1f);
    public Color ownZoneColor = new Color(0.3f, 0.95f, 0.4f, 1f);
    public Color enemyZoneColor = new Color(1f, 0.27f, 0.22f, 1f);

    [Header("Spotting")]
    [Tooltip("seconds between the visibility checks")] public float checkInterval = 0.1f;
    [Tooltip("seconds the red dot takes to turn into a question mark after nobody sees the enemy")] public float fadeTime = 0.3f;
    [Tooltip("seconds the question mark stays where the enemy was last seen")] public float lastSeenTime = 15f;
    [Tooltip("the question mark fades out during the last seconds")] public float lastSeenFade = 2f;
    [Tooltip("how far the human player spots enemies; the target's stealth makes it shorter, like for bots")]
    public float playerViewRange = 120f;

    [Header("Map")]
    public float pixelsPerMeter = 6f;
    [Tooltip("on a big map the resolution goes down to stay under this number of cells")] public int maxCells = 400000;
    [Tooltip("milliseconds per frame spent on making the map at the start")] public float buildBudgetMs = 4f;
    [Tooltip("a place this high above the ground is drawn as a wall")] public float wallHeight = 2f;

    // body points to look at, the same as BotController uses
    const float HeadBelowTop = 0.25f;
    const float ShoulderOffset = 0.3f;

    const float StandHeight = 1.6f;   // free space a player needs above a floor
    const float StepHeight = 0.3f;    // smaller height differences are the same floor
    const int Border = 2;             // texels of empty map around the level
    const int Segments = 96;          // the round map is a fan of triangles

    const byte Void = 0, Wall = 1, Floor = 2, Raised = 3;

    // the buy zone: how opaque the inside is (the outline is solid)
    const float ZoneInside = 0.25f;

    // the HUD around the radar, in pixels at 1080p: the timer, the scores and the rows of players at the top centre
    // (260) and the "СМЕНА СТОРОН" banner under them (560 wide) take this much to each side of the middle; the health,
    // bhop and money boxes with the "+$300" lines take this much from the bottom at the left
    const float TopHudHalfWidth = 280f;
    const float BottomHudHeight = 300f;
    // the training target's panel in solo mode is in the top left corner (20, 80, 440 x 198 pixels): go under it
    const float TrainingPanelBottom = 282f;

    static readonly Color32 VoidColor = new Color32(14, 16, 19, 255);
    static readonly Color32 WallColor = new Color32(38, 41, 46, 255);
    static readonly Color32 FloorColor = new Color32(88, 93, 99, 255);
    static readonly Color32 LowColor = new Color32(70, 75, 81, 255);
    static readonly Color32 HighColor = new Color32(150, 155, 161, 255);
    static readonly Color32 EdgeColor = new Color32(198, 204, 210, 255);
    static readonly Color RingColor = new Color(0.8f, 0.83f, 0.87f, 0.9f);

    class Spot
    {
        public bool visible;
        public float lostAt;
        public Vector3 lastPos;   // where the enemies of this player saw him last
    }

    static Radar instance;

    public PlayerController player;
    Health playerHealth;
    Camera playerCam;

    readonly Dictionary<Health, Spot> spots = new Dictionary<Health, Spot>();
    readonly List<Health> gone = new List<Health>();
    // a character model gives up to 7 points from its hitboxes (CharacterBody.AimPoints), the capsule 5
    readonly Vector3[] bodyPoints = new Vector3[8];
    static readonly RaycastHit[] lineHits = new RaycastHit[32];
    float nextCheck;

    // the centre of the radar this frame
    Vector3 centrePos;
    Vector3 centreForward = Vector3.forward;
    Health centreOwner;

    // the map: a texture of the level seen from above
    Texture2D mapTexture;
    Texture2D blankTexture;
    Vector2 mapOrigin;               // world x, z of the texture's corner
    Vector2 mapSize = Vector2.one;   // meters covered by the texture
    float mapReadyAt;
    Material mapMaterial;
    RenderTexture view;              // the round map turned with the view, drawn every frame
    Texture2D ringTexture, dotTexture, arrowTexture, zoneTexture, questionTexture;
    bool flipY;                     // drawing into a texture comes out upside down on this graphics API
    readonly Vector2[] zoneCorners = new Vector2[4];

    readonly RaycastHit[] rayHits = new RaycastHit[16];
    readonly Collider[] overlaps = new Collider[16];
    readonly float[] heights = new float[16];
    readonly Dictionary<Collider, bool> characterColliders = new Dictionary<Collider, bool>();

    bool trainingPanel;
    float nextPanelCheck;
    Scoreboard scoreboard;
    float nextScoreboardSearch;

    // true while somebody of the other team sees this player (the radar of his enemies shows him)
    public static bool IsSpotted(Health h)
    {
        return instance != null && h != null && instance.spots.TryGetValue(h, out Spot s) && s.visible;
    }

    // ---------------- bootstrap ----------------

    // Play mode starts without a domain reload: no radar of the last run
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

    // a radar for a scene with rounds and a human player, never in a simulation or without graphics
    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        PlayerController pc = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<Radar>(true) != null) return;   // already there
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
            if (pc == null) pc = root.GetComponentInChildren<PlayerController>(true);
        }
        if (rm == null || pc == null || rm.simulation) return;

        GameObject go = new GameObject("Radar");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<Radar>().player = pc;
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

    void OnEnable()
    {
        RoundManager.RoundStarted += OnRoundStarted;
    }

    void OnDisable()
    {
        RoundManager.RoundStarted -= OnRoundStarted;
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
        FindCamera();

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            mapMaterial = new Material(shader);
            CheckFlip();
        }

        blankTexture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        Color32[] blank = new Color32[16];
        for (int i = 0; i < blank.Length; i++) blank[i] = VoidColor;
        blankTexture.SetPixels32(blank);
        blankTexture.Apply(false, true);

        dotTexture = MakeDot(64);
        arrowTexture = MakeArrow(64);
        questionTexture = MakeQuestion(64);

        StartCoroutine(BuildMap());
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        if (view != null)
        {
            view.Release();
            Destroy(view);
        }
        if (mapMaterial != null) Destroy(mapMaterial);
        if (mapTexture != null) Destroy(mapTexture);
        if (blankTexture != null) Destroy(blankTexture);
        if (ringTexture != null) Destroy(ringTexture);
        if (dotTexture != null) Destroy(dotTexture);
        if (arrowTexture != null) Destroy(arrowTexture);
        if (zoneTexture != null) Destroy(zoneTexture);
        if (questionTexture != null) Destroy(questionTexture);
    }

    // a new round: no dots and no question marks from the last one
    void OnRoundStarted()
    {
        spots.Clear();
    }

    void FindCamera()
    {
        if (player != null && player.cameraTransform != null) playerCam = player.cameraTransform.GetComponent<Camera>();
        if (playerCam == null) playerCam = Camera.main;
    }

    bool Visible()
    {
        return GameSettings.ShowRadar && Time.timeScale > 0f;
    }

    static bool Alive(Health h)
    {
        return h != null && !h.IsDead && h.gameObject.activeInHierarchy;
    }

    // the size and zoom settings, kept inside their ranges. They are read every frame, so a change in the settings
    // screen (GameSettings.Changed) or from anywhere else shows at once.
    static float SizeSetting => Setting(GameSettings.RadarSize, GameSettings.DefaultRadarSize, GameSettings.MinRadarSize, GameSettings.MaxRadarSize);
    static float ZoomSetting => Setting(GameSettings.RadarZoom, GameSettings.DefaultRadarZoom, GameSettings.MinRadarZoom, GameSettings.MaxRadarZoom);

    static float Setting(float value, float def, float min, float max)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? def : Mathf.Clamp(value, min, max);
    }

    // meters from the centre to the edge: more zoom = closer
    float Range => Mathf.Max(range, 1f) / ZoomSetting;

    void LateUpdate()
    {
        if (playerHealth == null) return;
        if (playerCam == null) FindCamera();

        UpdateSpotting();
        UpdateCentre();
        if (Visible()) RenderView();
    }

    // ---------------- spotting ----------------

    void UpdateSpotting()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + checkInterval;

        // dead players leave the radar at once (their question marks too)
        gone.Clear();
        foreach (KeyValuePair<Health, Spot> pair in spots)
            if (!Alive(pair.Key)) gone.Add(pair.Key);
        foreach (Health h in gone) spots.Remove(h);

        foreach (Health target in Health.All)
        {
            if (!Alive(target)) continue;

            bool seen = SeenByEnemies(target);
            if (!spots.TryGetValue(target, out Spot s))
            {
                if (!seen) continue;
                s = new Spot();
                spots.Add(target, s);
            }

            if (seen)
            {
                s.visible = true;
                s.lastPos = target.transform.position;
            }
            else if (s.visible)
            {
                // lost from sight: the last place he was seen at stays marked
                s.visible = false;
                s.lostAt = Time.time;
            }
        }
    }

    // a living player of the other team has the target in his field of view with a clear line to his body
    bool SeenByEnemies(Health target)
    {
        Collider body = target.GetComponent<Collider>();
        Bounds b = body != null ? body.bounds : new Bounds(target.transform.position, Vector3.zero);
        Stealth st = target.GetComponent<Stealth>();

        foreach (Health o in Health.All)
        {
            if (o == target || o.team == target.team || !Alive(o)) continue;

            if (o == playerHealth)
            {
                if (playerCam != null && PlayerSees(b, st, target, o)) return true;
                continue;
            }

            BotController bot = o.GetComponent<BotController>();
            if (bot != null && bot.head != null && BotSees(bot, b, st, target, o)) return true;
        }
        return false;
    }

    // the human player sees what is on his screen
    bool PlayerSees(Bounds b, Stealth st, Health target, Health self)
    {
        Vector3 from = playerCam.transform.position;
        float radius = st != null ? st.DetectionRadius(playerViewRange) : playerViewRange;
        Vector3 toTarget = b.center - from;
        if (toTarget.sqrMagnitude > radius * radius) return false;

        Vector3 flat = toTarget;
        flat.y = 0f;
        int count = TargetPoints(target, b, flat);
        for (int i = 0; i < count; i++)
        {
            Vector3 v = playerCam.WorldToViewportPoint(bodyPoints[i]);
            if (v.z <= 0f || v.x < 0f || v.x > 1f || v.y < 0f || v.y > 1f) continue;   // off the screen
            if (ClearLine(from, bodyPoints[i], target, self)) return true;
        }
        return false;
    }

    // a bot sees like in BotController: view range (shorter for a stealthy target), field of view, body points
    bool BotSees(BotController bot, Bounds b, Stealth st, Health target, Health self)
    {
        Vector3 from = bot.head.position;
        float radius = st != null ? st.DetectionRadius(bot.viewRange) : bot.viewRange;
        Vector3 toTarget = b.center - from;
        if (toTarget.sqrMagnitude > radius * radius) return false;

        Vector3 flat = toTarget;
        flat.y = 0f;
        // an alerted bot looks all around, like in BotController.CanSee
        if (!bot.IsAlerted && flat.sqrMagnitude > 0.0001f && Vector3.Angle(bot.transform.forward, flat) > bot.fieldOfView * 0.5f)
            return false;

        int count = TargetPoints(target, b, flat);
        for (int i = 0; i < count; i++)
            if (ClearLine(from, bodyPoints[i], target, self)) return true;
        return false;
    }

    // the points of the target to check: the centres of his hitboxes (a character model), else the capsule's points
    int TargetPoints(Health target, Bounds b, Vector3 flat)
    {
        CharacterBody cb = target.GetComponent<CharacterBody>();
        int count = cb != null ? cb.AimPoints(bodyPoints) : 0;
        return count > 0 ? count : BodyPoints(b, flat);
    }

    // the first thing on the line is the target (corpses are on Ignore Raycast, so they do not block it);
    // the one who looks is not in his own way (the hitboxes of his arms and gun hands in front of his eyes)
    static bool ClearLine(Vector3 from, Vector3 to, Health target, Health self)
    {
        Vector3 dir = to - from;
        float d = dir.magnitude;
        if (d < 0.01f) return true;

        int n = Physics.RaycastNonAlloc(from, dir / d, lineHits, d + 0.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            if (best >= 0 && lineHits[i].distance >= lineHits[best].distance) continue;
            if (self != null && lineHits[i].collider.GetComponentInParent<Health>() == self) continue;
            best = i;
        }
        return best >= 0 && lineHits[best].collider.GetComponentInParent<Health>() == target;
    }

    // centre, upper chest, head and both shoulders, from the body's collider
    int BodyPoints(Bounds b, Vector3 flatToTarget)
    {
        Vector3 centre = b.center;
        Vector3 headPoint = new Vector3(centre.x, Mathf.Max(centre.y, b.max.y - HeadBelowTop), centre.z);
        Vector3 chest = Vector3.Lerp(centre, headPoint, 0.5f);

        bodyPoints[0] = centre;
        bodyPoints[1] = chest;
        bodyPoints[2] = headPoint;
        if (flatToTarget.sqrMagnitude < 0.0001f) return 3;

        float sideways = Mathf.Min(ShoulderOffset, Mathf.Min(b.extents.x, b.extents.z) * 0.8f);
        Vector3 side = Vector3.Cross(Vector3.up, flatToTarget).normalized * sideways;
        bodyPoints[3] = chest + side;
        bodyPoints[4] = chest - side;
        return 5;
    }

    // ---------------- drawing ----------------

    // the radar follows the camera's owner: the player, or the teammate he watches after death
    void UpdateCentre()
    {
        Transform cam = playerCam != null ? playerCam.transform : player.transform;
        centreOwner = cam.GetComponentInParent<Health>();
        centrePos = centreOwner != null ? centreOwner.transform.position : cam.position;

        Vector3 f = cam.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.0001f)
        {
            f = (centreOwner != null ? centreOwner.transform : cam).forward;
            f.y = 0f;
        }
        centreForward = f.sqrMagnitude > 0.0001f ? f.normalized : Vector3.forward;
    }

    // radar pixels from the centre (x to the right, y down) for a point of the world
    Vector2 ToRadar(Vector3 world, float scale)
    {
        Vector3 d = world - centrePos;
        Vector3 right = new Vector3(centreForward.z, 0f, -centreForward.x);
        return new Vector2(d.x * right.x + d.z * right.z, -(d.x * centreForward.x + d.z * centreForward.z)) * scale;
    }

    // the radar's diameter in pixels: the size setting, but never under the score at the top centre,
    // never over the boxes at the bottom left and never off the screen
    float RadarSize()
    {
        float k = Screen.height / 1080f;
        float size = sizeAt1080 * k * SizeSetting;
        size = Mathf.Min(size, Screen.width * 0.5f - (TopHudHalfWidth + 12f) * k - Mathf.Round(margin * k));
        size = Mathf.Min(size, Screen.height - RadarTop(k) - BottomHudHeight * k);
        return Mathf.Max(64f, Mathf.Round(size));
    }

    static float RingWidth(int size)
    {
        return Mathf.Max(2f, size / 110f);
    }

    // the map disc ends in the middle of the ring, so the ring hides its edge
    static float DiscRadius(int size)
    {
        return size * 0.5f - 0.5f - RingWidth(size) * 0.5f;
    }

    void MakeView(int size)
    {
        if (view != null)
        {
            view.Release();
            Destroy(view);
        }
        view = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        view.name = "Radar view";
        view.filterMode = FilterMode.Bilinear;
        view.wrapMode = TextureWrapMode.Clamp;
        view.Create();
    }

    // the round map turned with the view, into a texture of the radar's size
    void RenderView()
    {
        int size = Mathf.RoundToInt(RadarSize());
        if (mapMaterial == null) return;
        if (view == null || view.width != size || !view.IsCreated()) MakeView(size);

        float radius = DiscRadius(size);
        float scale = radius / Range;

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = view;
        GL.Clear(false, true, new Color(0f, 0f, 0f, 0f));
        GL.PushMatrix();
        // y = 0 at the top, like on the screen
        if (flipY) GL.LoadPixelMatrix(0f, size, 0f, size);
        else GL.LoadPixelMatrix(0f, size, size, 0f);

        Fan(blankTexture, 1f, size * 0.5f, radius, scale);
        float fade = mapTexture != null ? Mathf.Clamp01((Time.time - mapReadyAt) / 0.5f) : 0f;
        if (fade > 0f) Fan(mapTexture, fade, size * 0.5f, radius, scale);
        DrawBuyZones(size * 0.5f, radius, scale, RingWidth(size));

        GL.PopMatrix();
        RenderTexture.active = previous;
    }

    // Unity normally keeps textures drawn this way the right way up on every graphics API.
    // To be sure, a mark is drawn at the top of a tiny texture once and read back.
    void CheckFlip()
    {
        RenderTexture test = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = test;
        GL.Clear(false, true, Color.black);
        GL.PushMatrix();
        GL.LoadPixelMatrix(0f, 4f, 4f, 0f);
        mapMaterial.mainTexture = Texture2D.whiteTexture;
        mapMaterial.SetPass(0);
        GL.Begin(GL.TRIANGLES);
        GL.Color(Color.white);
        GL.TexCoord2(0.5f, 0.5f);
        GL.Vertex3(0f, 0f, 0f);
        GL.Vertex3(4f, 0f, 0f);
        GL.Vertex3(4f, 2f, 0f);
        GL.Vertex3(0f, 0f, 0f);
        GL.Vertex3(4f, 2f, 0f);
        GL.Vertex3(0f, 2f, 0f);
        GL.End();
        GL.PopMatrix();

        Texture2D read = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        read.ReadPixels(new Rect(0f, 0f, 4f, 4f), 0, 0);
        read.Apply();
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(test);

        // texture rows go up: row 3 is the top, it should have the mark
        float top = read.GetPixel(2, 3).r;
        float bottom = read.GetPixel(2, 0).r;
        flipY = top < 0.5f && bottom > 0.5f;
        Destroy(read);
    }

    // a disc of triangles; every corner gets the map texture coordinates of the world point under it,
    // so the map is turned and cut round at the same time
    void Fan(Texture texture, float alpha, float half, float radius, float scale)
    {
        Fan(texture, new Color(1f, 1f, 1f, alpha), half, radius, scale, mapOrigin, mapSize);
    }

    // the same with a tint and a texture lying on the world rectangle origin .. origin + area (x, z)
    void Fan(Texture texture, Color color, float half, float radius, float scale, Vector2 origin, Vector2 area)
    {
        Vector3 right = new Vector3(centreForward.z, 0f, -centreForward.x);

        mapMaterial.mainTexture = texture;
        mapMaterial.SetPass(0);
        GL.Begin(GL.TRIANGLES);
        GL.Color(color);
        for (int i = 0; i < Segments; i++)
        {
            float a0 = i * Mathf.PI * 2f / Segments;
            float a1 = (i + 1) * Mathf.PI * 2f / Segments;
            FanVertex(0f, 0f, half, scale, right, origin, area);
            FanVertex(Mathf.Cos(a0) * radius, Mathf.Sin(a0) * radius, half, scale, right, origin, area);
            FanVertex(Mathf.Cos(a1) * radius, Mathf.Sin(a1) * radius, half, scale, right, origin, area);
        }
        GL.End();
    }

    // x, y: pixels from the centre, x to the right and y down (forward is up)
    void FanVertex(float x, float y, float half, float scale, Vector3 right, Vector2 origin, Vector2 area)
    {
        Vector3 world = centrePos + (right * x - centreForward * y) / scale;
        GL.TexCoord2((world.x - origin.x) / area.x, (world.z - origin.y) / area.y);
        GL.Vertex3(half + x, half + y, 0f);
    }

    // the zone's rectangle in metres (x along the world X, y along the world Z); a zone without a size
    // (an old round zone) becomes the square whose half diagonal is its radius
    static Vector2 ZoneSize(RoundManager.BuyZone z)
    {
        if (z.size.x > 0f && z.size.y > 0f) return z.size;
        float side = z.radius * 1.41421356f;
        return new Vector2(side, side);
    }

    // the buy zones in normal games: the player's team green, the enemies' red. The inside is a texture laid on the
    // world like the map, so the same fan turns it with the view and cuts it at the radar's edge; the outline is
    // drawn on top with smooth edges and cut at the edge too.
    void DrawBuyZones(float half, float radius, float scale, float lineWidth)
    {
        if (!Economy.Enabled || RoundManager.BuyZones.Count == 0 || playerHealth == null) return;
        if (zoneTexture == null) zoneTexture = MakeZone();

        int team = playerHealth.team;
        float reach = radius / scale;   // meters from the centre to the radar's edge
        foreach (RoundManager.BuyZone z in RoundManager.BuyZones)
        {
            Vector2 extent = ZoneSize(z);
            if (extent.x <= 0f || extent.y <= 0f) continue;

            // the nearest point of the rectangle is beyond the radar's edge: nothing to draw
            float dx = Mathf.Max(0f, Mathf.Abs(z.center.x - centrePos.x) - extent.x * 0.5f);
            float dz = Mathf.Max(0f, Mathf.Abs(z.center.z - centrePos.z) - extent.y * 0.5f);
            if (dx * dx + dz * dz > reach * reach) continue;

            Color col = z.team == team ? ownZoneColor : enemyZoneColor;

            // the inside: the middle half of the zone texture is the rectangle
            Vector2 origin = new Vector2(z.center.x - extent.x, z.center.z - extent.y);
            Fan(zoneTexture, new Color(col.r, col.g, col.b, col.a * ZoneInside), half, radius, scale, origin, extent * 2f);

            // the outline through the corners
            Vector3 hx = new Vector3(extent.x * 0.5f, 0f, 0f);
            Vector3 hz = new Vector3(0f, 0f, extent.y * 0.5f);
            zoneCorners[0] = ToRadar(z.center - hx - hz, scale);
            zoneCorners[1] = ToRadar(z.center + hx - hz, scale);
            zoneCorners[2] = ToRadar(z.center + hx + hz, scale);
            zoneCorners[3] = ToRadar(z.center - hx + hz, scale);
            // cut at the inner edge of the ring, so a side that leaves the radar at a slant stays under the ring
            Outline(zoneCorners, col, half, radius - lineWidth * 0.5f, lineWidth);
        }
    }

    // a closed line through the corners (pixels from the centre), cut at the clip radius. Every side is a strip
    // with a solid middle and a one pixel soft border on both sides, so it stays smooth while the radar turns.
    void Outline(Vector2[] corners, Color color, float half, float clip, float width)
    {
        mapMaterial.mainTexture = Texture2D.whiteTexture;
        mapMaterial.SetPass(0);
        GL.Begin(GL.TRIANGLES);
        GL.TexCoord2(0.5f, 0.5f);
        for (int i = 0; i < corners.Length; i++)
            Side(corners[i], corners[(i + 1) % corners.Length], color, half, clip, width);
        GL.End();
    }

    static void Side(Vector2 a, Vector2 b, Color color, float half, float clip, float width)
    {
        if (!ClipToDisc(ref a, ref b, clip, out bool cutA, out bool cutB)) return;
        Vector2 d = b - a;
        if (d.sqrMagnitude < 0.0001f) return;
        Vector2 dir = d.normalized;

        // an end that was not cut goes on by half the width, so two sides meet in a square corner
        float hw = width * 0.5f;
        if (!cutA) a -= dir * hw;
        if (!cutB) b += dir * hw;

        Vector2 n = new Vector2(-dir.y, dir.x);
        float core = Mathf.Max(0f, hw - 0.5f);
        Color clear = new Color(color.r, color.g, color.b, 0f);
        Strip(a, b, -n * core, n * core, color, color, half);
        Strip(a, b, n * core, n * (core + 1f), color, clear, half);
        Strip(a, b, -n * core, -n * (core + 1f), color, clear, half);
    }

    // two triangles between the lines a + inner .. b + inner and a + outer .. b + outer
    static void Strip(Vector2 a, Vector2 b, Vector2 inner, Vector2 outer, Color innerColor, Color outerColor, float half)
    {
        StripVertex(a + inner, innerColor, half);
        StripVertex(b + inner, innerColor, half);
        StripVertex(b + outer, outerColor, half);
        StripVertex(a + inner, innerColor, half);
        StripVertex(b + outer, outerColor, half);
        StripVertex(a + outer, outerColor, half);
    }

    static void StripVertex(Vector2 p, Color color, float half)
    {
        GL.Color(color);
        GL.Vertex3(half + p.x, half + p.y, 0f);
    }

    // cuts the segment a - b (pixels from the centre) to the disc of radius r; false when nothing of it is inside.
    // cutA / cutB: that end was moved to the disc's edge
    static bool ClipToDisc(ref Vector2 a, ref Vector2 b, float r, out bool cutA, out bool cutB)
    {
        cutA = false;
        cutB = false;
        Vector2 d = b - a;
        float qa = Vector2.Dot(d, d);
        if (qa < 0.0001f) return false;
        float qb = 2f * Vector2.Dot(a, d);
        float qc = Vector2.Dot(a, a) - r * r;
        float disc = qb * qb - 4f * qa * qc;
        if (disc <= 0f) return false;

        float root = Mathf.Sqrt(disc);
        float t0 = (-qb - root) / (2f * qa);
        float t1 = (-qb + root) / (2f * qa);
        if (t0 >= 1f || t1 <= 0f) return false;

        Vector2 start = a;
        if (t0 > 0f)
        {
            a = start + d * t0;
            cutA = true;
        }
        if (t1 < 1f)
        {
            b = start + d * t1;
            cutB = true;
        }
        return true;
    }

    float RadarTop(float k)
    {
        if (Time.time >= nextPanelCheck)
        {
            nextPanelCheck = Time.time + 1f;
            TrainingTarget t = FindAnyObjectByType<TrainingTarget>();
            trainingPanel = t != null && t.isActiveAndEnabled;
        }
        // in solo mode the training target's panel is in the top left corner: go under it
        return trainingPanel ? TrainingPanelBottom : Mathf.Round(margin * k);
    }

    // the TAB table is on the screen (a big radar on a narrow screen reaches under it)
    bool ScoreboardShown()
    {
        if (scoreboard == null && Time.unscaledTime >= nextScoreboardSearch)
        {
            nextScoreboardSearch = Time.unscaledTime + 2f;
            scoreboard = FindAnyObjectByType<Scoreboard>();
        }
        return scoreboard != null && scoreboard.isActiveAndEnabled && Input.GetKey(scoreboard.key) && !DevConsole.IsOpen;
    }

    void OnGUI()
    {
        // above the sniper scope, like in CS; under the TAB table while it is shown
        GUI.depth = ScoreboardShown() ? 1 : -1;
        if (Event.current.type != EventType.Repaint || view == null || playerHealth == null || !Visible()) return;
        if (BuyMenu.IsOpen) return;   // hidden under the buy menu, like the HUD

        int size = view.width;
        float k = Screen.height / 1080f;
        Rect r = new Rect(Mathf.Round(margin * k), RadarTop(k), size, size);
        Vector2 c = r.center;

        GUI.color = new Color(1f, 1f, 1f, opacity);
        GUI.DrawTexture(r, view);

        if (ringTexture == null || ringTexture.width != size)
        {
            if (ringTexture != null) Destroy(ringTexture);
            ringTexture = MakeRing(size, RingWidth(size));
        }
        GUI.color = Color.white;
        GUI.DrawTexture(r, ringTexture);

        // the icons grow with the radar
        float s = size / 220f;
        float radius = DiscRadius(size);
        float scale = radius / Range;
        float allySize = Mathf.Max(6f, 9f * s);
        float enemySize = Mathf.Max(6f, 10f * s);
        float questionSize = Mathf.Max(10f, 17f * s);
        float arrowSize = Mathf.Max(10f, 17f * s);
        int team = playerHealth.team;

        // teammates: always, while they live
        foreach (Health h in Health.All)
        {
            if (h == centreOwner || h.team != team || !Alive(h)) continue;
            Icon(h.transform.position, dotTexture, allyColor, allySize, c, radius, scale);
        }

        // enemies nobody sees any more: the dot goes out quickly and a "?" takes its place where he was last seen;
        // it fades out at the end of lastSeenTime (under the dots of the enemies in sight)
        float dotFade = Mathf.Max(fadeTime, 0.01f);
        foreach (KeyValuePair<Health, Spot> pair in spots)
        {
            Health h = pair.Key;
            Spot sp = pair.Value;
            if (sp.visible || !Alive(h) || h.team == team) continue;

            float age = Time.time - sp.lostAt;
            float a = Mathf.Clamp01(age / dotFade) * Mathf.Clamp01((lastSeenTime - age) / Mathf.Max(lastSeenFade, 0.01f));
            if (a <= 0f) continue;

            Color col = enemyColor;
            col.a *= a;
            Icon(sp.lastPos, questionTexture, col, questionSize, c, radius, scale);
        }

        // enemies in sight: a red dot; a dot just lost from sight fades out where he was seen
        foreach (KeyValuePair<Health, Spot> pair in spots)
        {
            Health h = pair.Key;
            Spot sp = pair.Value;
            if (!Alive(h) || h.team == team) continue;

            float a = sp.visible ? 1f : 1f - (Time.time - sp.lostAt) / dotFade;
            if (a <= 0f) continue;

            Color col = enemyColor;
            col.a *= a;
            Icon(sp.visible ? h.transform.position : sp.lastPos, dotTexture, col, enemySize, c, radius, scale);
        }

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(c.x - arrowSize * 0.5f, c.y - arrowSize * 0.5f, arrowSize, arrowSize), arrowTexture);
    }

    // an icon outside the radar's range sits on its edge
    void Icon(Vector3 world, Texture texture, Color color, float size, Vector2 centre, float radius, float scale)
    {
        Vector2 p = ToRadar(world, scale);
        float max = radius - size * 0.5f;
        if (p.sqrMagnitude > max * max) p = p.normalized * max;

        GUI.color = color;
        GUI.DrawTexture(new Rect(centre.x + p.x - size * 0.5f, centre.y + p.y - size * 0.5f, size, size), texture);
    }

    // ---------------- the map ----------------

    bool IsCharacter(Collider c)
    {
        if (!characterColliders.TryGetValue(c, out bool character))
        {
            character = c.GetComponentInParent<Health>() != null;
            characterColliders[c] = character;
        }
        return character;
    }

    // the part of the world to draw: MapBounds if the map has them, otherwise all the level's colliders
    bool LevelArea(out Rect area, out float bottom, out float top)
    {
        area = new Rect();
        bottom = 0f;
        top = 0f;

        bool any = false;
        Bounds all = new Bounds();
        foreach (Collider c in FindObjectsByType<Collider>(FindObjectsInactive.Exclude))
        {
            if (!c.enabled || c.isTrigger || ((1 << c.gameObject.layer) & Physics.DefaultRaycastLayers) == 0 || IsCharacter(c)) continue;
            if (!any) all = c.bounds;
            else all.Encapsulate(c.bounds);
            any = true;
        }
        if (!any) return false;

        bottom = all.min.y - 1f;
        top = all.max.y + 1f;

        MapBounds mb = FindAnyObjectByType<MapBounds>();
        if (mb != null)
            area = Rect.MinMaxRect(-mb.halfSizeX - 2f, -mb.halfSizeZ - 2f, mb.halfSizeX + 2f, mb.halfSizeZ + 2f);
        else
            area = Rect.MinMaxRect(Mathf.Max(all.min.x, -150f), Mathf.Max(all.min.z, -150f),
                                   Mathf.Min(all.max.x, 150f), Mathf.Min(all.max.z, 150f));
        return area.width > 1f && area.height > 1f;
    }

    // For every cell: rays from above find the surfaces, the lowest one with free space above it is the floor.
    // Inside a building that is its roof (a wall on the radar), in a tunnel it is the ground under the roof.
    // Players are skipped, bodies are on Ignore Raycast, so nobody is ever drawn into the map.
    IEnumerator BuildMap()
    {
        // one frame later: the round has started and every collider is in place
        yield return null;
        Physics.SyncTransforms();

        if (!LevelArea(out Rect area, out float bottom, out float top)) yield break;

        float ppm = Mathf.Max(1f, pixelsPerMeter);
        int cw = Mathf.CeilToInt(area.width * ppm);
        int ch = Mathf.CeilToInt(area.height * ppm);
        if ((long)cw * ch > maxCells)
        {
            ppm *= Mathf.Sqrt(maxCells / ((float)cw * ch));
            cw = Mathf.CeilToInt(area.width * ppm);
            ch = Mathf.CeilToInt(area.height * ppm);
        }

        int count = cw * ch;
        float[] floor = new float[count];
        bool[] covered = new bool[count];
        float rayLength = top - bottom;
        float probe = 0.25f / ppm;
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        for (int j = 0; j < ch; j++)
        {
            float z = area.yMin + (j + 0.5f) / ppm;
            for (int i = 0; i < cw; i++)
            {
                float x = area.xMin + (i + 0.5f) / ppm;
                int c = j * cw + i;
                floor[c] = FloorAt(x, z, top, rayLength, probe, out covered[c]);
            }
            if (watch.Elapsed.TotalMilliseconds > buildBudgetMs)
            {
                yield return null;
                watch.Restart();
            }
        }

        // heights are drawn from the ground: the most common floor height
        float ground = MostCommonHeight(floor);
        byte[] kind = new byte[count];
        float[] rel = new float[count];
        for (int c = 0; c < count; c++)
        {
            if (float.IsNaN(floor[c])) continue;   // Void
            rel[c] = floor[c] - ground;
            kind[c] = rel[c] > wallHeight ? Wall : rel[c] > StepHeight ? Raised : Floor;
        }

        int w = cw + Border * 2;
        int h = ch + Border * 2;
        Color32[] pixels = new Color32[w * h];
        for (int p = 0; p < pixels.Length; p++) pixels[p] = VoidColor;

        int line = Mathf.Max(1, Mathf.RoundToInt(0.3f * ppm));   // outline width in cells
        for (int j = 0; j < ch; j++)
        {
            for (int i = 0; i < cw; i++)
                pixels[(j + Border) * w + i + Border] = CellColor(kind, rel, covered, cw, ch, i, j, line);
            if (watch.Elapsed.TotalMilliseconds > buildBudgetMs)
            {
                yield return null;
                watch.Restart();
            }
        }

        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
        tex.name = "Radar map";
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Trilinear;
        tex.SetPixels32(pixels);
        tex.Apply(true, true);

        mapTexture = tex;
        mapOrigin = new Vector2(area.xMin - Border / ppm, area.yMin - Border / ppm);
        mapSize = new Vector2(w / ppm, h / ppm);
        mapReadyAt = Time.time;
        characterColliders.Clear();
    }

    // height of the lowest surface with free space above it, NaN if there is none
    float FloorAt(float x, float z, float top, float length, float probe, out bool covered)
    {
        covered = false;
        int n = Physics.RaycastNonAlloc(new Vector3(x, top, z), Vector3.down, rayHits, length,
                                        Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        int count = 0;
        for (int k = 0; k < n && count < heights.Length; k++)
        {
            if (IsCharacter(rayHits[k].collider)) continue;
            heights[count++] = rayHits[k].point.y;
        }
        System.Array.Sort(heights, 0, count);

        for (int k = 0; k < count; k++)
        {
            if (!FreeAbove(x, heights[k], z, probe)) continue;
            // anything higher is at least StandHeight above: a roof (a tunnel, a door lintel)
            covered = heights[count - 1] > heights[k] + StepHeight;
            return heights[k];
        }
        return float.NaN;
    }

    bool FreeAbove(float x, float y, float z, float probe)
    {
        Vector3 centre = new Vector3(x, y + 0.05f + StandHeight * 0.5f, z);
        Vector3 half = new Vector3(probe, StandHeight * 0.5f, probe);
        int n = Physics.OverlapBoxNonAlloc(centre, half, overlaps, Quaternion.identity,
                                           Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int k = 0; k < n; k++)
            if (!IsCharacter(overlaps[k])) return false;
        return true;
    }

    static float MostCommonHeight(float[] floor)
    {
        Dictionary<int, int> bins = new Dictionary<int, int>();
        int best = 0;
        int bestCount = 0;
        foreach (float f in floor)
        {
            if (float.IsNaN(f)) continue;
            int key = Mathf.RoundToInt(f * 10f);
            bins.TryGetValue(key, out int n);
            n++;
            bins[key] = n;
            if (n > bestCount)
            {
                bestCount = n;
                best = key;
            }
        }
        return best / 10f;
    }

    // floor grey, higher places lighter, walls dark, covered floor darker;
    // a bright line along walls and the map edge, a softer one where the floor height changes
    Color32 CellColor(byte[] kind, float[] rel, bool[] covered, int cw, int ch, int i, int j, int line)
    {
        int c = j * cw + i;
        if (kind[c] == Void) return VoidColor;
        if (kind[c] == Wall) return WallColor;

        Color32 col;
        if (kind[c] == Raised) col = Color32.Lerp(FloorColor, HighColor, rel[c] / wallHeight);
        else if (rel[c] < -StepHeight) col = LowColor;
        else col = FloorColor;
        if (covered[c]) col = new Color32((byte)(col.r * 0.72f), (byte)(col.g * 0.72f), (byte)(col.b * 0.74f), 255);

        float edge = 0f;
        for (int dj = -line; dj <= line && edge < 1f; dj++)
        {
            for (int di = -line; di <= line; di++)
            {
                int x = i + di;
                int y = j + dj;
                if (x < 0 || y < 0 || x >= cw || y >= ch)
                {
                    edge = 1f;
                    break;
                }
                int n = y * cw + x;
                if (kind[n] == Void || kind[n] == Wall)
                {
                    edge = 1f;
                    break;
                }
                if (di >= -1 && di <= 1 && dj >= -1 && dj <= 1 && Mathf.Abs(rel[n] - rel[c]) > StepHeight)
                    edge = Mathf.Max(edge, 0.5f);
            }
        }
        return Color32.Lerp(col, EdgeColor, edge * 0.8f);
    }

    // ---------------- small textures ----------------

    // anti-aliased ring along the edge, made for the exact size so it stays crisp
    static Texture2D MakeRing(int size, float width)
    {
        Color32[] px = new Color32[size * size];
        float half = size * 0.5f;
        float outer = half - 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01(outer - d + 0.5f) * Mathf.Clamp01(d - (outer - width) + 0.5f);
                px[y * size + x] = new Color(RingColor.r, RingColor.g, RingColor.b, RingColor.a * a);
            }

        Texture2D t = new Texture2D(size, size, TextureFormat.RGBA32, false);
        t.name = "Radar ring";
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Bilinear;
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }

    // the inside of a buy zone: 4 x 4 texels, the middle 2 x 2 white, the ring around them clear. Laid on twice the
    // zone's size, its middle half is the zone; point filtering keeps that edge exact and clamping keeps the rest clear.
    // The team colour tints it when drawn.
    static Texture2D MakeZone()
    {
        Color32[] px = new Color32[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                bool inside = x >= 1 && x <= 2 && y >= 1 && y <= 2;
                px[y * 4 + x] = new Color32(255, 255, 255, (byte)(inside ? 255 : 0));
            }

        Texture2D t = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        t.name = "Radar buy zone";
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Point;
        t.SetPixels32(px);
        t.Apply(false, true);
        return t;
    }

    // white disc with a dark outline; GUI.color tints the white part
    static Texture2D MakeDot(int n)
    {
        float outline = n * 0.12f;
        float radius = n * 0.5f - 1f - outline;
        Vector2 centre = new Vector2(n * 0.5f, n * 0.5f);
        return MakeShape(n, outline, p => radius - Vector2.Distance(p, centre));
    }

    // white arrow pointing up with a dark outline (texture rows go up, the screen shows the last row on top)
    static Texture2D MakeArrow(int n)
    {
        Vector2[] poly =
        {
            new Vector2(0.5f, 0.92f) * n,
            new Vector2(0.84f, 0.1f) * n,
            new Vector2(0.5f, 0.3f) * n,
            new Vector2(0.16f, 0.1f) * n
        };
        return MakeShape(n, n * 0.07f, p => PolygonInside(p, poly));
    }

    // white question mark with a dark outline: a hook (over the top from the upper left, down the right side and back
    // to the middle), a short stem under it and a dot at the bottom. Rows go up, like the arrow.
    static Texture2D MakeQuestion(int n)
    {
        Vector2 hook = new Vector2(0.5f, 0.645f) * n;
        float hookRadius = 0.195f * n;
        float stroke = 0.078f * n;   // half the line width
        Vector2 stemTop = new Vector2(hook.x, hook.y - hookRadius);
        Vector2 stemEnd = new Vector2(hook.x, 0.4f * n);
        Vector2 dot = new Vector2(hook.x, 0.165f * n);
        float dotRadius = 0.088f * n;

        return MakeShape(n, n * 0.06f, p =>
        {
            float s = stroke - ArcDistance(p, hook, hookRadius, -90f, 160f);
            s = Mathf.Max(s, stroke - SegmentDistance(p, stemTop, stemEnd));
            return Mathf.Max(s, dotRadius - Vector2.Distance(p, dot));
        });
    }

    // inside: signed distance in texels, above zero inside the shape
    static Texture2D MakeShape(int n, float outline, System.Func<Vector2, float> inside)
    {
        Color32[] px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float s = inside(new Vector2(x + 0.5f, y + 0.5f));
                float fill = Mathf.Clamp01(s + 0.5f);
                float rim = Mathf.Clamp01(s + outline + 0.5f) * 0.85f;
                float a = fill + (1f - fill) * rim;
                float white = a > 0f ? fill / a : 0f;
                px[y * n + x] = new Color(white, white, white, a);
            }

        Texture2D t = new Texture2D(n, n, TextureFormat.RGBA32, true);
        t.name = "Radar icon";
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Trilinear;
        t.SetPixels32(px);
        t.Apply(true, true);
        return t;
    }

    // signed distance to a polygon: above zero inside
    static float PolygonInside(Vector2 p, Vector2[] poly)
    {
        float best = float.MaxValue;
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            Vector2 a = poly[j];
            Vector2 b = poly[i];
            if ((b.y > p.y) != (a.y > p.y) && p.x < (a.x - b.x) * (p.y - b.y) / (a.y - b.y) + b.x) inside = !inside;
            best = Mathf.Min(best, SegmentDistance(p, a, b));
        }
        return inside ? best : -best;
    }

    // distance from p to the segment a - b
    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    // distance from p to the arc of a circle between two angles (degrees, counter-clockwise from +x, from < to,
    // both inside -180 .. 180)
    static float ArcDistance(Vector2 p, Vector2 centre, float radius, float from, float to)
    {
        Vector2 q = p - centre;
        float angle = Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg;
        if (angle >= from && angle <= to) return Mathf.Abs(q.magnitude - radius);

        Vector2 a = centre + new Vector2(Mathf.Cos(from * Mathf.Deg2Rad), Mathf.Sin(from * Mathf.Deg2Rad)) * radius;
        Vector2 b = centre + new Vector2(Mathf.Cos(to * Mathf.Deg2Rad), Mathf.Sin(to * Mathf.Deg2Rad)) * radius;
        return Mathf.Min(Vector2.Distance(p, a), Vector2.Distance(p, b));
    }
}
