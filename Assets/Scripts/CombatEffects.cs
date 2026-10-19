using System.Collections.Generic;
using UnityEngine;

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

        // the player's shots start at the muzzle of the weapon model, it draws its own flash
        Vector3 modelMuzzle = Vector3.zero;
        bool fromModel = byPlayer && ViewModel.TryGetMuzzle(s.shooter, out modelMuzzle);
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

            if (!fromModel) Burst(flashFx, muzzlePos, s.muzzle.forward, 2);
            flashLight.transform.position = muzzlePos;
            flashLight.enabled = true;
            flashTimer = 0.05f;
        }

        if (!s.hit) return;

        // any player or bot that was hit bleeds, teammates too while friendly fire is on
        bool damaging = s.target != null && s.target != s.shooter &&
                        (s.shooter == null || s.target.team != s.shooter.team || Weapon.FriendlyFire);

        if (s.melee && !damaging) return;

        if (damaging)
        {
            Burst(bloodFx, s.end, s.normal, 14);
            SpawnFloorBlood(s.target.transform.position, 0.3f);

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

    // killfeed picture of a weapon: Resources/KillIcons/<weapon name>, loaded once
    Texture2D WeaponIcon(WeaponData weapon)
    {
        if (weapon == null || string.IsNullOrEmpty(weapon.weaponName)) return null;

        if (!weaponIcons.TryGetValue(weapon.weaponName, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + weapon.weaponName);
            weaponIcons[weapon.weaponName] = tex;
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
            weaponIcon = WeaponIcon(k.weapon),
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
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit fh, 4f))
            SpawnDecal(bloodSprite, fh.point, fh.normal, size * Random.Range(0.8f, 1.3f), new Color(1f, 1f, 1f, 0.85f));
    }

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

        foreach (Decal d in decals)
        {
            if (!d.active) continue;

            float left = d.expire - Time.time;
            if (left <= 0f)
            {
                d.sr.enabled = false;
                d.active = false;
                continue;
            }

            if (left < decalFadeTime)
            {
                Color c = d.sr.color;
                c.a = d.baseAlpha * (left / decalFadeTime);
                d.sr.color = c;
            }
        }

        if (flashTimer > 0f)
        {
            flashTimer -= dt;
            if (flashTimer <= 0f) flashLight.enabled = false;
        }
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