using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Visual presentation for grenades (3D projectile models, HE blast, Flash screen blind,
// volumetric Smoke cloud + screen haze, Fire spread flames + lights + char decals, Decoy flashes).
// Never created or enabled in batch mode / -sim runs.
public class GrenadeVisuals : MonoBehaviour
{
    static GrenadeVisuals instance;

    class LightPulse
    {
        public Light light;
        public float until;
        public float duration;
        public float maxIntensity;
    }

    class SmokeVisual
    {
        public GrenadeSystem.SmokeCloud cloud;
        public GameObject root;
        public ParticleSystem ps;
        public float nextEmit;
    }

    class FireVisual
    {
        public GrenadeSystem.FireZone zone;
        public GameObject root;
        public ParticleSystem flamePs;
        public ParticleSystem smokePs;
        public Light light;
        public float nextEmit;
        public readonly List<SpriteRenderer> scorches = new List<SpriteRenderer>();
    }

    class DecalVisual
    {
        public SpriteRenderer sr;
        public float expire;
        public float baseAlpha;
    }

    readonly List<LightPulse> lights = new List<LightPulse>();
    readonly Dictionary<int, SmokeVisual> smokes = new Dictionary<int, SmokeVisual>();
    readonly Dictionary<int, FireVisual> fires = new Dictionary<int, FireVisual>();
    readonly List<DecalVisual> decals = new List<DecalVisual>();
    readonly List<GameObject> tempVisuals = new List<GameObject>();

    ParticleSystem heFirePs, heSmokePs, heSparkPs, steamPs, decoySparkPs;
    Material softMat, flameMat, smokeMat;
    Sprite scorchSprite, charSprite;

    Health playerHealth;
    Spectator playerSpectator;
    Camera playerCam;

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

    static void TryCreate(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return;
        if (Application.isBatchMode || SimulationFlag()) return;
        if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

        RoundManager rm = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.GetComponentInChildren<GrenadeVisuals>(true) != null) return;
            if (rm == null) rm = root.GetComponentInChildren<RoundManager>(true);
        }
        if (rm != null && rm.simulation) return;

        GameObject go = new GameObject("GrenadeVisuals");
        SceneManager.MoveGameObjectToScene(go, scene);
        go.AddComponent<GrenadeVisuals>();
    }

    void Awake()
    {
        RoundManager rm = FindAnyObjectByType<RoundManager>();
        if ((rm != null && rm.simulation) || SimulationFlag() || Application.isBatchMode)
        {
            enabled = false;
            return;
        }
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        BuildAssets();
    }

    void OnEnable()
    {
        if (instance != this) return;
        GrenadeSystem.AnyThrown += OnThrown;
        GrenadeSystem.AnyHEExploded += OnHEExploded;
        GrenadeSystem.AnyFlashExploded += OnFlashExploded;
        GrenadeSystem.AnySmokeStarted += OnSmokeStarted;
        GrenadeSystem.AnySmokeEnded += OnSmokeEnded;
        GrenadeSystem.AnyFireStarted += OnFireStarted;
        GrenadeSystem.AnyFireNodesChanged += OnFireNodesChanged;
        GrenadeSystem.AnyFireExtinguished += OnFireExtinguished;
        GrenadeSystem.AnyFireEnded += OnFireEnded;
        GrenadeSystem.AnyDecoyShot += OnDecoyShot;
        GrenadeSystem.AnyDecoyEnded += OnDecoyEnded;
        GrenadeSystem.AnyCleared += ClearAll;
    }

    void OnDisable()
    {
        GrenadeSystem.AnyThrown -= OnThrown;
        GrenadeSystem.AnyHEExploded -= OnHEExploded;
        GrenadeSystem.AnyFlashExploded -= OnFlashExploded;
        GrenadeSystem.AnySmokeStarted -= OnSmokeStarted;
        GrenadeSystem.AnySmokeEnded -= OnSmokeEnded;
        GrenadeSystem.AnyFireStarted -= OnFireStarted;
        GrenadeSystem.AnyFireNodesChanged -= OnFireNodesChanged;
        GrenadeSystem.AnyFireExtinguished -= OnFireExtinguished;
        GrenadeSystem.AnyFireEnded -= OnFireEnded;
        GrenadeSystem.AnyDecoyShot -= OnDecoyShot;
        GrenadeSystem.AnyDecoyEnded -= OnDecoyEnded;
        GrenadeSystem.AnyCleared -= ClearAll;
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
        ClearAll();
    }

    void BuildAssets()
    {
        Shader particleShader = Shader.Find("Sprites/Default");
        softMat = new Material(particleShader);
        softMat.mainTexture = MakeSoftCircle(64, 0.1f);

        smokeMat = new Material(particleShader);
        smokeMat.mainTexture = MakeCloudPuff(96);

        flameMat = new Material(particleShader);
        flameMat.mainTexture = MakeSoftCircle(64, 0.25f);

        scorchSprite = MakeSprite(MakeScorch(64, new Color(0.06f, 0.055f, 0.05f, 0.85f)));
        charSprite = MakeSprite(MakeScorch(64, new Color(0.08f, 0.04f, 0.02f, 0.65f)));

        heFirePs = MakeSystem("HE_Fire", flameMat, new Color(1f, 0.62f, 0.18f, 0.95f), 1.6f, 3.2f, 0.35f, 2f, 7f, -0.3f, 180f);
        heSmokePs = MakeSystem("HE_Smoke", smokeMat, new Color(0.22f, 0.21f, 0.20f, 0.72f), 1.8f, 3.6f, 1.8f, 1f, 3.8f, -0.15f, 180f);
        heSparkPs = MakeSystem("HE_Sparks", softMat, new Color(1f, 0.86f, 0.45f, 1f), 0.08f, 0.16f, 0.5f, 5f, 13f, 1.5f, 180f);
        steamPs = MakeSystem("Fire_Steam", smokeMat, new Color(0.88f, 0.90f, 0.92f, 0.65f), 0.9f, 1.8f, 1.1f, 0.6f, 1.8f, -0.25f, 60f);
        decoySparkPs = MakeSystem("Decoy_Sparks", softMat, new Color(1f, 0.85f, 0.35f, 1f), 0.04f, 0.09f, 0.18f, 2f, 5f, 0.6f, 180f);
    }

    ParticleSystem MakeSystem(string name, Material mat, Color col, float sizeMin, float sizeMax, float life,
                              float speedMin, float speedMax, float gravity, float coneAngle)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);
        ParticleSystem ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = true;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = new ParticleSystem.MinMaxCurve(life * 0.75f, life * 1.15f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
        main.startSize = new ParticleSystem.MinMaxCurve(sizeMin, sizeMax);
        main.startColor = col;
        main.gravityModifier = gravity;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        main.maxParticles = 600;

        var emission = ps.emission;
        emission.enabled = false;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.25f;

        var fade = ps.colorOverLifetime;
        fade.enabled = true;
        Gradient g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0.85f, 0.65f), new GradientAlphaKey(0f, 1f) });
        fade.color = g;

        ParticleSystemRenderer pr = go.GetComponent<ParticleSystemRenderer>();
        pr.sharedMaterial = mat;
        ps.Play();
        return ps;
    }

    // ---------------- events ----------------

    void OnThrown(GrenadeProjectile proj)
    {
        if (proj == null || proj.Data == null) return;
        GameObject prefab = proj.Data.worldPrefab;
        if (prefab == null) prefab = Resources.Load<GameObject>("GrenadeModels/" + proj.Data.grenadeName);
        if (prefab == null) return;

        GameObject model = ViewModel.SpawnModel(prefab, proj.transform, true);
        model.transform.localPosition = Vector3.zero;
        model.transform.localScale = Vector3.one * 1.35f;
        foreach (Transform t in model.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = 0;

        if (proj.Data.type == GrenadeType.Fire && proj.Data.team == 0)
        {
            Transform wick = ViewModel.FindChild(model.transform, "Wick");
            if (wick != null)
            {
                GameObject glow = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                DestroyImmediate(glow.GetComponent<Collider>());
                glow.transform.SetParent(wick, false);
                glow.transform.localPosition = Vector3.zero;
                glow.transform.localScale = Vector3.one * 0.03f;
                glow.GetComponent<MeshRenderer>().sharedMaterial = flameMat;
            }
        }
    }

    void OnHEExploded(Vector3 pos, float radius, GrenadeData data)
    {
        EmitBurst(heFirePs, pos + Vector3.up * 0.25f, 28);
        EmitBurst(heSmokePs, pos + Vector3.up * 0.45f, 24);
        EmitBurst(heSparkPs, pos + Vector3.up * 0.2f, 36);
        AddLight(pos + Vector3.up * 0.5f, new Color(1f, 0.65f, 0.2f), 14f, 7f, 0.22f);

        if (Physics.Raycast(pos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 2.5f, GrenadeSystem.WorldMask, QueryTriggerInteraction.Ignore))
            AddDecal(scorchSprite, hit.point, hit.normal, 2.8f, 0.85f, 24f);
    }

    void OnFlashExploded(Vector3 pos, GrenadeData data, Health localAffected, float localStrength)
    {
        AddLight(pos + Vector3.up * 0.25f, Color.white, 22f, 11f, 0.18f);
        EmitBurst(heSparkPs, pos + Vector3.up * 0.2f, 16);
    }

    void OnSmokeStarted(GrenadeSystem.SmokeCloud cloud)
    {
        if (cloud == null) return;
        GameObject root = new GameObject("SmokeVisual_" + cloud.id);
        root.transform.position = cloud.center;

        ParticleSystem ps = MakeSystem("SmokeCloudPs_" + cloud.id, smokeMat, new Color(0.76f, 0.78f, 0.80f, 0.96f),
                                       2.6f, 3.8f, 2.6f, 0.05f, 0.25f, -0.02f, 180f);
        ps.transform.SetParent(root.transform, false);
        var main = ps.main;
        main.maxParticles = 260;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = cloud.maxRadius * 0.62f;

        // Initial dense bloom burst so the cloud immediately blocks view
        ps.Emit(70);

        smokes[cloud.id] = new SmokeVisual
        {
            cloud = cloud,
            root = root,
            ps = ps,
            nextEmit = Time.time + 0.08f
        };
    }

    void OnSmokeEnded(int id)
    {
        if (!smokes.TryGetValue(id, out SmokeVisual sv)) return;
        smokes.Remove(id);
        if (sv.root != null) Destroy(sv.root);
    }

    void OnFireStarted(GrenadeSystem.FireZone zone)
    {
        if (zone == null) return;
        GameObject root = new GameObject("FireVisual_" + zone.id);
        root.transform.position = zone.origin;

        ParticleSystem fPs = MakeSystem("FireFlames_" + zone.id, flameMat, new Color(1f, 0.56f, 0.14f, 0.92f),
                                        0.7f, 1.35f, 0.65f, 0.6f, 1.9f, -0.35f, 40f);
        fPs.transform.SetParent(root.transform, false);
        var fMain = fPs.main;
        fMain.maxParticles = 320;
        var fFade = fPs.colorOverLifetime;
        Gradient fg = new Gradient();
        fg.SetKeys(
            new[] { new GradientColorKey(new Color(1f, 0.92f, 0.45f), 0f), new GradientColorKey(new Color(1f, 0.35f, 0.05f), 0.55f), new GradientColorKey(new Color(0.35f, 0.08f, 0.02f), 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.95f, 0.15f), new GradientAlphaKey(0f, 1f) });
        fFade.color = fg;

        ParticleSystem sPs = MakeSystem("FireSmoke_" + zone.id, smokeMat, new Color(0.14f, 0.13f, 0.13f, 0.42f),
                                        0.9f, 1.8f, 1.4f, 0.7f, 1.5f, -0.25f, 45f);
        sPs.transform.SetParent(root.transform, false);
        var sMain = sPs.main;
        sMain.maxParticles = 160;

        GameObject lgo = new GameObject("FireLight");
        lgo.transform.SetParent(root.transform, false);
        lgo.transform.localPosition = Vector3.up * 0.9f;
        Light lt = lgo.AddComponent<Light>();
        lt.type = LightType.Point;
        lt.color = new Color(1f, 0.52f, 0.15f);
        lt.range = 9f;
        lt.intensity = 4.5f;
        lt.shadows = LightShadows.None;

        FireVisual fv = new FireVisual
        {
            zone = zone,
            root = root,
            flamePs = fPs,
            smokePs = sPs,
            light = lt,
            nextEmit = Time.time,
        };
        fires[zone.id] = fv;
        SyncFireScorches(fv);
    }

    void OnFireNodesChanged(GrenadeSystem.FireZone zone)
    {
        if (zone != null && fires.TryGetValue(zone.id, out FireVisual fv))
            SyncFireScorches(fv);
    }

    void SyncFireScorches(FireVisual fv)
    {
        if (fv == null || fv.zone == null || fv.root == null) return;
        while (fv.scorches.Count < fv.zone.nodes.Count)
        {
            GameObject go = new GameObject("FireScorch");
            go.transform.SetParent(fv.root.transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = charSprite;
            sr.color = new Color(1f, 0.45f, 0.12f, 0.65f);
            fv.scorches.Add(sr);
        }
        for (int i = 0; i < fv.scorches.Count; i++)
        {
            bool active = i < fv.zone.nodes.Count;
            fv.scorches[i].enabled = active;
            if (active)
            {
                Vector3 p = fv.zone.nodes[i].position + Vector3.up * 0.02f;
                fv.scorches[i].transform.position = p;
                fv.scorches[i].transform.rotation = Quaternion.Euler(90f, (i * 47) % 360, 0f);
                fv.scorches[i].transform.localScale = Vector3.one * (fv.zone.nodes[i].radius * 1.9f);
            }
        }
    }

    void OnFireExtinguished(Vector3 pos)
    {
        EmitBurst(steamPs, pos + Vector3.up * 0.4f, 22);
    }

    void OnFireEnded(int id)
    {
        if (!fires.TryGetValue(id, out FireVisual fv)) return;
        fires.Remove(id);
        if (fv.root != null) Destroy(fv.root);
    }

    void OnDecoyShot(Vector3 pos, WeaponData mimicWeapon, Health owner)
    {
        EmitBurst(decoySparkPs, pos + Vector3.up * 0.08f, 6);
        AddLight(pos + Vector3.up * 0.15f, new Color(1f, 0.8f, 0.4f), 4f, 3f, 0.06f);
    }

    void OnDecoyEnded(Vector3 pos)
    {
        EmitBurst(heSmokePs, pos + Vector3.up * 0.1f, 6);
        EmitBurst(decoySparkPs, pos + Vector3.up * 0.1f, 10);
    }

    public void ClearAll()
    {
        foreach (SmokeVisual sv in smokes.Values)
            if (sv.root != null) Destroy(sv.root);
        smokes.Clear();

        foreach (FireVisual fv in fires.Values)
            if (fv.root != null) Destroy(fv.root);
        fires.Clear();

        for (int i = 0; i < lights.Count; i++)
            if (lights[i].light != null) Destroy(lights[i].light.gameObject);
        lights.Clear();

        for (int i = 0; i < decals.Count; i++)
            if (decals[i].sr != null) Destroy(decals[i].sr.gameObject);
        decals.Clear();

        for (int i = 0; i < tempVisuals.Count; i++)
            if (tempVisuals[i] != null) Destroy(tempVisuals[i]);
        tempVisuals.Clear();
    }

    // ---------------- per-frame update & overlays ----------------

    void Update()
    {
        float now = Time.time;

        for (int i = lights.Count - 1; i >= 0; i--)
        {
            LightPulse lp = lights[i];
            if (lp.light == null || now >= lp.until)
            {
                if (lp.light != null) Destroy(lp.light.gameObject);
                lights.RemoveAt(i);
                continue;
            }
            float t = (lp.until - now) / Mathf.Max(0.01f, lp.duration);
            lp.light.intensity = lp.maxIntensity * t * t;
        }

        foreach (SmokeVisual sv in smokes.Values)
        {
            if (sv.cloud == null || sv.ps == null) continue;
            float r = sv.cloud.CurrentRadius;
            var shape = sv.ps.shape;
            shape.radius = Mathf.Max(0.3f, r * 0.62f);

            if (now >= sv.nextEmit && sv.cloud.Remaining > 1.2f)
            {
                sv.nextEmit = now + 0.08f;
                sv.ps.Emit(5);
            }
        }

        foreach (FireVisual fv in fires.Values)
        {
            if (fv.zone == null || fv.flamePs == null) continue;
            if (fv.light != null)
            {
                float flicker = 0.8f + 0.35f * Mathf.Sin(now * 23f + fv.zone.id) * Mathf.Cos(now * 13f);
                fv.light.intensity = 4.2f * flicker;
            }
            if (now < fv.nextEmit || fv.zone.nodes.Count == 0) continue;
            fv.nextEmit = now + 0.045f;

            // Emit flames distributed across active BFS nodes
            int count = Mathf.Min(10, Mathf.Max(2, fv.zone.nodes.Count));
            for (int k = 0; k < count; k++)
            {
                GrenadeSystem.FireNode node = fv.zone.nodes[Random.Range(0, fv.zone.nodes.Count)];
                Vector2 off = Random.insideUnitCircle * (node.radius * 0.72f);
                Vector3 emitPos = node.position + new Vector3(off.x, 0.08f, off.y);
                fv.flamePs.Emit(new ParticleSystem.EmitParams { position = emitPos }, 1);
                if (Random.value < 0.25f)
                    fv.smokePs.Emit(new ParticleSystem.EmitParams { position = emitPos + Vector3.up * 0.35f }, 1);
            }
        }

        for (int i = decals.Count - 1; i >= 0; i--)
        {
            DecalVisual d = decals[i];
            if (d.sr == null || now >= d.expire)
            {
                if (d.sr != null) Destroy(d.sr.gameObject);
                decals.RemoveAt(i);
                continue;
            }
            float left = d.expire - now;
            if (left < 3f)
            {
                Color c = d.sr.color;
                c.a = d.baseAlpha * Mathf.Clamp01(left / 3f);
                d.sr.color = c;
            }
        }
    }

    void OnGUI()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (Time.timeScale <= 0f || MainMenu.IsOpen) return;

        if (playerHealth == null)
        {
            PlayerController pc = FindAnyObjectByType<PlayerController>();
            if (pc != null)
            {
                playerHealth = pc.GetComponent<Health>();
                playerSpectator = pc.GetComponent<Spectator>();
                if (pc.cameraTransform != null) playerCam = pc.cameraTransform.GetComponent<Camera>();
            }
        }
        if (playerCam == null) playerCam = Camera.main;

        // Flashbang blind screen overlay (supports both player and spectated teammate)
        Health viewed = playerHealth;
        if (playerHealth != null && playerHealth.IsDead && playerSpectator != null && playerSpectator.Watched != null)
            viewed = playerSpectator.Watched;

        float blind = GrenadeSystem.GetBlindness(viewed);
        float smoke = playerCam != null ? GrenadeSystem.GetSmokeDensityAt(playerCam.transform.position) : 0f;
        if (blind <= 0.005f && smoke <= 0.005f) return;

        GUI.depth = -15;
        if (smoke > 0.005f)
        {
            GUI.color = new Color(0.68f, 0.70f, 0.72f, Mathf.Clamp01(smoke * 0.96f));
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        }
        if (blind > 0.005f)
        {
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(blind));
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        }
        GUI.color = Color.white;
    }

    // ---------------- helpers ----------------

    static void EmitBurst(ParticleSystem ps, Vector3 pos, int count)
    {
        if (ps == null) return;
        ps.transform.position = pos;
        ps.Emit(count);
    }

    void AddLight(Vector3 pos, Color color, float range, float intensity, float duration)
    {
        GameObject go = new GameObject("GrenadeLight");
        go.transform.SetParent(transform, false);
        go.transform.position = pos;
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = color;
        l.range = range;
        l.intensity = intensity;
        l.shadows = LightShadows.None;
        lights.Add(new LightPulse { light = l, until = Time.time + duration, duration = duration, maxIntensity = intensity });
    }

    void AddDecal(Sprite sprite, Vector3 point, Vector3 normal, float size, float alpha, float lifetime)
    {
        if (sprite == null) return;
        GameObject go = new GameObject("GrenadeScorch");
        go.transform.SetParent(transform, false);
        go.transform.position = point + normal * 0.018f;
        go.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        go.transform.localScale = Vector3.one * size;
        SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.color = new Color(1f, 1f, 1f, alpha);
        decals.Add(new DecalVisual { sr = sr, expire = Time.time + lifetime, baseAlpha = alpha });
    }

    static Texture2D MakeSoftCircle(int size, float hardness)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = size * 0.5f;
        Vector2 c = new Vector2(r - 0.5f, r - 0.5f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / r;
                float a = Mathf.Clamp01((1f - d) / Mathf.Max(0.01f, 1f - hardness));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
            }
        tex.Apply();
        return tex;
    }

    static Texture2D MakeCloudPuff(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = size * 0.5f;
        Vector2 c = new Vector2(r - 0.5f, r - 0.5f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / r;
                float n = Mathf.PerlinNoise(x * 0.08f + 3.1f, y * 0.08f + 7.4f);
                float edge = Mathf.Clamp01(1f - d);
                float a = Mathf.Clamp01(edge * (0.75f + 0.45f * n));
                a = a * a * (3f - 2f * a);
                float shade = 0.86f + 0.14f * n;
                tex.SetPixel(x, y, new Color(shade, shade, shade, a));
            }
        tex.Apply();
        return tex;
    }

    static Texture2D MakeScorch(int size, Color tint)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        float r = size * 0.5f;
        Vector2 c = new Vector2(r - 0.5f, r - 0.5f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / r;
                float n = Mathf.PerlinNoise(x * 0.14f + 1.2f, y * 0.14f + 5.7f);
                float a = Mathf.Clamp01((1f - d) * 1.4f) * Mathf.Lerp(0.45f, 1f, n);
                tex.SetPixel(x, y, new Color(tint.r, tint.g, tint.b, tint.a * a * a));
            }
        tex.Apply();
        return tex;
    }

    static Sprite MakeSprite(Texture2D tex)
    {
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
    }
}
