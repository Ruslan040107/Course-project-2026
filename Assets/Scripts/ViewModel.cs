using System.Collections.Generic;
using UnityEngine;

// The weapon in the player's hands (first person view).
// Every model is built at runtime from cubes and cylinders, no imported assets.
// All animations are procedural: draw, sway, bob, recoil, reload (the magazine goes out and in),
// AWP bolt, pistol slide, knife slash and stab, muzzle flash and ejected shells.
// PlayerShoot adds this component to the player camera by itself.
public class ViewModel : MonoBehaviour
{
    [Header("Placement (camera space)")]
    public Vector3 hipPosition = new Vector3(0.13f, -0.15f, 0.24f);
    public float modelScale = 0.55f;
    [Tooltip("the camera near clip plane is lowered to this, otherwise the close model is cut")]
    public float nearClip = 0.02f;

    [Header("Sway and bob")]
    public float swayAmount = 2.5f;
    public float swayMax = 6f;
    public float swaySmooth = 10f;
    public float bobFrequency = 9f;
    public float bobAmount = 0.006f;

    [Header("Recoil")]
    public float kickBack = 0.035f;
    public float kickUp = 4f;
    public float kickRecover = 14f;

    [Header("Shells")]
    public int shellPool = 16;
    public float shellLifetime = 0.8f;

    static ViewModel active;
    static Mesh cubeMesh, cylinderMesh, quadMesh;

    class Model
    {
        public GameObject root;
        public Transform muzzle, eject, mag, slide;
        public Vector3 magRest, slideRest;
        public Quaternion slideRestRot = Quaternion.identity;
        public float slideTravel;
        public bool boltAction;
    }

    class Shell
    {
        public Transform t;
        public Vector3 velocity, spin;
        public float until;
    }

    Weapon weapon;
    PlayerShoot shoot;
    Health owner;
    CharacterController controller;

    Transform pivot;
    Transform flash;
    float flashUntil;

    readonly Dictionary<WeaponData, Model> models = new Dictionary<WeaponData, Model>();
    readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    readonly List<Shell> shells = new List<Shell>();
    int nextShell;

    Model current;
    WeaponData currentData;

    float drawStart = -10f, drawTime = 0.5f;
    float boltStart = -10f, boltTime = 1f;
    bool boltEjected = true;
    float meleeStart = -10f;
    bool meleeStab;
    float slideKick;
    Vector3 kickPos, kickRot;
    Vector2 sway;
    float bobTimer, bobBlend, airOffset;

    // where the player's shots come out of the gun model; false when the model is hidden (scope, death)
    public static bool TryGetMuzzle(Health shooter, out Vector3 position)
    {
        position = Vector3.zero;
        if (active == null || active.owner != shooter || active.current == null) return false;
        if (!active.pivot.gameObject.activeInHierarchy) return false;
        position = active.current.muzzle.position;
        return true;
    }

    public void Init(Weapon w, PlayerShoot s, Health h)
    {
        if (pivot != null) return;

        weapon = w;
        shoot = s;
        owner = h;
        controller = h != null ? h.GetComponent<CharacterController>() : null;

        Camera cam = GetComponent<Camera>();
        if (cam != null) cam.nearClipPlane = Mathf.Min(cam.nearClipPlane, nearClip);

        // a camera with scale 0 still renders, but everything attached to it shrinks to a point
        Vector3 camScale = transform.localScale;
        if (Mathf.Abs(camScale.x * camScale.y * camScale.z) < 1e-6f) transform.localScale = Vector3.one;

        if (cubeMesh == null)
        {
            cubeMesh = PrimitiveMesh(PrimitiveType.Cube);
            cylinderMesh = PrimitiveMesh(PrimitiveType.Cylinder);
            quadMesh = PrimitiveMesh(PrimitiveType.Quad);
        }

        pivot = new GameObject("ViewModel").transform;
        pivot.SetParent(transform, false);
        flash = BuildFlash();

        Material brass = Mat("Brass", new Color(0.8f, 0.6f, 0.25f), 1f, 0.7f);
        for (int i = 0; i < shellPool; i++)
        {
            Transform t = Part(null, cylinderMesh, Vector3.zero, new Vector3(0.008f, 0.012f, 0.008f), brass, Vector3.zero);
            t.name = "Shell";
            t.gameObject.SetActive(false);
            shells.Add(new Shell { t = t });
        }

        active = this;
        Weapon.AnyShot += OnShot;
    }

