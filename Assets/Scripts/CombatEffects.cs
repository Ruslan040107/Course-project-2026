using System.Collections.Generic;
using UnityEngine;

// Shot effects (never in simulations): flashes, tracers, bullet holes, blood, the killfeed and the hit marker.
// Also the knife scratches on walls and floors, and the empty magazines and shells that fall out of guns
// (DropPiece / DropShell, called by ViewModel and BotWeaponModels): a copy at real size falls without physics
// (a ray to the floor every frame), bounces once or twice, lies on its flattest side for a second and shrinks away.
public class CombatEffects : MonoBehaviour
{
    [Header("Tracers")]
    public int tracerPool = 32;
    public float tracerSpeed = 250f;
    public float tracerLength = 6f;
    public float tracerWidth = 0.03f;
    public Color tracerColor = new Color(1f, 0.85f, 0.45f, 1f);

    [Header("Decals")]
    public int decalPool = 80;
    public float decalLifetime = 25f;
    public float decalFadeTime = 4f;

    [Header("Knife scratches")]
    public int scratchPool = 30;
    public float scratchLifetime = 20f;
    public float scratchFadeTime = 4f;

    [Header("Dropped magazines and shells")]
    [Tooltip("magazines and empty shells in the world at most; the oldest goes first")] public int pieceLimit = 48;
    [Tooltip("seconds a magazine or a shell lies on the floor, then it shrinks away")] public float pieceLieTime = 1f;
    public float pieceShrinkTime = 0.25f;
    [Tooltip("none farther than this from the camera")] public float pieceMaxDistance = 40f;

    [Header("Other")]
    public Texture2D headshotIcon;
    public Texture2D noScopeIcon;
    public Texture2D jumpIcon;
    [Tooltip("empty: Resources/KillIcons/wallbang")]
    public Texture2D wallbangIcon;
    public float maxEffectDistance = 80f;

    class Tracer
    {
        public LineRenderer line;
        public Vector3 start, dir;
        public float total, travelled;
        public bool active;
    }

    class Decal
    {
        public SpriteRenderer sr;
        public float expire, baseAlpha;
        public bool active;
    }

    // a magazine or an empty shell that fell out of a gun: a copy of the part at real size, no physics
    class Piece
    {
        public GameObject go;
        public Transform t;
        public string key;
        public Vector3 scale, velocity, spin;
        public Vector3 center;      // the middle of its meshes (local, unscaled)
        public Vector3 thinAxis;    // the local axis of its smallest size: it lies on that side
        public float thinHalf;      // half of that size (local)
        public float born, landedAt;
        public int bounces;
        public bool landed;
        public Vector3 restPos, contact;   // where it lies, and the point it touches the floor with
    }

    class FeedEntry
    {
        public bool headshot;
        public bool noScope;
        public bool inAir;
        public bool wallbang;
        public WeaponData weapon;
        public Texture2D weaponIcon;   // null: a ">" is drawn instead
        public string killer, victim, assister;
        public int killerTeam, victimTeam, assisterTeam;
        // the player took part: made the kill, the assist, or was killed
        public bool playerKiller, playerAssister, playerVictim;
        public float expire;
    }

    // one piece of a killfeed line: a text or an icon
    struct FeedPart
    {
        public GUIContent text;
        public Texture2D icon;
        public float width, height;
        public Color color;
    }

    readonly List<Tracer> tracers = new List<Tracer>();
    readonly List<Decal> decals = new List<Decal>();
    readonly List<FeedEntry> feed = new List<FeedEntry>();
    readonly List<FeedPart> parts = new List<FeedPart>();
    // weapon name -> killfeed icon (Resources/KillIcons/<name>), null if there is none
    readonly Dictionary<string, Texture2D> weaponIcons = new Dictionary<string, Texture2D>();
    readonly Collider[] overlap = new Collider[8];

    // knife scratches (a pool like the bullet holes), dropped magazines and shells (alive, oldest first; free ones by kind)
    readonly List<Decal> scratches = new List<Decal>();
    readonly List<Piece> pieces = new List<Piece>();
    readonly Dictionary<string, Stack<Piece>> freePieces = new Dictionary<string, Stack<Piece>>();
    readonly Dictionary<string, Material> pieceMaterials = new Dictionary<string, Material>();
    readonly RaycastHit[] pieceHits = new RaycastHit[16];
    readonly List<GameObject> removeBuffer = new List<GameObject>();
    GameObject[] shellTemplates;
    Sprite[] slashSprites;
    Sprite stabSprite;
    int nextScratch;

