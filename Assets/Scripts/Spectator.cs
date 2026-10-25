using System.Collections.Generic;
using UnityEngine;

// The dead player watches a living teammate (LMB / RMB switch to the next one) from the teammate's eyes: the camera
// goes lower when it crouches, and looks where it aims in a fight (a bot's head is turned to its enemy), else where
// its body faces. CharacterBody hides the watched model from this camera (LocalBody layer).
// The first person weapon (ViewModel) is bound to the teammate's Weapon: its draws, shots, reloads and knife are shown
// with hands; while a bot looks through a sniper scope the gun is hidden, the view zooms and the scope is drawn here.
// When the player lives again (or nobody is left to watch) the camera and the view go back to the player.
public class Spectator : MonoBehaviour
{
    public Transform cam;
    public GameObject crosshair;
    [Tooltip("how fast the view turns to where the watched bot aims, 1/s")] public float aimFollow = 18f;

    Health self;
    Transform originalParent;
    Vector3 originalPos;
    Quaternion originalRot;
    bool spectating;
    Health watched;
    BotController watchedBot;
    Weapon watchedWeapon;
    ViewModel view;
    Camera viewCam;
    Quaternion viewRot;
    bool viewSet;
    bool scoped;
    Texture2D scopeTexture;
    GUIStyle style;

    void Start()
    {
        self = GetComponent<Health>();
        if (cam == null && Camera.main != null) cam = Camera.main.transform;
        if (cam == null) return;

        originalParent = cam.parent;
        originalPos = cam.localPosition;
        originalRot = cam.localRotation;
        viewCam = cam.GetComponent<Camera>();
    }

    void Update()
    {
        if (self == null || cam == null) return;

        if (!self.IsDead)
        {
            if (spectating) StopSpectating();
            return;
        }

        List<Health> mates = AliveTeammates();
        if (mates.Count == 0)
        {
            if (spectating) StopSpectating();
            return;
        }

        if (!spectating || watched == null || watched.IsDead || !watched.gameObject.activeInHierarchy)
            Watch(mates[0]);
        BindView();

        // the eyes go down when the watched bot crouches (like the player's own crouch camera, 0.6 -> 0.05)
        float eyes = 0.6f;
        if (watchedBot != null) eyes -= watchedBot.crouchEyeDrop * watchedBot.CrouchAmount;
        cam.localPosition = new Vector3(0f, eyes, 0.2f);

        // clicks on the pause menu buttons (and the click that takes the cursor back) do not switch the watched teammate,
        // nor clicks while the developer console is open
        if (MainMenu.IsOpen || Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked || DevConsole.IsOpen) return;
        if (Time.frameCount - MainMenu.LastClosedFrame <= 1) return;

        if (Input.GetMouseButtonDown(0)) Cycle(mates, 1);
        if (Input.GetMouseButtonDown(1)) Cycle(mates, -1);
    }

    // after the bots have aimed and moved this frame: the view turns with the watched bot's aim, the scope zooms
    void LateUpdate()
    {
        if (!spectating || watched == null || cam == null) return;

        // in a fight at the enemy the bot sees (its chest), just after one along the bot's head, else along its body
        Quaternion look = watched.transform.rotation;
        Vector3 f = Vector3.zero;
        if (watchedBot != null && watchedBot.Target != null && !watchedBot.Target.IsDead)
            f = watchedBot.Target.transform.position + Vector3.up * 0.25f - cam.position;
        else if (watchedBot != null && watchedBot.head != null && watchedWeapon != null && watchedWeapon.IsFiring)
            f = watchedBot.head.forward;
        if (f.sqrMagnitude > 1e-4f && Mathf.Abs(f.normalized.y) < 0.99f) look = Quaternion.LookRotation(f, Vector3.up);
        // its own smoothed rotation, so the steps of the bot's turn (in FixedUpdate) do not jerk the view
        viewRot = viewSet ? Quaternion.Slerp(viewRot, look, 1f - Mathf.Exp(-aimFollow * Time.deltaTime)) : look;
        viewSet = true;
        cam.rotation = viewRot;

        // PlayerShoot sets the normal field of view every frame; while the bot is in its scope it is the scope's
        bool wasScoped = scoped;
        scoped = ViewModel.InScope(watchedBot);
        if (viewCam != null)
        {
            if (scoped) viewCam.fieldOfView = ScopeFov();
            else if (wasScoped) viewCam.fieldOfView = NormalFov();
        }
    }

    List<Health> AliveTeammates()
    {
        List<Health> list = new List<Health>();
        foreach (Health h in Health.All)
            if (h != self && h.team == self.team && !h.IsDead && h.gameObject.activeInHierarchy)
                list.Add(h);
        return list;
    }

    void Cycle(List<Health> mates, int dir)
    {
        int i = mates.IndexOf(watched);
        if (i < 0) i = 0;
        i = (i + dir + mates.Count) % mates.Count;
        Watch(mates[i]);
    }

