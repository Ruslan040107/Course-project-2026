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
        public string killer, victim;
        public int killerTeam, victimTeam;
        public float expire;
    }

    readonly List<Tracer> tracers = new List<Tracer>();
    readonly List<Decal> decals = new List<Decal>();
    readonly List<FeedEntry> feed = new List<FeedEntry>();

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

    void Awake()
    {
        RoundManager rm = FindAnyObjectByType<RoundManager>();  
        if (rm != null && rm.simulation)
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
        Health.AnyKilled += OnKilled;
        RoundManager.RoundStarted += ClearDecals;
        subscribed = true;
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

        Vector3 muzzlePos = s.muzzle.position;
        if (byPlayer)
            muzzlePos += s.muzzle.forward * 0.55f + s.muzzle.right * 0.16f - s.muzzle.up * 0.12f;
        else
            muzzlePos += s.muzzle.forward * 0.6f;

        if (!s.melee)
        {
            Vector3 toEnd = s.end - muzzlePos;
            float dist = toEnd.magnitude;
            if (dist > 1f) SpawnTracer(muzzlePos, toEnd / dist, dist);

            Burst(flashFx, muzzlePos, s.muzzle.forward, 2);
            flashLight.transform.position = muzzlePos;
            flashLight.enabled = true;
            flashTimer = 0.05f;
        }

        if (!s.hit) return;

        bool damaging = s.target != null && s.shooter != null && s.target.team != s.shooter.team;

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

    void OnKilled(Health victim, Health killer, bool headshot)
    {
        Burst(bloodFx, victim.transform.position + Vector3.up * 0.4f, Vector3.up, headshot ? 55 : 40);
        SpawnFloorBlood(victim.transform.position, 1.1f);

        feed.Add(new FeedEntry
        {
            killer = killer != null ? killer.name : "World",
            killerTeam = killer != null ? killer.team : -1,
            victim = victim.name,
            victimTeam = victim.team,
            headshot = headshot,
            expire = Time.time + 6f
        });
        if (feed.Count > 5) feed.RemoveAt(0);

        if (killer != null && killer.GetComponent<PlayerController>() != null)
        {
            hitMarkerUntil = Time.time + 0.35f;
            hitMarkerColor = new Color(1f, 0.25f, 0.25f);
        }
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

        float y = 70f;
        float icon = 22f;

        foreach (FeedEntry e in feed)
        {
            if (Time.time > e.expire) continue;

            GUIContent a = new GUIContent(e.killer);
            GUIContent m = new GUIContent("  >  ");
            GUIContent b = new GUIContent(e.victim);
            Vector2 sa = feedStyle.CalcSize(a);
            Vector2 sm = feedStyle.CalcSize(m);
            Vector2 sb = feedStyle.CalcSize(b);
            float iconW = e.headshot ? icon + 6f : 0f;
            float x = Screen.width - 20f - (sa.x + sm.x + iconW + sb.x);

            GUI.contentColor = TeamColor(e.killerTeam);
            GUI.Label(new Rect(x, y, sa.x, sa.y), a, feedStyle);
            GUI.contentColor = Color.white;
            GUI.Label(new Rect(x + sa.x, y, sm.x, sm.y), m, feedStyle);

            if (e.headshot && headIcon != null)
                GUI.DrawTexture(new Rect(x + sa.x + sm.x, y + (sa.y - icon) * 0.5f, icon, icon), headIcon);

            GUI.contentColor = TeamColor(e.victimTeam);
            GUI.Label(new Rect(x + sa.x + sm.x + iconW, y, sb.x, sb.y), b, feedStyle);

            y += sa.y + 2f;
        }
        GUI.contentColor = Color.white;
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