    void OnDestroy()
    {
        Weapon.AnyShot -= OnShot;
        if (active == this) active = null;
        foreach (Shell s in shells)
            if (s.t != null) Destroy(s.t.gameObject);
    }

    // ---------------- events ----------------

    void OnShot(ShotInfo s)
    {
        if (owner == null || s.shooter != owner || current == null) return;

        if (s.melee)
        {
            meleeStart = Time.time;
            meleeStab = Input.GetMouseButton(1) && !Input.GetButton("Fire1");
            return;
        }

        float r = Mathf.Max(0.3f, weapon.recoilFactor);
        kickPos += new Vector3(0f, kickBack * 0.15f, -kickBack) * r;
        kickRot += new Vector3(-kickUp, Random.Range(-1f, 1f) * kickUp * 0.3f, Random.Range(-1f, 1f) * kickUp * 0.4f) * r;
        kickPos.z = Mathf.Max(kickPos.z, -0.09f);
        kickRot.x = Mathf.Max(kickRot.x, -16f);
        slideKick = 1f;

        flashUntil = Time.time + 0.035f;
        flash.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        flash.localScale = Vector3.one * Random.Range(0.8f, 1.3f);

        if (current.boltAction)
        {
            boltStart = Time.time;
            boltTime = 1f / Mathf.Max(weapon.fireRate, 0.01f);
            boltEjected = false;
        }
        else
        {
            EjectShell();
        }
    }

    // ---------------- every frame ----------------

