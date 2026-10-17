using UnityEngine;

public class PlayerShoot : MonoBehaviour
{
    public Weapon weapon;
    public float zoomSpeed = 14f;
    public GameObject crosshair;   // if empty, taken from the Spectator component

    Health health;
    PlayerController pcRef;
    Camera cam;
    float baseFov = 90f;
    float currentFov = 90f;
    int desiredZoom;      // zoom level the player chose
    int zoomLevel;        // zoom level actually shown (0 while the bolt is cycling)
    float boltUntil;
    WeaponData lastData;
    Texture2D scopeTexture;

    public bool IsScoped => zoomLevel > 0;

    // mouse sensitivity multiplier while zoomed
    public float SensitivityFactor
    {
        get
        {
            float fov = ScopeFov(zoomLevel);
            return fov > 0f ? fov / baseFov : 1f;
        }
    }

    int ScopeLevels()
    {
        return (weapon != null && weapon.data != null && weapon.data.scopeFov != null) ? weapon.data.scopeFov.Length : 0;
    }

    // level 1 = first zoom; returns -1 if there is no such level
    float ScopeFov(int level)
    {
        if (level <= 0 || level > ScopeLevels()) return -1f;
        return weapon.data.scopeFov[level - 1];
    }

    public void ResetZoom()
    {
        desiredZoom = 0;
        zoomLevel = 0;
        boltUntil = 0f;
    }

    void OnEnable()
    {
        Weapon.AnyShot += OnShot;
    }

    void OnDisable()
    {
        Weapon.AnyShot -= OnShot;
    }

    void OnShot(ShotInfo s)
    {
        if (health == null || s.shooter != health || s.melee) return;

        // scoped sniper: leave the scope while the next round is loaded, then return to the same zoom
        if (desiredZoom > 0 && ScopeLevels() > 0)
            boltUntil = Time.time + 1f / Mathf.Max(weapon.fireRate, 0.01f);
    }

    void Start()
    {
        health = GetComponent<Health>();

        if (crosshair == null)
        {
            Spectator sp = GetComponent<Spectator>();
            if (sp != null) crosshair = sp.crosshair;
        }

        pcRef = GetComponent<PlayerController>();
        if (pcRef != null && pcRef.cameraTransform != null) cam = pcRef.cameraTransform.GetComponent<Camera>();

        if (cam != null)
        {
            baseFov = cam.fieldOfView;
            currentFov = baseFov;
        }
    }

    void Update()
    {
        if (weapon == null) return;

        bool dead = health != null && health.IsDead;
        int levels = ScopeLevels();

        // sniper rifles have no crosshair, only the scope
        if (!dead && crosshair != null) crosshair.SetActive(levels == 0);

        if (weapon.data != lastData)
        {
            lastData = weapon.data;
            ResetZoom();
        }

        if (dead || levels == 0 || weapon.IsReloading) ResetZoom();

        if (!dead && levels > 0 && Cursor.lockState == CursorLockMode.Locked && Input.GetMouseButtonDown(1))
            desiredZoom = (desiredZoom + 1) % (levels + 1);

        zoomLevel = Time.time < boltUntil ? 0 : desiredZoom;
        if (zoomLevel > levels) zoomLevel = 0;

        weapon.Scoped = zoomLevel > 0;
        ApplyFov();

        if (dead) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        if (Input.GetKeyDown(KeyCode.R)) weapon.StartReload();

        if (weapon.IsMelee)
        {
            if (Input.GetButton("Fire1")) weapon.TryFire();
            else if (Input.GetMouseButton(1)) weapon.TrySecondary();
            return;
        }

        bool pressed = weapon.automatic
            ? Input.GetButton("Fire1")
            : Input.GetButtonDown("Fire1");

        if (pressed) weapon.TryFire();
    }

    void ApplyFov()
    {
        if (cam == null) return;

        float fov = ScopeFov(zoomLevel);
        float target = fov > 0f ? fov : baseFov;
        float t = 1f - Mathf.Exp(-zoomSpeed * Time.deltaTime);
        currentFov = Mathf.Lerp(currentFov, target, t);
        cam.fieldOfView = currentFov;
    }

    void OnGUI()
    {
        if (zoomLevel == 0 || currentFov > baseFov - 8f) return;

        if (scopeTexture == null) scopeTexture = MakeScopeTexture();

        float size = Screen.height;
        float x = (Screen.width - size) * 0.5f;
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        float line = Mathf.Max(1f, size / 540f);
        float half = size * 0.41f;

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x, 0f, size, size), scopeTexture);

        GUI.color = Color.black;
        GUI.DrawTexture(new Rect(0f, 0f, x, Screen.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + size, 0f, x, Screen.height), Texture2D.whiteTexture);

        // moving in the scope: the crosshair gets thicker, paler and smeared
        float mi = Mathf.Clamp01(pcRef != null ? pcRef.MoveInaccuracy : 0f);
        float k = size / 1080f;
        float thick = line * (1f + 1.5f * mi);

        DrawCross(cx, cy, half, thick, 1f - 0.5f * mi);

        if (mi > 0.05f)
        {
            float dx = mi * 12f * k * Mathf.Sin(Time.time * 7f);
            float dy = mi * 9f * k * Mathf.Cos(Time.time * 9f);
            DrawCross(cx + dx, cy + dy, half, thick, 0.35f * mi);
            DrawCross(cx - dx * 0.7f, cy - dy * 0.7f, half, thick, 0.25f * mi);
        }

        GUI.color = Color.white;
    }

    void DrawCross(float cx, float cy, float half, float thick, float alpha)
    {
        GUI.color = new Color(0f, 0f, 0f, alpha);
        GUI.DrawTexture(new Rect(cx - half, cy - thick * 0.5f, half * 2f, thick), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - thick * 0.5f, cy - half, thick, half * 2f), Texture2D.whiteTexture);
    }

    // black mask with a clear circle in the middle (about 82% of the screen height)
    Texture2D MakeScopeTexture()
    {
        const int n = 512;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(n - 1, n - 1) * 0.5f;

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / (n * 0.5f);
                float t = Mathf.Clamp01((d - 0.80f) / 0.04f);
                float a = t * t * (3f - 2f * t);
                tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
        }

        tex.Apply();
        return tex;
    }
}