    void Watch(Health h)
    {
        watched = h;
        watchedBot = h.GetComponent<BotController>();
        watchedWeapon = watchedBot != null && watchedBot.weapon != null ? watchedBot.weapon : h.GetComponentInChildren<Weapon>();
        cam.SetParent(h.transform, false);
        cam.localPosition = new Vector3(0f, 0.6f, 0.2f);
        cam.localRotation = Quaternion.identity;
        viewSet = false;
        spectating = true;
        EndScope();
        BindView();

        if (crosshair != null) crosshair.SetActive(false);
    }

    void StopSpectating()
    {
        cam.SetParent(originalParent, false);
        cam.localPosition = originalPos;
        cam.localRotation = originalRot;
        spectating = false;
        watched = null;
        watchedBot = null;
        watchedWeapon = null;
        EndScope();

        // the hands hold the player's own weapon again
        ViewModel vm = View();
        if (vm != null) vm.BindHome();

        if (crosshair != null) crosshair.SetActive(true);
    }

    // the first person view on the camera shows the watched teammate's weapon
    void BindView()
    {
        if (watched == null || ViewModel.IsFirstPerson(watched)) return;
        ViewModel vm = View();
        if (vm != null) vm.Bind(watchedWeapon, watched, watched.GetComponent<PlayerShoot>());
    }

    // PlayerShoot adds the view model in its Start, maybe after this Start
    ViewModel View()
    {
        if (view == null && cam != null) view = cam.GetComponent<ViewModel>();
        return view;
    }

    void EndScope()
    {
        if (scoped && viewCam != null) viewCam.fieldOfView = NormalFov();
        scoped = false;
    }

    static float NormalFov()
    {
        return Mathf.Clamp(GameSettings.VerticalFov, 20f, 140f);
    }

    // the first zoom of the watched weapon, the same ratio to the normal view as the player's own scope
    float ScopeFov()
    {
        float normal = NormalFov();
        WeaponData d = watchedWeapon != null ? watchedWeapon.data : null;
        if (d == null || !d.HasScope) return normal;
        float defaultFov = Camera.HorizontalToVerticalFieldOfView(GameSettings.DefaultFov, 16f / 9f);
        return Mathf.Clamp(d.scopeFov[0] * normal / defaultFov, 2f, normal);
    }

    // bottom centre, between the health and ammo boxes of GameHud (they show the watched teammate)
    GUIStyle hintStyle;
    Health titleFor;
    string titleText = "";
    const string HintText = "ЛКМ / ПКМ — сменить игрока";

    void OnGUI()
    {
        if (!spectating || watched == null) return;
        if (Time.timeScale == 0f) return;   // a menu is open
        if (Event.current.type != EventType.Repaint) return;

        if (scoped) DrawScope();

        float k = GameHud.Scale;
        if (style == null)
        {
            style = GameHud.MakeStyle(FontStyle.Bold);
            hintStyle = GameHud.MakeStyle(FontStyle.Normal);
        }
        style.fontSize = Mathf.Max(6, Mathf.RoundToInt(26f * k));
        hintStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(17f * k));

        if (titleFor != watched)
        {
            titleFor = watched;
            titleText = "Наблюдение: " + watched.name;
        }

        Vector2 title = GameHud.Measure(style, titleText);
        Vector2 hint = GameHud.Measure(hintStyle, HintText);
        float titleH = title.y;
        float hintH = hint.y;
        float w = Mathf.Max(title.x, hint.x) + 48f * k;
        float h = titleH + hintH + 16f * k;
        Rect r = new Rect(Mathf.Round((Screen.width - w) * 0.5f), Mathf.Round(Screen.height - GameHud.Margin * k - h),
            Mathf.Round(w), Mathf.Round(h));

        GameHud.Fill(r, GameHud.PanelColor);
        Color team = watched.team == 0 ? new Color(1f, 0.35f, 0.35f) : new Color(0.4f, 0.6f, 1f);
        GameHud.Fill(new Rect(r.x, r.y, r.width, Mathf.Max(2f, Mathf.Round(3f * k))), team);

        GameHud.Text(new Rect(r.x, r.y + 8f * k, r.width, titleH), titleText, style, Color.white, TextAnchor.MiddleCenter);
        GameHud.Text(new Rect(r.x, r.y + 8f * k + titleH, r.width, hintH), HintText, hintStyle, GameHud.DimText, TextAnchor.MiddleCenter);
    }

    // the sniper scope of the watched bot: black around a clear circle, thin black cross lines
    void DrawScope()
    {
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
        GUI.DrawTexture(new Rect(cx - half, cy - line * 0.5f, half * 2f, line), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(cx - line * 0.5f, cy - half, line, half * 2f), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    // black with a clear circle in the middle (about 82% of the screen height), like the player's scope
    static Texture2D MakeScopeTexture()
    {
        const int n = 512;
        Texture2D tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        Color[] px = new Color[n * n];
        Vector2 c = new Vector2(n - 1, n - 1) * 0.5f;
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), c) / (n * 0.5f);
                float t = Mathf.Clamp01((d - 0.8f) / 0.04f);
                px[y * n + x] = new Color(0f, 0f, 0f, t * t * (3f - 2f * t));
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        return tex;
    }

    void OnDestroy()
    {
        if (scopeTexture != null) Destroy(scopeTexture);
    }
}