    void LateUpdate()
    {
        if (pivot == null) return;

        float dt = Time.deltaTime;
        UpdateShells(dt);

        if (weapon != null && weapon.data != currentData) SwitchTo(weapon.data);

        bool show = owner != null && !owner.IsDead && current != null
                    && !(shoot != null && shoot.IsScoped);
        if (pivot.gameObject.activeSelf != show) pivot.gameObject.SetActive(show);
        if (!show) return;

        Vector3 pos = hipPosition;
        Vector3 rot = Vector3.zero;

        // draw: the weapon comes up from below
        float draw = Ease(Mathf.Clamp01((Time.time - drawStart) / drawTime));
        pos += new Vector3(0f, -0.22f, -0.05f) * (1f - draw);
        rot += new Vector3(45f, -10f, 0f) * (1f - draw);

        // sway: the weapon lags behind the mouse
        Vector2 mouse = Cursor.lockState == CursorLockMode.Locked
            ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"))
            : Vector2.zero;
        Vector2 swayTarget = Vector2.ClampMagnitude(new Vector2(-mouse.x, mouse.y) * swayAmount, swayMax);
        sway = Vector2.Lerp(sway, swayTarget, 1f - Mathf.Exp(-swaySmooth * dt));
        rot += new Vector3(sway.y, sway.x, sway.x * 0.6f);
        pos += new Vector3(sway.x, -sway.y, 0f) * 0.002f;

        // bob while walking, breathing while standing, lag while in the air
        Vector3 vel = controller != null ? controller.velocity : Vector3.zero;
        bool grounded = controller == null || controller.isGrounded;
        float speed = new Vector2(vel.x, vel.z).magnitude;
        bobBlend = Mathf.MoveTowards(bobBlend, grounded ? Mathf.Clamp01(speed / 5f) : 0f, 6f * dt);
        bobTimer += dt * bobFrequency * bobBlend;
        pos += new Vector3(Mathf.Cos(bobTimer) * bobAmount, Mathf.Sin(bobTimer * 2f) * bobAmount * 0.6f, 0f) * bobBlend;
        rot.z += Mathf.Cos(bobTimer) * 1.2f * bobBlend;
        pos.y += Mathf.Sin(Time.time * 1.6f) * 0.0015f;

        float airTarget = grounded ? 0f : Mathf.Clamp(-vel.y * 0.006f, -0.03f, 0.03f);
        airOffset = Mathf.Lerp(airOffset, airTarget, 1f - Mathf.Exp(-8f * dt));
        pos.y += airOffset;

        // recoil kick, recovers quickly
        float k = 1f - Mathf.Exp(-kickRecover * dt);
        kickPos = Vector3.Lerp(kickPos, Vector3.zero, k);
        kickRot = Vector3.Lerp(kickRot, Vector3.zero, k);
        pos += kickPos;
        rot += kickRot;
        slideKick = Mathf.MoveTowards(slideKick, 0f, dt * 12f);

        Vector3 slideOffset = Vector3.back * current.slideTravel * slideKick;
        Quaternion slideRot = Quaternion.identity;

        // reload: tilt the weapon, the magazine drops out and comes back
        bool reloading = weapon.IsReloading && !weapon.IsMelee;
        float drop = 0f;
        if (reloading)
        {
            float p = weapon.ReloadProgress;
            float tilt = Window(p, 0f, 0.15f, 0.85f, 1f);
            pos += new Vector3(-0.02f, -0.05f, 0f) * tilt;
            rot += new Vector3(-10f, 10f, 30f) * tilt;

            drop = Window(p, 0.15f, 0.35f, 0.55f, 0.75f);

            // the slide or the charging handle is pulled at the end
            float pull = Window(p, 0.78f, 0.86f, 0.88f, 0.95f);
            slideOffset += Vector3.back * current.slideTravel * pull;
        }

        if (current.mag != null)
        {
            current.mag.localPosition = current.magRest + new Vector3(0f, -0.3f, 0.02f) * drop;
            bool magVisible = drop < 0.95f;
            if (current.mag.gameObject.activeSelf != magVisible) current.mag.gameObject.SetActive(magVisible);
        }

        // AWP: lift the bolt handle, pull it back, push it forward, lower it
        float bt = (Time.time - boltStart) / boltTime;
        if (current.boltAction && bt >= 0f && bt < 1f)
        {
            float lift = Window(bt, 0.15f, 0.3f, 0.65f, 0.8f);
            float back = Window(bt, 0.3f, 0.42f, 0.5f, 0.65f);
            slideRot = Quaternion.Euler(0f, 0f, 70f * lift);
            slideOffset = Vector3.back * current.slideTravel * back;
            rot += new Vector3(-3f, 4f, 10f) * lift;
            pos += new Vector3(0f, -0.015f, 0f) * lift;

            if (!boltEjected && bt > 0.42f)
            {
                boltEjected = true;
                EjectShell();
            }
        }

        if (current.slide != null)
        {
            current.slide.localPosition = current.slideRest + slideOffset;
            current.slide.localRotation = current.slideRestRot * slideRot;
        }

        // knife: slash with the left button, stab with the right one
        if (weapon.IsMelee)
        {
            float mt = Time.time - meleeStart;
            if (meleeStab)
            {
                const float dur = 0.5f;
                if (mt < dur)
                {
                    float f = Window(mt / dur, 0f, 0.25f, 0.45f, 1f);
                    pos += new Vector3(-0.06f, 0.02f, 0.14f) * f;
                    rot += new Vector3(-8f, -15f, 0f) * f;
                }
            }
            else
            {
                const float dur = 0.35f;
                if (mt < dur)
                {
                    float u = mt / dur;
                    float swing = Mathf.Sin(u * Mathf.PI);
                    float side = Mathf.Lerp(35f, -55f, Ease(u));
                    rot += new Vector3(-10f * swing, side * swing, -40f * swing);
                    pos += new Vector3(-0.06f, 0.02f, 0.05f) * swing;
                }
            }
        }

        pivot.localPosition = pos;
        pivot.localRotation = Quaternion.Euler(rot);

        bool flashOn = Time.time < flashUntil;
        if (flash.gameObject.activeSelf != flashOn) flash.gameObject.SetActive(flashOn);
    }

    void SwitchTo(WeaponData d)
    {
        if (current != null) current.root.SetActive(false);

        currentData = d;
        current = null;
        if (d == null) return;

        if (!models.TryGetValue(d, out current))
        {
            current = Build(d);
            models[d] = current;
        }
        current.root.SetActive(true);

        flash.SetParent(current.muzzle, false);
        flash.gameObject.SetActive(false);

        drawStart = Time.time;
        drawTime = Mathf.Clamp(d.drawTime, 0.15f, 1.5f);
        boltStart = -10f;
        boltEjected = true;
        meleeStart = -10f;
        kickPos = Vector3.zero;
        kickRot = Vector3.zero;
        slideKick = 0f;
    }

    // ---------------- shells ----------------

    void EjectShell()
    {
        if (current == null || current.eject == null || shells.Count == 0) return;

        Shell s = shells[nextShell];
        nextShell = (nextShell + 1) % shells.Count;

        Transform cam = transform;
        s.t.gameObject.SetActive(true);
        s.t.position = current.eject.position;
        s.t.rotation = cam.rotation * Quaternion.Euler(0f, 0f, 90f);
        s.velocity = cam.right * Random.Range(1.2f, 1.8f) + cam.up * Random.Range(0.8f, 1.3f)
                     - cam.forward * Random.Range(0f, 0.3f)
                     + (controller != null ? controller.velocity : Vector3.zero);
        s.spin = Random.insideUnitSphere * 900f;
        s.until = Time.time + shellLifetime;
    }