    // the effects of this scene (null in simulations: there CombatEffects is switched off)
    static CombatEffects instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
    }

    int nextTracer, nextDecal;
    bool subscribed;
    Sprite holeSprite, bloodSprite;
    ParticleSystem flashFx, sparkFx, dustFx, bloodFx;
    Light flashLight;
    float flashTimer;
    float hitMarkerUntil;
    Color hitMarkerColor = Color.white;
    GUIStyle feedStyle;
    Texture2D headIcon;
    Texture2D noScopeTex;
    Texture2D jumpTex;
    Texture2D wallbangTex;

    void Awake()
    {
        // background runs pass -sim on the command line; RoundManager reads it only in Start, after this Awake
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        bool simFlag = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-sim") >= 0;
        if ((rm != null && rm.simulation) || simFlag)
        {
            enabled = false;
            return;
        }
        Build();
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        RoundManager.RoundStarted -= ClearDecals;
        if (!subscribed) return;
        Weapon.AnyShot -= OnShot;
        Weapon.AnyPenetration -= OnPenetration;
        Health.AnyKilled -= OnKilled;
    }

    void Build()
    {
        Material spriteMat = new Material(Shader.Find("Sprites/Default"));
        Material softMat = new Material(Shader.Find("Sprites/Default"));
        softMat.mainTexture = MakeSoftCircle(32, 0.2f);

        holeSprite = MakeSprite(MakeHole(64));
        bloodSprite = MakeSprite(MakeSplat(64));
        headIcon = headshotIcon != null ? headshotIcon : MakeHeadIcon(32);
        noScopeTex = noScopeIcon != null ? noScopeIcon : MakeNoScopeIcon(32);
        jumpTex = jumpIcon != null ? jumpIcon : MakeJumpIcon(32);
        wallbangTex = wallbangIcon != null ? wallbangIcon : Resources.Load<Texture2D>("KillIcons/wallbang");
        if (wallbangTex == null) wallbangTex = MakeWallbangIcon(32);

        flashFx = MakeParticles("Flash", softMat, new Color(1f, 0.9f, 0.6f, 1f), 0.35f, 0.05f, 0f, 0.1f, 0f, 5f);
        sparkFx = MakeParticles("Sparks", softMat, new Color(1f, 0.85f, 0.45f, 1f), 0.04f, 0.4f, 2f, 5f, 1.2f, 35f);
        dustFx = MakeParticles("Dust", softMat, new Color(0.7f, 0.65f, 0.58f, 0.5f), 0.3f, 0.7f, 0.4f, 1.2f, -0.05f, 45f);
        bloodFx = MakeParticles("Blood", softMat, new Color(0.55f, 0.02f, 0.02f, 1f), 0.09f, 0.7f, 1.5f, 4.5f, 2.5f, 55f);

        for (int i = 0; i < tracerPool; i++)
        {
            GameObject go = new GameObject("Tracer");
            go.transform.SetParent(transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.useWorldSpace = true;
            lr.widthMultiplier = tracerWidth;
            lr.sharedMaterial = spriteMat;
            lr.startColor = new Color(tracerColor.r, tracerColor.g, tracerColor.b, 0f);
            lr.endColor = tracerColor;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;

            tracers.Add(new Tracer { line = lr });
        }

        for (int i = 0; i < decalPool; i++)
        {
            GameObject go = new GameObject("Decal");
            go.transform.SetParent(transform, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.enabled = false;

            decals.Add(new Decal { sr = sr });
        }

        // knife scratches: three looks for a slash, one for a stab
        slashSprites = new Sprite[3];
        for (int i = 0; i < slashSprites.Length; i++)
            slashSprites[i] = MakeSprite(MakeScratchTexture(256, 80, 17 + i * 31, false));
        stabSprite = MakeSprite(MakeScratchTexture(128, 48, 5, true));
        for (int i = 0; i < scratchPool; i++)
        {
            GameObject go = new GameObject("Scratch");
            go.transform.SetParent(transform, false);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.enabled = false;

            scratches.Add(new Decal { sr = sr });
        }
        instance = this;

        GameObject lightGo = new GameObject("MuzzleFlashLight");
        lightGo.transform.SetParent(transform, false);
        flashLight = lightGo.AddComponent<Light>();
        flashLight.type = LightType.Point;
        flashLight.color = new Color(1f, 0.8f, 0.5f);
        flashLight.range = 7f;
        flashLight.intensity = 4f;
        flashLight.shadows = LightShadows.None;
        flashLight.enabled = false;

        Weapon.AnyShot += OnShot;
        Weapon.AnyPenetration += OnPenetration;
        Health.AnyKilled += OnKilled;
        RoundManager.RoundStarted += ClearDecals;
        subscribed = true;

        // killed players fall over instead of vanishing
        if (GetComponent<Corpses>() == null) gameObject.AddComponent<Corpses>();

        // sounds, and bots hearing the player
        if (GetComponent<GameAudio>() == null) gameObject.AddComponent<GameAudio>();

        // weapon models in the bots' hands
        if (GetComponent<BotWeaponModels>() == null) gameObject.AddComponent<BotWeaponModels>();
    }

    ParticleSystem MakeParticles(string name, Material mat, Color color, float size, float life,
                                 float speedMin, float speedMax, float gravity, float cone)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);

        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = life;
        main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.6f, size);
        main.startColor = color;
        main.gravityModifier = gravity;
        main.maxParticles = 400;

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = cone;
        shape.radius = 0.01f;

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        fade.color = g;

        go.GetComponent<ParticleSystemRenderer>().sharedMaterial = mat;
        ps.Play();
        return ps;
    }

    static void Burst(ParticleSystem ps, Vector3 pos, Vector3 dir, int count)
    {
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.up;
        ps.transform.position = pos;
        ps.transform.rotation = Quaternion.LookRotation(dir);
        ps.Emit(count);
    }

    void OnShot(ShotInfo s)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos = cam.transform.position;
        float max2 = maxEffectDistance * maxEffectDistance;
        if ((s.origin - camPos).sqrMagnitude > max2 && (s.end - camPos).sqrMagnitude > max2) return;

        bool byPlayer = s.shooter != null && s.shooter.GetComponent<PlayerController>() != null;

        // the shots of the player (and of the teammate he watches) start at the muzzle of the first person model,
        // it draws its own flash
        Vector3 modelMuzzle = Vector3.zero;
        bool fromModel = ViewModel.TryGetMuzzle(s.shooter, out modelMuzzle);
        Vector3 muzzlePos = s.muzzle.position;
        if (fromModel)
            muzzlePos = modelMuzzle;
        else if (byPlayer)
            muzzlePos += s.muzzle.forward * 0.55f + s.muzzle.right * 0.16f - s.muzzle.up * 0.12f;
        else if (BotWeaponModels.TryGetMuzzle(s.shooter, out Vector3 gunMuzzle))
            muzzlePos = gunMuzzle;   // the gun model in the bot's hand (CharacterBody fist or under the head)
        else
            muzzlePos += s.muzzle.forward * 0.6f;

        if (!s.melee)
        {
            Vector3 toEnd = s.end - muzzlePos;
            float dist = toEnd.magnitude;
            if (dist > 1f) SpawnTracer(muzzlePos, toEnd / dist, dist);

            // one flash for a shotgun shell (the other pellets only have tracers), a small one for a silenced shot
            if (!s.pellet)
            {
                if (!fromModel) Burst(flashFx, muzzlePos, s.muzzle.forward, s.silenced ? 1 : 2);
                if (!s.silenced)
                {
                    flashLight.transform.position = muzzlePos;
                    flashLight.enabled = true;
                    flashTimer = 0.05f;
                }
            }
        }

        if (!s.hit) return;

        // any player or bot that was hit bleeds, teammates too while friendly fire is on
        bool damaging = s.target != null && s.target != s.shooter &&
                        (s.shooter == null || s.target.team != s.shooter.team || Weapon.FriendlyFire);

        if (s.melee && !damaging)
        {
            // the blade scratched a wall or the floor
            if (s.target == null) SpawnScratch(s);
            return;
        }

        if (damaging)
        {
            // the pellets of one shell: less blood each and one stain on the floor
            Burst(bloodFx, s.end, s.normal, s.pellet ? 6 : 14);
            if (!s.pellet) SpawnFloorBlood(s.target.transform.position, 0.3f);

            if (byPlayer)
            {
                hitMarkerUntil = Time.time + 0.15f;
                hitMarkerColor = Color.white;
            }
        }
        else
        {
            Burst(sparkFx, s.end, s.normal, 7);
            Burst(dustFx, s.end, s.normal, 4);

            if (s.target == null)
                SpawnDecal(holeSprite, s.end, s.normal, 0.14f, new Color(1f, 1f, 1f, 0.9f));
        }
    }

    // a bullet went through an object: a hole where it went in and where it came out
    void OnPenetration(Vector3 entry, Vector3 entryNormal, Vector3 exit, Vector3 exitNormal)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 camPos = cam.transform.position;
        float max2 = maxEffectDistance * maxEffectDistance;
        bool entryNear = (entry - camPos).sqrMagnitude <= max2;
        bool exitNear = (exit - camPos).sqrMagnitude <= max2;

        // the bullet went through a player or a bot (an enemy or a teammate): blood on both sides, no holes
        Health body = BodyAt(entry);
        if (body != null)
        {
            if (entryNear)
            {
                Burst(bloodFx, entry, entryNormal, 10);
                SpawnFloorBlood(body.transform.position, 0.3f);
            }
            if (exitNear && BodyAt(exit) != null) Burst(bloodFx, exit, exitNormal, 12);
            return;
        }

        if (entryNear)
        {
            Burst(sparkFx, entry, entryNormal, 5);
            Burst(dustFx, entry, entryNormal, 4);
            SpawnDecal(holeSprite, entry, entryNormal, 0.14f, new Color(1f, 1f, 1f, 0.9f));
        }

        // a wall exit right on a body gets no hole (it would hang in the air when they move)
        if (exitNear && BodyAt(exit) == null)
        {
            Burst(dustFx, exit, exitNormal, 6);
            SpawnDecal(holeSprite, exit, exitNormal, 0.16f, new Color(1f, 1f, 1f, 0.9f));
        }
    }

    // the player or bot the point is on, null if it is not on a body
    Health BodyAt(Vector3 point)
    {
        int n = Physics.OverlapSphereNonAlloc(point, 0.05f, overlap, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < n; i++)
        {
            Health h = overlap[i].GetComponentInParent<Health>();
            if (h != null) return h;
        }
        return null;
    }

    // killfeed picture of a weapon or grenade: Resources/KillIcons/<name>, loaded once
    Texture2D WeaponIcon(WeaponData weapon, GrenadeData grenade = null)
    {
        string iconName = weapon != null ? weapon.weaponName : grenade != null ? grenade.grenadeName : null;
        if (string.IsNullOrEmpty(iconName)) return null;

        if (!weaponIcons.TryGetValue(iconName, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + iconName);
            weaponIcons[iconName] = tex;
        }
        return tex;
    }

    void OnKilled(KillInfo k)
    {
        Health victim = k.victim;
        Health killer = k.killer;
        bool headshot = k.headshot;
        bool noScope = k.noScope;
        bool inAir = k.inAir;

        Burst(bloodFx, victim.transform.position + Vector3.up * 0.4f, Vector3.up, headshot ? 55 : 40);
        SpawnFloorBlood(victim.transform.position, 1.1f);

        feed.Add(new FeedEntry
        {
            killer = killer != null ? killer.name : "World",
            killerTeam = killer != null ? killer.team : -1,
            assister = k.assister != null ? k.assister.name : null,
            assisterTeam = k.assister != null ? k.assister.team : -1,
            victim = victim.name,
            victimTeam = victim.team,
            playerKiller = IsPlayer(killer),
            playerAssister = IsPlayer(k.assister),
            playerVictim = IsPlayer(victim),
            headshot = headshot,
            noScope = noScope,
            inAir = inAir,
            wallbang = k.wallbang,
            weapon = k.weapon,
            weaponIcon = WeaponIcon(k.weapon, k.grenade),
            expire = Time.time + 6f
        });
        if (feed.Count > 5) feed.RemoveAt(0);

        if (IsPlayer(killer))
        {
            hitMarkerUntil = Time.time + 0.35f;
            hitMarkerColor = new Color(1f, 0.25f, 0.25f);
        }
    }

    // the Health of the human player (not a bot or a training target)
    static bool IsPlayer(Health h)
    {
        return h != null && h.GetComponent<PlayerController>() != null;
    }

    void SpawnTracer(Vector3 start, Vector3 dir, float total)
    {
        Tracer t = tracers[nextTracer];
        nextTracer = (nextTracer + 1) % tracers.Count;

        t.start = start;
        t.dir = dir;
        t.total = total;
        t.travelled = 0f;
        t.active = true;
        t.line.enabled = true;
        t.line.SetPosition(0, start);
        t.line.SetPosition(1, start);
    }

    void SpawnFloorBlood(Vector3 pos, float size)
    {
        Vector3 origin = pos + new Vector3(Random.Range(-0.3f, 0.3f), 0f, Random.Range(-0.3f, 0.3f));

        // the nearest floor below; the hitboxes of the character models (legs) are not the floor
        int n = Physics.RaycastNonAlloc(origin, Vector3.down, floorHits, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        int best = -1;
        for (int i = 0; i < n; i++)
        {
            if (best >= 0 && floorHits[i].distance >= floorHits[best].distance) continue;
            if (floorHits[i].collider.GetComponentInParent<Health>() != null) continue;
            best = i;
        }
        if (best >= 0)
            SpawnDecal(bloodSprite, floorHits[best].point, floorHits[best].normal, size * Random.Range(0.8f, 1.3f), new Color(1f, 1f, 1f, 0.85f));
    }

    readonly RaycastHit[] floorHits = new RaycastHit[16];

    void SpawnDecal(Sprite sprite, Vector3 point, Vector3 normal, float size, Color color)
    {
        Decal d = decals[nextDecal];
        nextDecal = (nextDecal + 1) % decals.Count;

        SpriteRenderer sr = d.sr;
        sr.sprite = sprite;
        sr.color = color;

        Transform t = sr.transform;
        t.position = point + normal * 0.015f;
        t.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        t.localScale = Vector3.one * size;

        sr.enabled = true;
        d.active = true;
        d.expire = Time.time + decalLifetime;
        d.baseAlpha = color.a;
    }

    void Update()
    {
        float dt = Time.deltaTime;

        foreach (Tracer t in tracers)
        {
            if (!t.active) continue;

            t.travelled += tracerSpeed * dt;
            float head = Mathf.Min(t.travelled, t.total);
            float tail = Mathf.Max(0f, t.travelled - tracerLength);

            if (tail >= t.total)
            {
                t.active = false;
                t.line.enabled = false;
                continue;
            }

            t.line.SetPosition(0, t.start + t.dir * tail);
            t.line.SetPosition(1, t.start + t.dir * head);
        }

        FadeDecals(decals, decalFadeTime);
        FadeDecals(scratches, scratchFadeTime);
        UpdatePieces(dt);

        if (flashTimer > 0f)
        {
            flashTimer -= dt;
            if (flashTimer <= 0f) flashLight.enabled = false;
        }
    }

    // decals fade out in their last seconds, then they are free again
    static void FadeDecals(List<Decal> list, float fadeTime)
    {
        foreach (Decal d in list)
        {
            if (!d.active) continue;

            float left = d.expire - Time.time;
            if (left <= 0f)
            {
                d.sr.enabled = false;
                d.active = false;
                continue;
            }

            if (left < fadeTime)
            {
                Color c = d.sr.color;
                c.a = d.baseAlpha * (left / fadeTime);
                d.sr.color = c;
            }
        }
    }

    // ---------------- knife scratches ----------------

    // the knife hit a wall or the floor: thin bright scratches along the swing (a slash goes across, left to right),
    // a stab leaves a short gouge
    void SpawnScratch(ShotInfo s)
    {
        if (scratches.Count == 0) return;

        // the knife's sphere may touch the surface off the line of the blow: the surface on that line, if it is there
        Vector3 point = s.end;
        Vector3 normal = s.normal;
        Vector3 line = s.end - s.origin;
        float length = line.magnitude;
        if (length > 0.01f && SurfaceHit(s.origin, line / length, length + 0.3f, out RaycastHit exact) &&
            (exact.point - s.end).sqrMagnitude < 0.3f * 0.3f)
        {
            point = exact.point;
            normal = exact.normal;
        }
        if (normal.sqrMagnitude < 0.25f) return;
        normal.Normalize();

        // the direction of the cut on the surface: a slash across the view, a stab along the blade (up), on the floor forward
        Transform from = s.muzzle;
        Vector3 right = from != null ? from.right : Vector3.right;
        Vector3 up = from != null ? from.up : Vector3.up;
        Vector3 forward = from != null ? from.forward : (length > 0.01f ? line / length : Vector3.forward);
        Vector3 along = Vector3.ProjectOnPlane(s.stab ? up : right, normal);
        if (along.sqrMagnitude < 0.09f) along = Vector3.ProjectOnPlane(forward, normal);
        if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(Vector3.right, normal);
        if (along.sqrMagnitude < 1e-4f) along = Vector3.ProjectOnPlane(Vector3.forward, normal);
        along.Normalize();
        // a slash runs a little down, never twice the same way
        along = Quaternion.AngleAxis(s.stab ? Random.Range(-10f, 10f) : Random.Range(-25f, 12f), normal) * along;

        Decal d = scratches[nextScratch];
        nextScratch = (nextScratch + 1) % scratches.Count;

        SpriteRenderer sr = d.sr;
        sr.sprite = s.stab ? stabSprite : slashSprites[Random.Range(0, slashSprites.Length)];
        sr.color = Color.white;
        sr.flipX = Random.value < 0.5f;
        sr.flipY = Random.value < 0.5f;

        // the sprite's x goes along the cut, it lies on the surface
        Transform t = sr.transform;
        t.position = point + normal * 0.012f;
        t.rotation = Quaternion.LookRotation(-normal, Vector3.Cross(-normal, along));
        t.localScale = Vector3.one * (s.stab ? Random.Range(0.08f, 0.1f) : Random.Range(0.3f, 0.38f));

        sr.enabled = true;
        d.active = true;
        d.expire = Time.time + scratchLifetime;
        d.baseAlpha = 1f;
    }

    // 2-3 thin bright metal scratches with dark edges along x, thin at both ends;
    // a stab: one short deep gouge, dark inside with a bright rim
    static Texture2D MakeScratchTexture(int w, int h, int seed, bool stab)
    {
        System.Random rng = new System.Random(seed);
        Color[] px = new Color[w * h];
        int count = stab ? 1 : 2 + rng.Next(2);

        for (int s = 0; s < count; s++)
        {
            float x0 = w * (stab ? 0.1f : 0.02f + 0.16f * (float)rng.NextDouble());
            float x1 = w * (stab ? 0.9f : 0.8f + 0.18f * (float)rng.NextDouble());
            float y0 = h * 0.5f + (s - (count - 1) * 0.5f) * h * 0.21f + ((float)rng.NextDouble() - 0.5f) * h * 0.08f;
            float slope = ((float)rng.NextDouble() - 0.5f) * h * 0.14f;
            float bend = ((float)rng.NextDouble() - 0.5f) * h * 0.12f;
            float core = stab ? h * 0.15f : 0.8f + 1.1f * (float)rng.NextDouble();   // half width, pixels
            float edge = stab ? 1.8f : 1.3f + 0.9f * (float)rng.NextDouble();
            float noiseSeed = (float)rng.NextDouble() * 50f;

            int xa = Mathf.Max(0, Mathf.FloorToInt(x0)), xb = Mathf.Min(w - 1, Mathf.CeilToInt(x1));
            for (int x = xa; x <= xb; x++)
            {
                float t = Mathf.Clamp01((x + 0.5f - x0) / Mathf.Max(1f, x1 - x0));
                float taper = Mathf.Pow(Mathf.Sin(t * Mathf.PI), stab ? 0.6f : 0.35f);
                float yc = y0 + slope * (t - 0.5f) * 2f + bend * Mathf.Sin(t * Mathf.PI);
                float half = core * taper;
                float outer = half + edge * Mathf.Max(taper, 0.4f);
                // the blade pressed harder in some places
                float n = Mathf.PerlinNoise(noiseSeed + x * 0.07f, s * 3.7f);
                float strength = Mathf.Clamp01(taper * 1.6f) * (stab ? 1f : Mathf.Lerp(0.55f, 1f, n));

                int ya = Mathf.Max(0, Mathf.FloorToInt(yc - outer - 1f)), yb = Mathf.Min(h - 1, Mathf.CeilToInt(yc + outer + 1f));
                for (int y = ya; y <= yb; y++)
                {
                    float d = Mathf.Abs(y + 0.5f - yc);
                    int i = y * w + x;
                    if (d < half + 0.5f)
                    {
                        // the scratch: bright bare metal (the gouge is dark inside, its rim catches the light)
                        float a = Mathf.Clamp01(half + 0.5f - d) * strength;
                        float b = 0.78f + 0.2f * n;
                        Color col = new Color(b, b, b + 0.03f, 1f);
                        if (stab && d < half * 0.6f) col = new Color(0.04f, 0.035f, 0.03f, 1f);
                        col.a = a;
                        px[i] = Over(col, px[i]);
                    }
                    else if (d < outer + 0.5f)
                    {
                        // the dark torn edges, under the bright metal of another scratch
                        float a = 0.7f * Mathf.Clamp01(outer + 0.5f - d) * strength;
                        px[i] = Over(px[i], new Color(0.05f, 0.045f, 0.04f, a));
                    }
                }
            }
        }

        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.anisoLevel = 4;
        tex.SetPixels(px);
        tex.Apply(true);
        return tex;
    }

    // a over b (straight alpha)
    static Color Over(Color a, Color b)
    {
        float outA = a.a + b.a * (1f - a.a);
        if (outA <= 1e-5f) return new Color(0f, 0f, 0f, 0f);
        Color c = (a * a.a + b * b.a * (1f - a.a)) / outA;
        c.a = outA;
        return c;
    }

    // ---------------- dropped magazines and shells ----------------

    // A part of a gun falls out (an empty magazine): a copy of source (its meshes) at this position, rotation and world
    // scale is thrown with this velocity and spin (degrees per second). from: a point it surely did not come through a
    // wall from (the eye for a first person model). key: the same key = the same meshes (pooled copies are reused).
    // Returns false when there are no effects (simulation, no CombatEffects in the scene).
    public static bool DropPiece(GameObject source, string key, Vector3 position, Quaternion rotation, Vector3 scale,
                                 Vector3 velocity, Vector3 spin, Vector3 from)
    {
        CombatEffects fx = instance;
        if (fx == null || !fx.isActiveAndEnabled || source == null) return false;
        return fx.Drop(source, key, position, rotation, scale, velocity, spin, from);
    }

    // An empty shell of this weapon: a pistol, rifle or sniper case, or a red shotgun hull; real size, its long axis is
    // the rotation's up. Same as DropPiece otherwise.
    public static bool DropShell(WeaponData weapon, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 spin, Vector3 from)
    {
        CombatEffects fx = instance;
        if (fx == null || !fx.isActiveAndEnabled) return false;
        int kind = ShellKind(weapon);
        return fx.Drop(fx.ShellTemplate(kind), "shell" + kind, position, rotation, Vector3.one, velocity, spin, from);
    }

    // 0 pistol and SMG rounds, 1 rifle, 2 sniper, 3 shotgun hull
    static int ShellKind(WeaponData d)
    {
        if (d == null) return 1;
        if (d.IsShotgun || d.category == WeaponCategory.Shotgun) return 3;
        if (d.category == WeaponCategory.Pistol || d.category == WeaponCategory.SMG) return 0;
        if (d.category == WeaponCategory.Sniper) return 2;
        return 1;
    }

    bool Drop(GameObject source, string key, Vector3 position, Quaternion rotation, Vector3 scale, Vector3 velocity, Vector3 spin, Vector3 from)
    {
        if (source == null) return false;
        Camera cam = Camera.main;
        if (cam != null && (position - cam.transform.position).sqrMagnitude > pieceMaxDistance * pieceMaxDistance) return true;

        // it must not start behind a wall
        Vector3 path = position - from;
        float length = path.magnitude;
        if (length > 0.01f && SurfaceHit(from, path / length, length, out RaycastHit wall))
            position = wall.point + wall.normal * 0.03f;

        while (pieces.Count >= Mathf.Max(1, pieceLimit)) Recycle(0);

        Piece p = null;
        if (freePieces.TryGetValue(key, out Stack<Piece> free))
        {
            while (p == null && free.Count > 0)
            {
                p = free.Pop();
                if (p.go == null) p = null;
            }
        }
        if (p == null) p = NewPiece(source, key);

        p.t.SetPositionAndRotation(position, rotation);
        p.t.localScale = scale;
        p.scale = scale;
        p.velocity = velocity;
        p.spin = spin;
        p.born = Time.time;
        p.bounces = 0;
        p.landed = false;
        p.go.SetActive(true);
        pieces.Add(p);
        return true;
    }

    // a copy of a gun part: only its meshes, on the default layer, with shadows; its size is measured once
    Piece NewPiece(GameObject source, string key)
    {
        GameObject go = Instantiate(source);
        go.name = "Dropped " + source.name;
        Transform t = go.transform;
        t.SetParent(null, false);
        t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        t.localScale = Vector3.one;

        foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
        foreach (MonoBehaviour b in go.GetComponentsInChildren<MonoBehaviour>(true)) Destroy(b);
        removeBuffer.Clear();
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            // the shadow-only copies CharacterBody adds to the model the camera is in
            if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && r.gameObject != go)
            {
                removeBuffer.Add(r.gameObject);
                continue;
            }
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            r.receiveShadows = true;
            r.enabled = true;
        }
        foreach (GameObject g in removeBuffer) DestroyImmediate(g);
        removeBuffer.Clear();
        foreach (Transform c in go.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = 0;

        Piece p = new Piece { go = go, t = t, key = key };
        bool any = false;
        Bounds bounds = new Bounds();
        foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            Bounds mb = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 q = t.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (any) bounds.Encapsulate(q);
                else bounds = new Bounds(q, Vector3.zero);
                any = true;
            }
        }
        Vector3 size = any ? bounds.size : Vector3.one * 0.02f;
        p.center = any ? bounds.center : Vector3.zero;
        int axis = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
        p.thinAxis = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
        p.thinHalf = Mathf.Max(0.001f, size[axis] * 0.5f);

        go.SetActive(false);
        return p;
    }

    void UpdatePieces(float dt)
    {
        if (dt <= 0f || pieces.Count == 0) return;
        float now = Time.time;
        for (int i = pieces.Count - 1; i >= 0; i--)
        {
            Piece p = pieces[i];
            if (p.go == null)
            {
                pieces.RemoveAt(i);
                continue;
            }

            if (!p.landed)
            {
                // fell out of the map, or never found a floor
                if (now - p.born > 5f) Recycle(i);
                else Fly(p, dt);
                continue;
            }

            // lies for a moment, then shrinks to the point it touches the floor with
            float shrink = (now - p.landedAt - pieceLieTime) / Mathf.Max(pieceShrinkTime, 0.01f);
            if (shrink <= 0f) continue;
            if (shrink >= 1f)
            {
                Recycle(i);
                continue;
            }
            float k = 1f - shrink;
            p.t.position = p.contact + (p.restPos - p.contact) * k;
            p.t.localScale = p.scale * k;
        }
    }

    // one frame of the fall: gravity, a ray along the way; it bounces off walls and the floor and settles on the floor
    void Fly(Piece p, float dt)
    {
        p.velocity += Physics.gravity * dt;
        Vector3 step = p.velocity * dt;
        float distance = step.magnitude;
        Vector3 offset = p.t.rotation * Vector3.Scale(p.center, p.scale);
        float radius = ThinHalf(p);

        if (distance > 1e-6f && SurfaceHit(p.t.position + offset, step / distance, distance + radius, out RaycastHit hit))
        {
            Vector3 n = hit.normal;
            float into = -Vector3.Dot(p.velocity, n);
            if (n.y > 0.55f && (p.bounces >= 2 || into < 1.5f))
            {
                Land(p, hit.point, n);
                return;
            }

            // a bounce: most of the speed into the surface is lost, some along it
            Vector3 normalPart = Vector3.Dot(p.velocity, n) * n;
            p.velocity = (p.velocity - normalPart) * 0.55f - normalPart * 0.3f;
            p.spin *= 0.5f;
            p.bounces++;
            p.t.position = hit.point + n * radius - offset;
            // stuck in a corner: it lies down where it is
            if (p.bounces > 6) Land(p, hit.point, n.y > 0.3f ? n : Vector3.up);
            return;
        }

        p.t.position += step;
        p.t.rotation = Quaternion.Euler(p.spin * dt) * p.t.rotation;
    }

    // it falls onto its flattest side
    void Land(Piece p, Vector3 point, Vector3 normal)
    {
        Vector3 axis = p.t.rotation * Vector3.Scale(p.thinAxis, p.scale);
        if (Vector3.Dot(axis, normal) < 0f) axis = -axis;
        Quaternion rot = axis.sqrMagnitude > 1e-12f ? Quaternion.FromToRotation(axis.normalized, normal) * p.t.rotation : p.t.rotation;

        p.t.rotation = rot;
        p.contact = point;
        p.restPos = point + normal * ThinHalf(p) - rot * Vector3.Scale(p.center, p.scale);
        p.t.position = p.restPos;
        p.velocity = Vector3.zero;
        p.landed = true;
        p.landedAt = Time.time;
    }

    // half of its smallest size in the world
    static float ThinHalf(Piece p)
    {
        return p.thinHalf * Mathf.Abs(Vector3.Dot(p.thinAxis, p.scale));
    }

    // the nearest surface along the ray; players, bots and their hitboxes are not surfaces
    bool SurfaceHit(Vector3 origin, Vector3 dir, float distance, out RaycastHit best)
    {
        best = default;
        int n = Physics.RaycastNonAlloc(origin, dir, pieceHits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            if (found && pieceHits[i].distance >= best.distance) continue;
            if (pieceHits[i].collider.GetComponentInParent<Health>() != null) continue;
            best = pieceHits[i];
            found = true;
        }
        return found;
    }

    void Recycle(int i)
    {
        Piece p = pieces[i];
        pieces.RemoveAt(i);
        if (p.go == null) return;

        p.go.SetActive(false);
        if (!freePieces.TryGetValue(p.key, out Stack<Piece> free))
        {
            free = new Stack<Piece>();
            freePieces[p.key] = free;
        }
        // shells come often, magazines of one gun rarely
        int keep = p.key.StartsWith("shell") ? 24 : 4;
        if (free.Count < keep) free.Push(p);
        else Destroy(p.go);
    }

    void ClearPieces()
    {
        while (pieces.Count > 0) Recycle(pieces.Count - 1);
    }

    // cartridge cases at real size along their local up (meters); a shotgun hull is red with a brass head
    GameObject ShellTemplate(int kind)
    {
        if (shellTemplates == null) shellTemplates = new GameObject[4];
        if (shellTemplates[kind] != null) return shellTemplates[kind];

        GameObject root = new GameObject("Shell template " + kind);
        root.transform.SetParent(transform, false);
        root.SetActive(false);

        Material brass = PieceMaterial("Brass", new Color(0.78f, 0.58f, 0.24f), 1f, 0.75f);
        switch (kind)
        {
            case 0:
                ShellPart(root.transform, Vector3.zero, 0.0099f, 0.019f, brass);    // 9 mm
                break;
            case 1:
                ShellPart(root.transform, Vector3.zero, 0.0112f, 0.045f, brass);    // 5.56 / 7.62
                break;
            case 2:
                ShellPart(root.transform, Vector3.zero, 0.0149f, 0.069f, brass);    // .338
                break;
            default:
                Material hull = PieceMaterial("Hull", new Color(0.62f, 0.07f, 0.05f), 0f, 0.45f);
                ShellPart(root.transform, new Vector3(0f, 0.006f, 0f), 0.0203f, 0.058f, hull);   // 12 gauge
                ShellPart(root.transform, new Vector3(0f, -0.029f, 0f), 0.0212f, 0.012f, brass);
                break;
        }
        shellTemplates[kind] = root;
        return root;
    }

    // a cylinder along y (Unity's cylinder is 2 units tall, radius 0.5)
    static void ShellPart(Transform parent, Vector3 pos, float diameter, float length, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        DestroyImmediate(go.GetComponent<Collider>());
        go.name = "Case";
        Transform t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localScale = new Vector3(diameter, length * 0.5f, diameter);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
    }

    Material PieceMaterial(string key, Color color, float metallic, float smoothness)
    {
        if (pieceMaterials.TryGetValue(key, out Material m) && m != null) return m;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        m = new Material(shader) { name = key };
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);
        pieceMaterials[key] = m;
        return m;
    }

    static Color TeamColor(int team)
    {
        if (team == 0) return new Color(1f, 0.35f, 0.35f);
        if (team == 1) return new Color(0.4f, 0.6f, 1f);
        return Color.white;
    }
    
    void ClearDecals()
    {
        foreach (Decal d in decals)
        {
            d.sr.enabled = false;
            d.active = false;
        }
        foreach (Decal d in scratches)
        {
            d.sr.enabled = false;
            d.active = false;
        }
        ClearPieces();
        feed.Clear();
    }

    // arrow up over a ground line
    static Texture2D MakeJumpIcon(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                bool head = v > 0.58f && v < 0.92f && Mathf.Abs(u - 0.5f) <= 0.28f * (0.92f - v) / 0.34f;
                bool shaft = Mathf.Abs(u - 0.5f) < 0.07f && v > 0.30f && v <= 0.60f;
                bool ground = v > 0.10f && v < 0.19f && u > 0.2f && u < 0.8f;
                tex.SetPixel(x, y, head || shaft || ground ? Color.white : Color.clear);
            }

        tex.Apply();
        return tex;
    }

    // wall slab with a hole and a bullet streak with sparks coming out of it to the right
    static Texture2D MakeWallbangIcon(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 tip = new Vector2(0.84f, 0.55f);
        Vector2[] rays = { new Vector2(1f, 0f), new Vector2(0.77f, 0.64f), new Vector2(0.77f, -0.64f) };

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                float v = (y + 0.5f) / size;
                float hu = (u - 0.22f) / 0.07f;
                float hv = (v - 0.55f) / 0.11f;
                bool hole = hu * hu + hv * hv < 1f;
                bool slab = u > 0.08f && u < 0.36f && v > 0.08f && v < 0.92f && !hole;
                bool streak = u > 0.44f && u < 0.78f && Mathf.Abs(v - 0.55f) < 0.02f + 0.04f * (u - 0.44f) / 0.34f;

                bool spark = false;
                Vector2 d = new Vector2(u, v) - tip;
                foreach (Vector2 r in rays)
                {
                    float along = Vector2.Dot(d, r);
                    float across = Mathf.Abs(d.x * r.y - d.y * r.x);
                    if (along > 0.05f && along < 0.13f && across < 0.025f) spark = true;
                }

                tex.SetPixel(x, y, slab || streak || spark ? Color.white : Color.clear);
            }

        tex.Apply();
        return tex;
    }

    // crossed-out scope: a ring with a crosshair and a red strike
    static Texture2D MakeNoScopeIcon(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(size * 0.5f, size * 0.5f);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Vector2.Distance(p, c);
                Color col = Color.clear;

                bool ring = d < size * 0.42f && d > size * 0.32f;
                bool cross = d < size * 0.42f && (Mathf.Abs(p.x - c.x) < size * 0.05f || Mathf.Abs(p.y - c.y) < size * 0.05f);
                if (ring || cross) col = Color.white;

                // diagonal strike from the top left to the bottom right
                bool strike = Mathf.Abs(p.x - (size - p.y)) < size * 0.07f && p.x > size * 0.06f && p.x < size * 0.94f;
                if (strike) col = new Color(0.95f, 0.15f, 0.15f, 1f);

                tex.SetPixel(x, y, col);
            }

        tex.Apply();
        return tex;
    }

    static Texture2D MakeHeadIcon(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(size * 0.5f, size * 0.58f);
        float r = size * 0.38f;
        Vector2 eyeL = new Vector2(size * 0.37f, size * 0.55f);
        Vector2 eyeR = new Vector2(size * 0.63f, size * 0.55f);
        Vector2 hole = new Vector2(size * 0.5f, size * 0.78f);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                Color col = Color.clear;

                bool cranium = Vector2.Distance(p, c) < r;
                bool jaw = Mathf.Abs(p.x - size * 0.5f) < size * 0.2f && p.y > size * 0.12f && p.y < size * 0.35f;
                if (cranium || jaw) col = Color.white;

                if (Vector2.Distance(p, eyeL) < size * 0.09f || Vector2.Distance(p, eyeR) < size * 0.09f)
                    col = new Color(0.1f, 0.1f, 0.1f, 1f);

                if (Vector2.Distance(p, hole) < size * 0.07f)
                    col = new Color(0.9f, 0.1f, 0.1f, 1f);

                tex.SetPixel(x, y, col);
            }

        tex.Apply();
        return tex;
    }

    void OnGUI()
    {
        if (Time.time < hitMarkerUntil)
        {
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;

            Matrix4x4 old = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, new Vector2(cx, cy));
            GUI.color = hitMarkerColor;
            GUI.DrawTexture(new Rect(cx - 16f, cy - 1.5f, 32f, 3f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(cx - 1.5f, cy - 16f, 3f, 32f), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.matrix = old;
        }

        if (feedStyle == null)
        {
            feedStyle = new GUIStyle(GUI.skin.label);
            feedStyle.fontSize = 20;
            feedStyle.fontStyle = FontStyle.Bold;
        }

        // killer [+ assister] [jump] [noscope] WEAPON [wallbang] [headshot] victim, right-aligned
        float y = 70f;
        float lineH = feedStyle.CalcSize(new GUIContent("Ag")).y;
        float flagH = Mathf.Round(lineH - 4f);   // jump, noscope, wallbang and headshot icons
        float gap = 6f;

        foreach (FeedEntry e in feed)
        {
            if (Time.time > e.expire) continue;

            parts.Clear();
            AddText(e.killer, TeamColor(e.killerTeam));
            if (!string.IsNullOrEmpty(e.assister))
            {
                AddText(" + ", Color.white);
                AddText(e.assister, TeamColor(e.assisterTeam));
            }
            if (e.inAir) AddIcon(jumpTex, flagH);
            if (e.noScope) AddIcon(noScopeTex, flagH);
            if (e.weaponIcon != null) AddIcon(e.weaponIcon, lineH);
            else AddText("  >  ", Color.white);   // fall, out of the map, or no icon for this weapon
            if (e.wallbang) AddIcon(wallbangTex, flagH);
            if (e.headshot) AddIcon(headIcon, flagH);
            AddText(e.victim, TeamColor(e.victimTeam));

            // a gap before and after every icon, texts follow each other directly
            float total = 0f;
            for (int i = 0; i < parts.Count; i++)
                total += parts[i].width + PartGap(i, gap);

            float x = Screen.width - 20f - total;
            float alpha = Mathf.Clamp01((e.expire - Time.time) / 0.5f);   // fades out in the last half second
            Rect back = new Rect(x - 6f, y - 2f, total + 12f, lineH + 4f);

            // like in CS: the player's kills and assists get a red frame and a stronger reddish background,
            // the player's own death a dark red background
            Color backColor = new Color(0f, 0f, 0f, 0.35f);
            if (e.playerVictim) backColor = new Color(0.45f, 0.04f, 0.04f, 0.75f);
            else if (e.playerKiller || e.playerAssister) backColor = new Color(0.22f, 0.05f, 0.05f, 0.6f);
            backColor.a *= alpha;

            GUI.color = backColor;
            GUI.DrawTexture(back, Texture2D.whiteTexture);

            if (e.playerKiller || e.playerAssister)
                DrawFrame(back, 2f, new Color(0.95f, 0.2f, 0.2f, alpha));

            GUI.color = new Color(1f, 1f, 1f, alpha);

            for (int i = 0; i < parts.Count; i++)
            {
                FeedPart p = parts[i];
                x += PartGap(i, gap);

                if (p.icon != null)
                {
                    GUI.DrawTexture(new Rect(x, y + (lineH - p.height) * 0.5f, p.width, p.height), p.icon);
                }
                else
                {
                    GUI.contentColor = p.color;
                    GUI.Label(new Rect(x, y, p.width, lineH), p.text, feedStyle);
                }
                x += p.width;
            }

            y += lineH + 6f;
        }
        GUI.color = Color.white;
        GUI.contentColor = Color.white;
    }

    // a frame of the given thickness along the inner edge of the rect
    static void DrawFrame(Rect r, float thick, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - thick, r.width, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y + thick, thick, r.height - thick * 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - thick, r.y + thick, thick, r.height - thick * 2f), Texture2D.whiteTexture);
    }

    void AddText(string text, Color color)
    {
        GUIContent c = new GUIContent(text);
        parts.Add(new FeedPart { text = c, width = feedStyle.CalcSize(c).x, color = color });
    }

    // icon of the given height, the width keeps its aspect
    void AddIcon(Texture2D icon, float height)
    {
        if (icon == null) return;
        float width = height * icon.width / Mathf.Max(1, icon.height);
        parts.Add(new FeedPart { icon = icon, width = width, height = height });
    }

    // space before part i: next to an icon there is a gap, between two texts there is none
    float PartGap(int i, float gap)
    {
        if (i == 0) return 0f;
        bool textPair = parts[i].icon == null && parts[i - 1].icon == null;
        return textPair ? 0f : gap;
    }

    static Texture2D MakeSoftCircle(int size, float hardness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float r = size * 0.5f;
        Vector2 c = new Vector2(r - 0.5f, r - 0.5f);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / r;
                float a = Mathf.Clamp01((1f - d) / Mathf.Max(0.01f, 1f - hardness));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }

        tex.Apply();
        return tex;
    }

    static Texture2D MakeHole(int size)
    {
        Texture2D tex = MakeSoftCircle(size, 0.45f);
        Color[] px = tex.GetPixels();
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color(0.05f, 0.05f, 0.05f, px[i].a);
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    static Texture2D MakeSplat(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        float r = size * 0.5f;
        Vector2 c = new Vector2(r - 0.5f, r - 0.5f);
        float seed = Random.Range(0f, 100f);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / r;
                float n = Mathf.PerlinNoise(seed + x * 0.12f, seed + y * 0.12f);
                float a = Mathf.Clamp01((1f - d) * 2.2f) * Mathf.SmoothStep(0.2f, 0.65f, n + (1f - d) * 0.5f);
                tex.SetPixel(x, y, new Color(0.45f, 0.02f, 0.02f, a));
            }

        tex.Apply();
        return tex;
    }

    static Sprite MakeSprite(Texture2D tex)
    {
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
    }
}