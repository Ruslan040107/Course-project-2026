using UnityEngine;

public class PlayerShoot : MonoBehaviour
{
    public Weapon weapon;
    public float zoomSpeed = 14f;
    // the old canvas crosshair (if empty, taken from the Spectator component); GameHud draws its own crosshair
    // and keeps this one hidden, it is shown only when GameHud does not run
    public GameObject crosshair;

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
    Texture2D dotTexture;
    Texture2D opticTexture;

    public bool IsScoped => zoomLevel > 0;

    // zoomed with an optic (SG 553, AUG): no black scope mask; ViewModel raises the gun to the eye, then OnGUI draws the optic view
    public bool IsOpticZoom => zoomLevel > 0 && weapon != null && weapon.HasOpticScope;

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
        // the asset values are for the default FOV (90 horizontal); keep the same zoom ratio at any FOV setting,
        // so a narrow FOV setting still zooms in and the scope overlay is drawn
        float defaultFov = Camera.HorizontalToVerticalFieldOfView(GameSettings.DefaultFov, 16f / 9f);
        return weapon.data.scopeFov[level - 1] * baseFov / defaultFov;
    }

    // the right mouse button zooms only if it has no other job (knife stab, burst mode switch, R8 fanning)
    bool RightClickZooms => weapon != null && !weapon.IsMelee && !weapon.HasBurstMode && !weapon.HasAltFire;

    public void ResetZoom()
    {
        desiredZoom = 0;
        zoomLevel = 0;
        boltUntil = 0f;
    }

    void OnEnable()
    {
        Weapon.AnyShot += OnShot;
        GameSettings.Changed += ApplySettingsFov;
    }

    void OnDisable()
    {
        Weapon.AnyShot -= OnShot;
        GameSettings.Changed -= ApplySettingsFov;
    }

    // the normal (not zoomed) field of view comes from the settings; the scope zooms relative to it
    void ApplySettingsFov()
    {
        baseFov = Mathf.Clamp(GameSettings.VerticalFov, 20f, 140f);
        if (cam == null) return;

        // not zoomed: change at once, so the settings screen shows it even while the game is paused
        if (zoomLevel == 0)
        {
            currentFov = baseFov;
            cam.fieldOfView = currentFov;
        }
    }

    void OnShot(ShotInfo s)
    {
        // one trigger pull counts once: the other pellets of a shotgun shell are skipped
        if (health == null || s.shooter != health || s.melee || s.pellet) return;

        // bolt-action sniper (AWP, SSG 08): leave the scope while the next round is loaded, then return to the same zoom;
        // auto-snipers (SCAR-20, G3SG1) and optics (SG 553, AUG) stay zoomed
        if (desiredZoom > 0 && ScopeLevels() > 0 && weapon.BoltAction)
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
            GameSettings.Load();   // does nothing if the settings are already loaded
            ApplySettingsFov();
            currentFov = baseFov;
            cam.fieldOfView = currentFov;

            // the weapon model in the hands
            ViewModel vm = cam.GetComponent<ViewModel>();
            if (vm == null) vm = cam.gameObject.AddComponent<ViewModel>();
            vm.Init(weapon, this, health);
        }
    }

    void Update()
    {
        if (weapon == null) return;

        bool dead = health != null && health.IsDead;
        int levels = RightClickZooms ? ScopeLevels() : 0;

        // GameHud draws the crosshair (none for sniper rifles, only the scope): the canvas image stays hidden;
        // without GameHud the old image is used as before (an optic rifle keeps it until it zooms)
        if (crosshair != null)
        {
            if (GameHud.Active)
            {
                if (crosshair.activeSelf) crosshair.SetActive(false);
            }
            else if (!dead)
            {
                crosshair.SetActive(!weapon.HasSniperScope && zoomLevel == 0);
            }
        }

        if (weapon.data != lastData)
        {
            lastData = weapon.data;
            ResetZoom();
        }

        // no scope while the weapon is being drawn or reloaded: right click does nothing then
        bool busy = weapon.IsDrawing || weapon.IsReloading;
        if (dead || levels == 0 || busy) ResetZoom();

        // the mouse buttons and keys belong to the game only while the cursor is locked and the developer console is closed
        bool locked = Cursor.lockState == CursorLockMode.Locked && !DevConsole.IsOpen;

        if (!dead && !busy && levels > 0 && locked && Input.GetMouseButtonDown(1))
            desiredZoom = (desiredZoom + 1) % (levels + 1);

        zoomLevel = Time.time < boltUntil ? 0 : desiredZoom;
        if (zoomLevel > levels) zoomLevel = 0;

        weapon.Scoped = zoomLevel > 0;
        ApplyFov();

        // R8: fanning spread for the crosshair while right click is held; letting go of the left button (or a menu taking the
        // cursor) before the hammer is fully pulled cancels the shot
        weapon.AltFireHeld = !dead && weapon.HasAltFire && !weapon.HasBurstMode && locked
            && Input.GetMouseButton(1) && !Input.GetButton("Fire1");
        if (!dead && weapon.IsHammerPulling && (!Input.GetButton("Fire1") || !locked))
            weapon.CancelHammer();

        if (dead) return;
        if (!locked) return;
        // the click that took the cursor back (or closed a menu) is not a shot
        if (Time.frameCount - MainMenu.LastClosedFrame <= 1) return;

        if (Input.GetKeyDown(KeyCode.R)) weapon.StartReload();

        if (weapon.IsMelee)
        {
            if (Input.GetButton("Fire1")) weapon.TryFire();
            else if (Input.GetMouseButton(1)) weapon.TrySecondary();
            return;
        }

        // right click: burst mode switch (Glock-18, FAMAS) or R8 fanning while it is held
        if (weapon.HasBurstMode)
        {
            if (Input.GetMouseButtonDown(1)) weapon.ToggleBurstMode();
        }
        else if (weapon.HasAltFire && Input.GetMouseButton(1) && !Input.GetButton("Fire1"))
        {
            weapon.TryAltFire();
            return;
        }

        // the R8 hammer is pulled while the button is held, then it fires (and pulls again);
        // in burst mode every press fires one burst, also with the automatic FAMAS
        bool burst = weapon.BurstMode && weapon.HasBurstMode;
        bool hold = (weapon.automatic && !burst) || weapon.fireDelay > 0f;
        bool pressed = hold
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
        if (zoomLevel == 0) return;
        if (weapon == null || weapon.IsDrawing) return;

        // optic: a round view with darkened edges (ViewModel hides the gun once it is at the eye), a reticle and a red dot
        if (IsOpticZoom)
        {
            if (currentFov < baseFov - 4f)
            {
                DrawOpticView();
                DrawOpticReticle();
                DrawOpticDot();
            }
            return;
        }

        if (currentFov > baseFov - 8f) return;

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

    // the view through an optic (SG 553, AUG): a clear circle, a dark rim and a dimmed area around it
    void DrawOpticView()
    {
        if (opticTexture == null) opticTexture = MakeOpticTexture();

        float size = Screen.height;
        float x = (Screen.width - size) * 0.5f;

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x, 0f, size, size), opticTexture);

        GUI.color = new Color(0f, 0f, 0f, OpticOutsideAlpha);
        GUI.DrawTexture(new Rect(0f, 0f, x, Screen.height), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + size, 0f, x, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    const float OpticOutsideAlpha = 0.82f;

    // the reticle of an optic (SG 553, AUG), like a small rifle scope: thin black lines through the view with a gap
    // in the middle, thick posts near the rim and a small circle around the red dot (DrawOpticDot draws the dot on top);
    // the sizes follow the screen height, like the sniper scope
    void DrawOpticReticle()
    {
        float h = Screen.height;
        float k = h / 1080f;
        float cx = Mathf.Floor(Screen.width * 0.5f);
        float cy = Mathf.Floor(h * 0.5f);

        float reach = Mathf.Round(h * 0.4f);          // into the dark rim of the optic view
        float post = Mathf.Round(reach * 0.6f);        // the thick posts from here to the rim
        float gap = Mathf.Round(30f * k);              // the thin lines start this far from the middle
        float thin = Mathf.Max(1f, Mathf.Round(1.5f * k));
        float thick = Mathf.Max(2f, Mathf.Round(5f * k));
        float ty = Mathf.Round(cy - thin * 0.5f), tx = Mathf.Round(cx - thin * 0.5f);
        float py = Mathf.Round(cy - thick * 0.5f), px = Mathf.Round(cx - thick * 0.5f);

        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        Texture2D white = Texture2D.whiteTexture;

        // thin lines: left, right, up, down
        GUI.DrawTexture(new Rect(cx - post, ty, post - gap, thin), white);
        GUI.DrawTexture(new Rect(cx + gap, ty, post - gap, thin), white);
        GUI.DrawTexture(new Rect(tx, cy - post, thin, post - gap), white);
        GUI.DrawTexture(new Rect(tx, cy + gap, thin, post - gap), white);

        // thick posts at the rim
        GUI.DrawTexture(new Rect(cx - reach, py, reach - post, thick), white);
        GUI.DrawTexture(new Rect(cx + post, py, reach - post, thick), white);
        GUI.DrawTexture(new Rect(px, cy - reach, thick, reach - post), white);
        GUI.DrawTexture(new Rect(px, cy + post, thick, reach - post), white);

        // the small circle around the dot, inside the gap of the lines
        int r = Mathf.Max(4, Mathf.RoundToInt(16f * k));
        int w = Mathf.RoundToInt(thin);
        int n = 2 * (r + w) + 2;
        if (ringTexture == null || ringSize != n || ringWidth != w)
        {
            if (ringTexture != null) Destroy(ringTexture);
            ringTexture = MakeRingTexture(n, r, w);
            ringSize = n;
            ringWidth = w;
        }
        GUI.DrawTexture(new Rect(cx - n * 0.5f, cy - n * 0.5f, n, n), ringTexture);
        GUI.color = Color.white;
    }

    Texture2D ringTexture;
    int ringSize;
    int ringWidth;

    // a white ring with soft edges (tinted by GUI.color): n x n pixels, radius r and width w in pixels
    static Texture2D MakeRingTexture(int n, float r, float w)
    {
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(n - 1, n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c);
                float a = Mathf.Clamp01(w * 0.5f + 0.5f - Mathf.Abs(d - r));
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }

        tex.Apply();
        return tex;
    }

    // clear up to 78% of the half size, a dark rim, then the dimmed outside
    Texture2D MakeOpticTexture()
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
                // a soft vignette inside the glass, a dark rim, the dimmed outside
                float inner = Mathf.Clamp01((d - 0.6f) / 0.18f) * 0.25f;
                float rim = Mathf.Clamp01(1f - Mathf.Abs(d - 0.8f) / 0.025f);
                float outside = d > 0.8f ? OpticOutsideAlpha : 0f;
                float a = Mathf.Max(Mathf.Max(inner, rim * 0.95f), outside);
                tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
            }
        }

        tex.Apply();
        return tex;
    }

    // the red dot of an optic in the middle of the screen, with a dark edge
    void DrawOpticDot()
    {
        if (dotTexture == null) dotTexture = MakeDotTexture();

        float k = Screen.height / 1080f;
        float cx = Screen.width * 0.5f;
        float cy = Screen.height * 0.5f;
        float dot = Mathf.Max(3f, Mathf.Round(6f * k));
        float edge = dot + 2f * Mathf.Max(1f, Mathf.Round(1.5f * k));

        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(cx - edge * 0.5f, cy - edge * 0.5f, edge, edge), dotTexture);
        GUI.color = new Color(1f, 0.12f, 0.08f, 0.95f);
        GUI.DrawTexture(new Rect(cx - dot * 0.5f, cy - dot * 0.5f, dot, dot), dotTexture);
        GUI.color = Color.white;
    }

    // a white disc with a soft edge (tinted by GUI.color)
    Texture2D MakeDotTexture()
    {
        const int n = 32;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(n - 1, n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / (n * 0.5f);
                float a = Mathf.Clamp01((1f - d) / 0.15f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }
        }

        tex.Apply();
        return tex;
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