    void UpdateShells(float dt)
    {
        foreach (Shell s in shells)
        {
            if (!s.t.gameObject.activeSelf) continue;
            if (Time.time > s.until)
            {
                s.t.gameObject.SetActive(false);
                continue;
            }
            s.velocity += Physics.gravity * dt;
            s.t.position += s.velocity * dt;
            s.t.Rotate(s.spin * dt, Space.Self);
        }
    }

    // ---------------- models ----------------

    Model Build(WeaponData d)
    {
        Model m = new Model();
        m.root = new GameObject(d.weaponName);
        Transform r = m.root.transform;
        r.SetParent(pivot, false);
        r.localScale = Vector3.one * modelScale;

        string n = d.weaponName.ToLowerInvariant();

        if (d.viewModelPrefab != null) UsePrefab(m, r, d);
        else if (d.melee) BuildKnife(m, r);
        else if (d.category == WeaponCategory.Sniper || n.Contains("awp")) BuildAwp(m, r);
        else if (n.Contains("ak")) BuildAk(m, r);
        else if (d.category == WeaponCategory.Rifle) BuildM4(m, r);
        else if (d.category == WeaponCategory.SMG) BuildMp9(m, r);
        else BuildPistol(m, r, n);

        if (m.muzzle == null) m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0f, 0.3f));
        if (m.mag != null) m.magRest = m.mag.localPosition;
        if (m.slide != null)
        {
            m.slideRest = m.slide.localPosition;
            m.slideRestRot = m.slide.localRotation;
        }
        return m;
    }

    // your own model (WeaponData.viewModelPrefab). Its children are found by name:
    // Muzzle (flash, tracers), Eject (shells), Mag (moves on reload), Slide or Bolt (moves on a shot),
    // Hands (if there is none, simple hands are added)
    void UsePrefab(Model m, Transform r, WeaponData d)
    {
        GameObject go = Instantiate(d.viewModelPrefab, r, false);
        go.name = d.viewModelPrefab.name;

        // colliders go at once, so they never block a shot from the camera
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
        foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true))
        {
            rd.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            rd.receiveShadows = false;
        }

        m.muzzle = FindChild(go.transform, "Muzzle");
        m.eject = FindChild(go.transform, "Eject");
        m.mag = FindChild(go.transform, "Mag");
        m.slide = FindChild(go.transform, "Slide");
        if (m.slide == null) m.slide = FindChild(go.transform, "Bolt");
        m.boltAction = d.category == WeaponCategory.Sniper;
        m.slideTravel = m.boltAction ? 0.07f : d.category == WeaponCategory.Pistol ? 0.035f : 0.05f;

        if (FindChild(go.transform, "Hands") == null && !d.melee)
        {
            if (d.category == WeaponCategory.Pistol) PistolHands(r);
            else RifleHands(r, -0.04f, 0.3f);
        }
    }

    static Transform FindChild(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    Material Metal() { return Mat("Metal", new Color(0.13f, 0.13f, 0.14f), 0.7f, 0.5f); }
    Material Polymer() { return Mat("Polymer", new Color(0.06f, 0.06f, 0.07f), 0f, 0.3f); }
    Material Wood() { return Mat("Wood", new Color(0.42f, 0.22f, 0.1f), 0f, 0.35f); }
    Material Silver() { return Mat("Silver", new Color(0.72f, 0.72f, 0.75f), 1f, 0.75f); }
    Material Glove() { return Mat("Glove", new Color(0.36f, 0.27f, 0.18f), 0f, 0.25f); }
    Material Sleeve() { return Mat("Sleeve", new Color(0.34f, 0.37f, 0.26f), 0f, 0.15f); }

    void BuildAk(Model m, Transform r)
    {
        Material metal = Metal(), wood = Wood();

        Box(r, new Vector3(0f, 0.01f, 0.06f), new Vector3(0.055f, 0.075f, 0.36f), metal);         // receiver
        Box(r, new Vector3(0f, 0.05f, 0.04f), new Vector3(0.05f, 0.012f, 0.3f), metal);           // dust cover
        Box(r, new Vector3(0f, 0f, 0.34f), new Vector3(0.062f, 0.065f, 0.2f), wood);              // lower handguard
        Box(r, new Vector3(0f, 0.05f, 0.33f), new Vector3(0.042f, 0.03f, 0.2f), wood);            // gas tube cover
        Tube(r, new Vector3(0f, 0.02f, 0.52f), 0.011f, 0.3f, metal);                              // barrel
        Box(r, new Vector3(0f, 0.06f, 0.6f), new Vector3(0.008f, 0.03f, 0.012f), metal);          // front sight
        Box(r, new Vector3(0f, 0.058f, 0.2f), new Vector3(0.03f, 0.012f, 0.04f), metal);          // rear sight
        Box(r, new Vector3(0f, -0.035f, -0.27f), new Vector3(0.045f, 0.1f, 0.26f), wood, new Vector3(-6f, 0f, 0f));  // stock
        Box(r, new Vector3(0f, -0.07f, -0.04f), new Vector3(0.035f, 0.1f, 0.045f), wood, new Vector3(18f, 0f, 0f));  // grip

        m.mag = Point(r, "Mag", new Vector3(0f, -0.03f, 0.1f));                                   // curved magazine
        Box(m.mag, new Vector3(0f, -0.05f, 0f), new Vector3(0.035f, 0.1f, 0.065f), metal, new Vector3(-10f, 0f, 0f));
        Box(m.mag, new Vector3(0f, -0.135f, 0.03f), new Vector3(0.035f, 0.08f, 0.065f), metal, new Vector3(-30f, 0f, 0f));

        m.slide = Box(r, new Vector3(0.033f, 0.02f, 0.12f), new Vector3(0.014f, 0.012f, 0.04f), metal);  // charging handle
        m.slideTravel = 0.06f;
        m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.02f, 0.68f));
        m.eject = Point(r, "Eject", new Vector3(0.03f, 0.03f, 0.08f));
        RifleHands(r, -0.04f, 0.33f);
    }

    void BuildM4(Model m, Transform r)
    {
        Material metal = Metal(), black = Polymer();

        Box(r, new Vector3(0f, 0.01f, 0.05f), new Vector3(0.05f, 0.075f, 0.32f), black);          // receiver
        Box(r, new Vector3(0f, 0.055f, 0.08f), new Vector3(0.03f, 0.018f, 0.4f), black);          // top rail
        Tube(r, new Vector3(0f, 0.01f, 0.31f), 0.03f, 0.22f, black);                              // handguard
        Tube(r, new Vector3(0f, 0.01f, 0.5f), 0.01f, 0.26f, metal);                               // barrel
        Box(r, new Vector3(0f, 0.05f, 0.44f), new Vector3(0.01f, 0.06f, 0.015f), black);          // front sight
        Box(r, new Vector3(0f, 0.075f, -0.06f), new Vector3(0.03f, 0.025f, 0.03f), black);        // rear sight
        Box(r, new Vector3(0f, 0.01f, -0.2f), new Vector3(0.03f, 0.03f, 0.2f), black);            // buffer tube
        Box(r, new Vector3(0f, -0.01f, -0.32f), new Vector3(0.045f, 0.085f, 0.14f), black);       // stock
        Box(r, new Vector3(0f, -0.07f, -0.03f), new Vector3(0.033f, 0.1f, 0.045f), black, new Vector3(18f, 0f, 0f));  // grip

        m.mag = Point(r, "Mag", new Vector3(0f, -0.03f, 0.09f));                                  // straight magazine
        Box(m.mag, new Vector3(0f, -0.07f, 0.01f), new Vector3(0.028f, 0.14f, 0.065f), metal, new Vector3(-8f, 0f, 0f));

        m.slide = Box(r, new Vector3(0f, 0.045f, -0.1f), new Vector3(0.04f, 0.01f, 0.02f), black);  // charging handle
        m.slideTravel = 0.05f;
        m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.01f, 0.64f));
        m.eject = Point(r, "Eject", new Vector3(0.03f, 0.03f, 0.06f));
        RifleHands(r, -0.03f, 0.3f);
    }

    void BuildAwp(Model m, Transform r)
    {
        Material metal = Metal(), black = Polymer();
        Material green = Mat("AwpGreen", new Color(0.22f, 0.3f, 0.18f), 0f, 0.25f);

        Box(r, new Vector3(0f, 0.005f, 0.08f), new Vector3(0.065f, 0.08f, 0.5f), green);         // body
        Box(r, new Vector3(0f, -0.03f, -0.33f), new Vector3(0.055f, 0.13f, 0.26f), green, new Vector3(-4f, 0f, 0f));  // stock
        Box(r, new Vector3(0f, 0.045f, -0.3f), new Vector3(0.045f, 0.03f, 0.2f), green);         // cheek rest
        Box(r, new Vector3(0f, -0.075f, -0.1f), new Vector3(0.035f, 0.09f, 0.05f), green, new Vector3(25f, 0f, 0f));  // grip
        Tube(r, new Vector3(0f, 0.015f, 0.55f), 0.013f, 0.5f, metal);                            // barrel
        Tube(r, new Vector3(0f, 0.015f, 0.82f), 0.018f, 0.05f, metal);                           // muzzle brake
        Tube(r, new Vector3(0f, 0.085f, 0.03f), 0.02f, 0.3f, black);                             // scope
        Tube(r, new Vector3(0f, 0.085f, 0.2f), 0.028f, 0.06f, black);
        Tube(r, new Vector3(0f, 0.085f, -0.13f), 0.025f, 0.05f, black);
        Box(r, new Vector3(0f, 0.06f, 0.1f), new Vector3(0.02f, 0.03f, 0.02f), black);           // scope mounts
        Box(r, new Vector3(0f, 0.06f, -0.06f), new Vector3(0.02f, 0.03f, 0.02f), black);

        m.mag = Point(r, "Mag", new Vector3(0f, -0.03f, 0.05f));
        Box(m.mag, new Vector3(0f, -0.04f, 0f), new Vector3(0.04f, 0.07f, 0.08f), black);

        m.slide = Point(r, "Bolt", new Vector3(0.035f, 0.03f, -0.04f));                          // bolt handle
        Box(m.slide, new Vector3(0.03f, 0f, 0f), new Vector3(0.06f, 0.012f, 0.012f), metal);
        Box(m.slide, new Vector3(0.065f, 0f, 0f), new Vector3(0.022f, 0.022f, 0.022f), black);
        m.slideTravel = 0.07f;
        m.boltAction = true;

        m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.015f, 0.85f));
        m.eject = Point(r, "Eject", new Vector3(0.035f, 0.03f, 0f));
        RifleHands(r, -0.1f, 0.3f);
    }

    void BuildMp9(Model m, Transform r)
    {
        Material metal = Metal(), black = Polymer();

        Box(r, new Vector3(0f, 0.015f, 0.04f), new Vector3(0.05f, 0.07f, 0.26f), black);         // body
        Box(r, new Vector3(0f, 0.055f, 0.04f), new Vector3(0.025f, 0.012f, 0.2f), black);        // top rail
        Tube(r, new Vector3(0f, 0.02f, 0.22f), 0.011f, 0.08f, metal);                            // barrel
        Box(r, new Vector3(0f, -0.05f, 0.13f), new Vector3(0.025f, 0.07f, 0.03f), black, new Vector3(-10f, 0f, 0f));  // front grip
        Box(r, new Vector3(0f, -0.06f, -0.02f), new Vector3(0.032f, 0.1f, 0.045f), black, new Vector3(12f, 0f, 0f));  // grip
        Box(r, new Vector3(0.03f, 0f, -0.1f), new Vector3(0.01f, 0.04f, 0.2f), black);           // folded stock

        m.mag = Point(r, "Mag", new Vector3(0f, -0.03f, -0.02f));                                // magazine inside the grip
        Box(m.mag, new Vector3(0f, -0.08f, -0.005f), new Vector3(0.026f, 0.13f, 0.035f), metal, new Vector3(12f, 0f, 0f));

        m.slide = Box(r, new Vector3(0f, 0.05f, -0.08f), new Vector3(0.03f, 0.01f, 0.015f), black);  // charging handle
        m.slideTravel = 0.04f;
        m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.02f, 0.27f));
        m.eject = Point(r, "Eject", new Vector3(0.03f, 0.03f, 0.05f));
        RifleHands(r, -0.02f, 0.13f);
    }

    void BuildPistol(Model m, Transform r, string n)
    {
        bool deagle = n.Contains("deagle") || n.Contains("desert");
        bool usp = n.Contains("usp");
        float s = deagle ? 1.25f : 1f;

        Material slideMat = deagle ? Silver() : usp ? Polymer() : Metal();
        Material frame = deagle ? Silver() : Polymer();
        Material metal = Metal();

        m.slide = Point(r, "Slide", new Vector3(0f, 0.035f, 0.06f) * s);                         // the slide with its sights
        Box(m.slide, Vector3.zero, new Vector3(0.032f, 0.034f, 0.19f) * s, slideMat);
        Box(m.slide, new Vector3(0f, 0.021f, 0.08f) * s, new Vector3(0.006f, 0.008f, 0.008f) * s, metal);
        Box(m.slide, new Vector3(0f, 0.021f, -0.08f) * s, new Vector3(0.02f, 0.008f, 0.01f) * s, metal);
        m.slideTravel = 0.035f * s;

        Box(r, new Vector3(0f, 0.008f, 0.05f) * s, new Vector3(0.03f, 0.022f, 0.16f) * s, frame);  // frame
        Box(r, new Vector3(0f, -0.045f, -0.01f) * s, new Vector3(0.031f, 0.095f, 0.048f) * s, frame, new Vector3(14f, 0f, 0f));  // grip
        Box(r, new Vector3(0f, -0.012f, 0.04f) * s, new Vector3(0.008f, 0.02f, 0.04f) * s, frame);  // trigger guard

        m.mag = Point(r, "Mag", new Vector3(0f, -0.02f, -0.01f) * s);
        Box(m.mag, new Vector3(0f, -0.05f, -0.01f) * s, new Vector3(0.026f, 0.09f, 0.038f) * s, metal, new Vector3(14f, 0f, 0f));

        if (usp)
        {
            Tube(r, new Vector3(0f, 0.035f, 0.23f), 0.017f, 0.16f, Polymer());                   // silencer
            m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.035f, 0.31f));
        }
        else
        {
            m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0.035f, 0.16f) * s);
        }
        m.eject = Point(r, "Eject", new Vector3(0.02f, 0.05f, 0.04f) * s);
        PistolHands(r);
    }

    void PistolHands(Transform r)
    {
        Material glove = Glove(), sleeve = Sleeve();
        Box(r, new Vector3(0.005f, -0.06f, -0.02f), new Vector3(0.05f, 0.07f, 0.08f), glove, new Vector3(14f, 0f, 0f));
        Box(r, new Vector3(0.03f, -0.13f, -0.19f), new Vector3(0.075f, 0.075f, 0.3f), sleeve, new Vector3(-25f, -8f, 0f));
        Box(r, new Vector3(-0.03f, -0.07f, -0.005f), new Vector3(0.05f, 0.065f, 0.08f), glove, new Vector3(15f, 0f, -20f));
        Box(r, new Vector3(-0.11f, -0.13f, -0.15f), new Vector3(0.075f, 0.075f, 0.3f), sleeve, new Vector3(-20f, 35f, 0f));
    }

    void BuildKnife(Model m, Transform r)
    {
        Transform k = Point(r, "Knife", Vector3.zero);
        k.localRotation = Quaternion.Euler(-35f, -10f, -25f);   // the blade points forward and up

        Box(k, Vector3.zero, new Vector3(0.028f, 0.032f, 0.11f), Polymer());                    // handle
        Box(k, new Vector3(0f, 0f, 0.06f), new Vector3(0.03f, 0.05f, 0.01f), Metal());          // guard
        Box(k, new Vector3(0f, 0.004f, 0.15f), new Vector3(0.005f, 0.03f, 0.17f), Silver());    // blade
        Box(k, new Vector3(0f, 0f, 0.24f), new Vector3(0.005f, 0.02f, 0.03f), Silver(), new Vector3(35f, 0f, 0f));  // tip

        Box(k, new Vector3(0f, -0.005f, -0.005f), new Vector3(0.055f, 0.06f, 0.09f), Glove());
        Box(r, new Vector3(0.03f, -0.1f, -0.17f), new Vector3(0.075f, 0.075f, 0.3f), Sleeve(), new Vector3(-30f, -8f, 0f));

        m.muzzle = Point(k, "Muzzle", new Vector3(0f, 0f, 0.25f));
    }

    void RifleHands(Transform r, float gripZ, float foreZ)
    {
        Material glove = Glove(), sleeve = Sleeve();

        // right hand on the grip, the forearm goes back towards the camera
        Box(r, new Vector3(0.005f, -0.075f, gripZ), new Vector3(0.055f, 0.07f, 0.085f), glove, new Vector3(15f, 0f, 0f));
        Box(r, new Vector3(0.03f, -0.14f, gripZ - 0.17f), new Vector3(0.075f, 0.075f, 0.3f), sleeve, new Vector3(-25f, -8f, 0f));

        // left hand under the handguard
        Box(r, new Vector3(-0.005f, -0.04f, foreZ), new Vector3(0.06f, 0.05f, 0.1f), glove);
        Box(r, new Vector3(-0.09f, -0.12f, foreZ - 0.15f), new Vector3(0.075f, 0.075f, 0.32f), sleeve, new Vector3(-20f, 30f, 0f));
    }

    // ---------------- muzzle flash ----------------

    Transform BuildFlash()
    {
        Material m = new Material(Shader.Find("Sprites/Default"));
        m.mainTexture = MakeFlashTexture(64);
        m.color = new Color(1f, 0.85f, 0.5f, 1f);

        Transform root = new GameObject("MuzzleFlash").transform;
        root.SetParent(pivot, false);

        // a star facing the camera and two long flames along the barrel
        Part(root, quadMesh, new Vector3(0f, 0f, 0.02f), new Vector3(0.14f, 0.14f, 1f), m, Vector3.zero);
        Part(root, quadMesh, new Vector3(0f, 0f, 0.08f), new Vector3(0.18f, 0.07f, 1f), m, new Vector3(0f, 90f, 0f));
        Transform side = Point(root, "Side", Vector3.zero);
        side.localRotation = Quaternion.Euler(0f, 0f, 90f);
        Part(side, quadMesh, new Vector3(0f, 0f, 0.08f), new Vector3(0.18f, 0.07f, 1f), m, new Vector3(0f, 90f, 0f));

        root.gameObject.SetActive(false);
        return root;
    }

    static Texture2D MakeFlashTexture(int size)
    {
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;

        Vector2 c = new Vector2(size * 0.5f, size * 0.5f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - c;
                float d = p.magnitude / (size * 0.5f);
                float angle = Mathf.Atan2(p.y, p.x);
                float rays = 0.45f + 0.55f * Mathf.Pow(Mathf.Abs(Mathf.Cos(angle * 3f)), 4f);
                float a = Mathf.Clamp01(1f - d / rays);
                a *= a;
                float core = Mathf.Clamp01(1f - d * 3f);
                tex.SetPixel(x, y, new Color(1f, 0.9f + 0.1f * core, 0.7f + 0.3f * core, a));
            }

        tex.Apply();
        return tex;
    }

    // ---------------- helpers ----------------

    // The primitive's mesh dies together with its object, so the object is kept: hidden, inactive,
    // without a collider. It is removed when Play mode ends, then the meshes are made again.
    static Mesh PrimitiveMesh(PrimitiveType type)
    {
        GameObject template = GameObject.CreatePrimitive(type);
        template.name = "ViewModel " + type;
        DestroyImmediate(template.GetComponent<Collider>());
        template.SetActive(false);
        template.hideFlags = HideFlags.HideInHierarchy;
        DontDestroyOnLoad(template);
        return template.GetComponent<MeshFilter>().sharedMesh;
    }

    Material Mat(string key, Color color, float metallic, float smoothness)
    {
        if (materials.TryGetValue(key, out Material m)) return m;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        m = new Material(shader);
        m.name = key;
        m.color = color;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smoothness);

        materials[key] = m;
        return m;
    }

    static Transform Point(Transform parent, string name, Vector3 pos)
    {
        Transform t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        return t;
    }

    static Transform Part(Transform parent, Mesh mesh, Vector3 pos, Vector3 scale, Material mat, Vector3 euler)
    {
        GameObject go = new GameObject("Part");
        Transform t = go.transform;
        if (parent != null) t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = Quaternion.Euler(euler);
        t.localScale = scale;

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return t;
    }

    static Transform Box(Transform parent, Vector3 pos, Vector3 size, Material mat, Vector3 euler = default)
    {
        return Part(parent, cubeMesh, pos, size, mat, euler);
    }

    // a cylinder along z (Unity's cylinder is 2 units tall along y, radius 0.5)
    static Transform Tube(Transform parent, Vector3 pos, float radius, float length, Material mat)
    {
        return Part(parent, cylinderMesh, pos, new Vector3(radius * 2f, length * 0.5f, radius * 2f), mat, new Vector3(90f, 0f, 0f));
    }

    static float Ease(float t)
    {
        return t * t * (3f - 2f * t);
    }

    // 0 before a, rises to 1 at b, stays until c, falls to 0 at d
    static float Window(float t, float a, float b, float c, float d)
    {
        float up = b > a ? Ease(Mathf.Clamp01((t - a) / (b - a))) : (t >= a ? 1f : 0f);
        float down = d > c ? Ease(Mathf.Clamp01((t - c) / (d - c))) : (t >= c ? 1f : 0f);
        return up * (1f - down);
    }
}
