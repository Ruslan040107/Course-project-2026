using System.Collections.Generic;
using UnityEngine;

// The weapon in the player's hands (first person view).
// The model comes from Resources/WeaponModels (or WeaponData.viewModelPrefab); without one it is
// built at runtime from cubes and cylinders. Gloves and sleeves are boxes on movable arms.
// All animations are procedural: draw (every weapon has its own, like in CS), sway, bob, recoil,
// reload (the left hand takes the magazine out and in, then pulls the handle or the slide),
// AWP bolt, pistol slide, knife slash and stab, muzzle flash and ejected shells.
// PlayerShoot adds this component to the player camera by itself.
public class ViewModel : MonoBehaviour
{
    [Header("Placement (camera space)")]
    public Vector3 hipPosition = new Vector3(0.13f, -0.125f, 0.275f);
    [Tooltip("pistols are held a bit higher, so both hands are on screen")]
    public Vector3 pistolOffset = new Vector3(-0.01f, 0.025f, 0.015f);
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

    // sounds that belong to the player's first person animations (draw, reload):
    // "bolt_back", "bolt_forward", "slide_back", "slide_forward", "mag_out", "mag_in", "knife_flip".
    // GameAudio plays them without position. The weapon is the one in the hands.
    public static event System.Action<string, WeaponData> AnimationSound;
    static void EmitSound(string key, WeaponData weapon) { AnimationSound?.Invoke(key, weapon); }

    static ViewModel active;
    static Mesh cubeMesh, cylinderMesh, quadMesh;

    // draw timeline (parts of WeaponData.drawTime): the weapon comes up, then the handle is pulled and released
    const float DrawRaise = 0.38f, DrawPull = 0.52f, DrawRelease = 0.66f;
    // reload timeline (parts of the reload time)
    const float MagOutAt = 0.18f, MagInAt = 0.6f, PullAt = 0.8f, ReleaseAt = 0.88f;
    // AWP bolt after a shot (parts of the time between shots)
    const float BoltBackAt = 0.3f, BoltForwardAt = 0.5f;

    // the magazine drop on reload, in the weapon root's space
    static readonly Vector3 MagDrop = new Vector3(0f, -0.3f, 0.02f);
    // the reload pose: the weapon turns its magazine towards the camera
    static readonly Vector3 ReloadPos = new Vector3(-0.03f, 0.005f, 0.005f);
    static readonly Vector3 ReloadRot = new Vector3(-8f, -15f, -28f);
    // the knife blade points forward and up
    static readonly Vector3 KnifeTilt = new Vector3(-35f, -10f, -25f);
    // where the elbows are, from the palms (weapon root space)
    static readonly Vector3 RightElbow = new Vector3(0.07f, -0.17f, -0.30f);
    static readonly Vector3 LeftElbow = new Vector3(-0.16f, -0.2f, -0.22f);
    static readonly Vector3 LeftElbowPistol = new Vector3(-0.1f, -0.2f, -0.22f);

    // how the hands work a weapon on draw and reload
    enum Rack
    {
        SideHandle,   // AK-47: the charging handle on the right side of the receiver
        RearHandle,   // M4A4, MP9: the charging handle at the rear top
        Bolt,         // AWP: the right hand works the bolt
        Slide,        // pistols: the left hand racks the slide
        Flip          // knife: a twirl on draw
    }

    // a sleeve with a glove at its end; the root sits at the elbow and looks at the palm
    class Arm
    {
        public Transform root;
        public Vector3 palm, elbow;   // rest pose, in the weapon root's space
        public float length;
    }

    class Model
    {
        public GameObject root;
        public Transform muzzle, eject, mag, slide;
        public Vector3 magRest, slideRest;
        public Vector3 magDrop = new Vector3(0f, -0.3f, 0.02f);   // where the magazine goes on reload, in its parent's space
        public Vector3 slideBack = Vector3.back;                  // the weapon's "back" and barrel axis, in the slide's parent space
        public Vector3 slideAxis = Vector3.forward;
        public Quaternion slideRestRot = Quaternion.identity;
        public float slideTravel;
        public bool boltAction;

        // hands; all points are in the weapon root's space
        public Rack rack;
        public Arm leftArm, rightArm;
        public Vector3 rightPalm, leftPalm;   // where the hands hold the weapon
        public Vector3 handle;                // where a hand grabs the charging handle, the bolt or the slide
        public Vector3 magGrab;               // the palm under the magazine
        public float pull;                    // how far the hand pulls the handle back
        public Vector3 restOffset;            // camera space

        // the knife model that twirls on draw
        public Transform spin;
        public Vector3 spinAxis, spinRestPos;
        public Quaternion spinRestRot;
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

    // sounds already played by the current draw, reload and bolt cycle (one bit per sound)
    int drawCues, reloadCues = ~0, boltCues = ~0;

    // the pose built this frame
    Vector3 posePos, poseRot;          // weapon offset from the camera
    Vector3 leftHand, rightHand;       // where the palms go
    Vector3 leftElbowLift;             // the left elbow goes up to reach over the receiver
    float handleBack;                  // handle, slide or bolt pulled back (0..1)
    float boltLift;                    // AWP bolt handle lifted (0..1)
    float magOut;                      // magazine dropped (0..1)
    float spinAngle;                   // knife twirl

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
        Weapon.AnyEquipped += OnEquipped;
        if (weapon != null) weapon.ReloadStarted += OnReloadStarted;
    }

    void OnDestroy()
    {
        Weapon.AnyShot -= OnShot;
        Weapon.AnyEquipped -= OnEquipped;
        if (weapon != null) weapon.ReloadStarted -= OnReloadStarted;
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
            boltCues = 0;
        }
        else
        {
            EjectShell();
        }
    }

    // the same weapon taken again (a new round) is drawn again too
    void OnEquipped(Weapon w)
    {
        if (w == weapon && pivot != null) SwitchTo(w.data);
    }

    void OnReloadStarted()
    {
        reloadCues = 0;
    }

    // ---------------- every frame ----------------

    void LateUpdate()
    {
        if (pivot == null) return;

        float dt = Time.deltaTime;
        UpdateShells(dt);

        if (weapon != null && weapon.data != currentData) SwitchTo(weapon.data);

        // sounds go on even when the model is hidden (the AWP bolt in the scope)
        bool alive = owner != null && !owner.IsDead;
        if (alive && current != null && weapon != null) PlayCues();

        bool show = alive && current != null && !(shoot != null && shoot.IsScoped);
        if (pivot.gameObject.activeSelf != show) pivot.gameObject.SetActive(show);
        if (!show) return;

        posePos = hipPosition + current.restOffset;
        poseRot = Vector3.zero;
        leftHand = current.leftPalm;
        rightHand = current.rightPalm;
        leftElbowLift = Vector3.zero;
        handleBack = 0f;
        boltLift = 0f;
        magOut = 0f;
        spinAngle = 0f;

        AnimateDraw((Time.time - drawStart) / drawTime);
        Sway(dt);

        // recoil kick, recovers quickly
        float k = 1f - Mathf.Exp(-kickRecover * dt);
        kickPos = Vector3.Lerp(kickPos, Vector3.zero, k);
        kickRot = Vector3.Lerp(kickRot, Vector3.zero, k);
        posePos += kickPos;
        poseRot += kickRot;
        slideKick = Mathf.MoveTowards(slideKick, 0f, dt * 12f);
        handleBack = Mathf.Max(handleBack, slideKick);

        if (weapon.IsReloading && !weapon.IsMelee) AnimateReload(weapon.ReloadProgress);

        float bt = (Time.time - boltStart) / boltTime;
        if (current.boltAction && bt >= 0f && bt < 1f) AnimateBolt(bt);

        if (weapon.IsMelee) AnimateMelee();

        ApplyPose();

        bool flashOn = Time.time < flashUntil;
        if (flash.gameObject.activeSelf != flashOn) flash.gameObject.SetActive(flashOn);
    }

    // sway: the weapon lags behind the mouse; bob while walking, breathing while standing, lag while in the air
    void Sway(float dt)
    {
        Vector2 mouse = Cursor.lockState == CursorLockMode.Locked
            ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"))
            : Vector2.zero;
        Vector2 swayTarget = Vector2.ClampMagnitude(new Vector2(-mouse.x, mouse.y) * swayAmount, swayMax);
        sway = Vector2.Lerp(sway, swayTarget, 1f - Mathf.Exp(-swaySmooth * dt));
        poseRot += new Vector3(sway.y, sway.x, sway.x * 0.6f);
        posePos += new Vector3(sway.x, -sway.y, 0f) * 0.002f;

        Vector3 vel = controller != null ? controller.velocity : Vector3.zero;
        bool grounded = controller == null || controller.isGrounded;
        float speed = new Vector2(vel.x, vel.z).magnitude;
        bobBlend = Mathf.MoveTowards(bobBlend, grounded ? Mathf.Clamp01(speed / 5f) : 0f, 6f * dt);
        bobTimer += dt * bobFrequency * bobBlend;
        posePos += new Vector3(Mathf.Cos(bobTimer) * bobAmount, Mathf.Sin(bobTimer * 2f) * bobAmount * 0.6f, 0f) * bobBlend;
        poseRot.z += Mathf.Cos(bobTimer) * 1.2f * bobBlend;
        posePos.y += Mathf.Sin(Time.time * 1.6f) * 0.0015f;

        float airTarget = grounded ? 0f : Mathf.Clamp(-vel.y * 0.006f, -0.03f, 0.03f);
        airOffset = Mathf.Lerp(airOffset, airTarget, 1f - Mathf.Exp(-8f * dt));
        posePos.y += airOffset;
    }

    // draw: the weapon comes up from below, then the hands work it like in CS
    void AnimateDraw(float u)
    {
        float raise = Ease(Mathf.Clamp01(u / DrawRaise));
        posePos += new Vector3(0f, -0.22f, -0.05f) * (1f - raise);
        poseRot += new Vector3(45f, -10f, 0f) * (1f - raise);
        if (u >= 1f) return;

        switch (current.rack)
        {
            case Rack.Flip:
                // knife: one full turn around the blade while it comes up
                spinAngle = 360f * Ease(Mathf.Clamp01(u / 0.85f));
                break;

            case Rack.Bolt:
                // AWP: the right hand lifts the bolt, pulls it back, pushes it forward and lowers it
                WorkHandle(Window(u, 0.25f, 0.4f, 0.85f, 1f), Window(u, 0.33f, 0.43f, 0.84f, 0.97f),
                           Window(u, DrawPull, 0.6f, DrawRelease, 0.74f), 0f, Window(u, 0.42f, 0.5f, 0.76f, 0.84f));
                break;

            default:
                // rifles, MP9, pistols: the left hand goes to the handle (slide), pulls it back and lets it go;
                // the handle snaps forward, the hand follows a moment later
                WorkHandle(Window(u, 0.3f, 0.45f, 0.85f, 1f), Window(u, 0.35f, 0.5f, 0.75f, 0.95f),
                           Window(u, DrawPull, DrawPull + 0.1f, DrawRelease, DrawRelease + 0.04f),
                           Window(u, DrawPull, DrawPull + 0.1f, DrawRelease + 0.06f, 0.9f), 0f);
                break;
        }
    }

    // reload: the weapon turns its magazine to the camera, the left hand takes the magazine out and puts it back,
    // then the handle (slide, bolt) is pulled
    void AnimateReload(float p)
    {
        float tilt = Window(p, 0f, 0.15f, 0.85f, 1f);
        posePos += ReloadPos * tilt;
        poseRot += ReloadRot * tilt;

        magOut = Window(p, 0.15f, 0.35f, 0.55f, 0.75f);

        // the hand holds the magazine while it drops out and comes back;
        // a model without a separate magazine does not drop it, so the hand only dips
        float onMag = Window(p, 0.04f, 0.15f, 0.72f, 0.8f);
        float follow = current.mag != null ? 1f : 0.25f;
        leftHand = Vector3.Lerp(leftHand, current.magGrab + MagDrop * magOut * follow, onMag);

        // the slide or the charging handle is pulled at the end; the AWP bolt is lifted first, by the right hand
        float back = Window(p, 0.78f, 0.86f, 0.88f, 0.95f);
        if (current.rack == Rack.Bolt)
            WorkHandle(Window(p, 0.66f, 0.76f, 0.92f, 1f), Window(p, 0.66f, 0.74f, 0.95f, 1f), back, 0f,
                       Window(p, 0.74f, 0.78f, 0.95f, 0.98f));
        else
            WorkHandle(Window(p, 0.7f, 0.8f, 0.92f, 1f), Window(p, 0.72f, 0.8f, 0.92f, 1f), back,
                       Window(p, 0.78f, 0.86f, 0.9f, 0.97f), 0f);
    }

    // The hands work the handle. turn: the weapon is turned to show the handle; reach: a hand is on it;
    // back: the handle (slide, bolt) is pulled back; handBack: the hand is pulled back (0 = same as the handle);
    // lift: the AWP bolt handle is lifted.
    void WorkHandle(float turn, float reach, float back, float handBack, float lift)
    {
        Model m = current;
        switch (m.rack)
        {
            case Rack.SideHandle:   // rolled to show the right side
                posePos += new Vector3(-0.02f, 0.018f, 0f) * turn;
                poseRot += new Vector3(-6f, -4f, 30f) * turn;
                break;
            case Rack.RearHandle:   // the rear of the receiver comes up a bit
                posePos += new Vector3(-0.01f, 0.01f, -0.01f) * turn;
                poseRot += new Vector3(-8f, -4f, 10f) * turn;
                break;
            case Rack.Bolt:
                posePos += new Vector3(0f, 0.005f, 0f) * turn;
                poseRot += new Vector3(-3f, 4f, 14f) * turn;
                break;
            case Rack.Slide:
                posePos += new Vector3(-0.015f, 0.01f, 0f) * turn;
                poseRot += new Vector3(-6f, -8f, 15f) * turn;
                break;
        }

        handleBack = Mathf.Max(handleBack, back);
        if (handBack <= 0f) handBack = back;
        Vector3 onHandle = m.handle + Vector3.back * m.pull * handBack;

        if (m.rack == Rack.Bolt)
        {
            boltLift = Mathf.Max(boltLift, lift);
            rightHand = Vector3.Lerp(rightHand, onHandle + new Vector3(-0.03f, 0.045f, 0f) * lift, reach);
            return;
        }

        leftHand = Vector3.Lerp(leftHand, onHandle, reach);
        if (m.rack == Rack.SideHandle) leftElbowLift += new Vector3(0f, 0.1f, 0f) * reach;   // the arm goes over the receiver

        // a pistol model without a separate slide: the whole gun jerks back a little
        if (m.rack == Rack.Slide && m.slide == null)
        {
            posePos += new Vector3(0f, 0.003f, -0.008f) * back;
            poseRot += new Vector3(-3f, 0f, 0f) * back;
        }
    }

    // AWP after a shot: the right hand lifts the bolt handle, pulls it back, pushes it forward, lowers it
    void AnimateBolt(float bt)
    {
        float lift = Window(bt, 0.15f, 0.3f, 0.65f, 0.8f);
        float back = Window(bt, 0.3f, 0.42f, 0.5f, 0.65f);
        float reach = Window(bt, 0f, 0.15f, 0.8f, 0.95f);
        poseRot += new Vector3(-3f, 4f, 10f) * lift;
        posePos += new Vector3(0f, -0.015f, 0f) * lift;

        boltLift = Mathf.Max(boltLift, lift);
        handleBack = back;
        Vector3 onHandle = current.handle + Vector3.back * current.pull * back + new Vector3(-0.03f, 0.045f, 0f) * lift;
        rightHand = Vector3.Lerp(rightHand, onHandle, reach);

        if (!boltEjected && bt > 0.42f)
        {
            boltEjected = true;
            EjectShell();
        }
    }

    // knife: slash with the left button, stab with the right one
    void AnimateMelee()
    {
        float mt = Time.time - meleeStart;
        if (meleeStab)
        {
            const float dur = 0.5f;
            if (mt < dur)
            {
                float f = Window(mt / dur, 0f, 0.25f, 0.45f, 1f);
                posePos += new Vector3(-0.06f, 0.02f, 0.14f) * f;
                poseRot += new Vector3(-8f, -15f, 0f) * f;
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
                poseRot += new Vector3(-10f * swing, side * swing, -40f * swing);
                posePos += new Vector3(-0.06f, 0.02f, 0.05f) * swing;
            }
        }
    }

    // moves the parts of the model to the pose built this frame
    void ApplyPose()
    {
        Model m = current;

        if (m.mag != null)
        {
            m.mag.localPosition = m.magRest + m.magDrop * magOut;
            bool magVisible = magOut < 0.95f;
            if (m.mag.gameObject.activeSelf != magVisible) m.mag.gameObject.SetActive(magVisible);
        }

        if (m.slide != null)
        {
            m.slide.localPosition = m.slideRest + m.slideBack * m.slideTravel * handleBack;
            m.slide.localRotation = Quaternion.AngleAxis(70f * boltLift, m.slideAxis) * m.slideRestRot;
        }

        if (m.spin != null)
        {
            Quaternion q = Quaternion.AngleAxis(spinAngle, m.spinAxis);
            m.spin.localPosition = q * m.spinRestPos;
            m.spin.localRotation = q * m.spinRestRot;
        }

        if (m.leftArm != null) Reach(m.leftArm, leftHand, leftElbowLift);
        if (m.rightArm != null) Reach(m.rightArm, rightHand, Vector3.zero);

        pivot.localPosition = posePos;
        pivot.localRotation = Quaternion.Euler(poseRot);
    }

    // sounds of the first person animations, each one once per animation
    void PlayCues()
    {
        Model m = current;
        string back = m.rack == Rack.Slide ? "slide_back" : "bolt_back";
        string forward = m.rack == Rack.Slide ? "slide_forward" : "bolt_forward";

        float u = (Time.time - drawStart) / drawTime;
        if (u < 1.2f)
        {
            if (m.rack == Rack.Flip)
            {
                Cue(ref drawCues, 1, true, "knife_flip");
            }
            else
            {
                Cue(ref drawCues, 1, u >= DrawPull, back);
                if (Cue(ref drawCues, 2, u >= DrawRelease, forward)) Jerk();
            }
        }

        if (weapon.IsReloading && !weapon.IsMelee)
        {
            float p = weapon.ReloadProgress;
            Cue(ref reloadCues, 1, p >= MagOutAt, "mag_out");
            Cue(ref reloadCues, 2, p >= MagInAt, "mag_in");
            Cue(ref reloadCues, 4, p >= PullAt, back);
            if (Cue(ref reloadCues, 8, p >= ReleaseAt, forward)) Jerk();
        }

        float bt = (Time.time - boltStart) / boltTime;
        if (m.boltAction && bt >= 0f && bt < 1f)
        {
            Cue(ref boltCues, 1, bt >= BoltBackAt, "bolt_back");
            Cue(ref boltCues, 2, bt >= BoltForwardAt, "bolt_forward");
        }
    }

    // plays a sound once: the bit marks it as played
    bool Cue(ref int played, int bit, bool due, string key)
    {
        if (!due || (played & bit) != 0) return false;
        played |= bit;
        EmitSound(key, currentData);
        return true;
    }

    // a small jerk when the handle or the slide is released
    void Jerk()
    {
        kickPos += new Vector3(0f, 0.003f, -0.006f);
        kickRot += new Vector3(-2.5f, 0f, 2f);
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

        // a new draw; the sounds of the old weapon's reload and bolt never come
        drawCues = 0;
        reloadCues = ~0;
        boltCues = ~0;
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
        m.rack = RackFor(d);
        if (!d.melee && d.category == WeaponCategory.Pistol) m.restOffset = pistolOffset;

        GameObject prefab = ModelFor(d);
        if (prefab != null) UsePrefab(m, r, d, prefab);
        else
        {
            if (d.melee) BuildKnife(m, r);
            else if (d.category == WeaponCategory.Sniper || n.Contains("awp")) BuildAwp(m, r);
            else if (n.Contains("ak")) BuildAk(m, r);
            else if (d.category == WeaponCategory.Rifle) BuildM4(m, r);
            else if (d.category == WeaponCategory.SMG) BuildMp9(m, r);
            else BuildPistol(m, r, n);
            BuildHands(m, r, d);
        }

        if (m.muzzle == null) m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0f, 0.3f));
        if (m.mag != null)
        {
            m.magRest = m.mag.localPosition;
            // the drop is "down" in the weapon's space, whatever the scale of an imported model is
            m.magDrop = m.mag.parent.InverseTransformVector(r.TransformVector(MagDrop));
        }
        if (m.slide != null)
        {
            m.slideRest = m.slide.localPosition;
            m.slideRestRot = m.slide.localRotation;
            // the same for the slide: "back" and the bolt axis of the weapon in its parent's space
            m.slideBack = m.slide.parent.InverseTransformVector(r.TransformVector(Vector3.back));
            m.slideAxis = m.slide.parent.InverseTransformDirection(r.TransformDirection(Vector3.forward));
        }
        if (m.spin != null)
        {
            m.spinRestPos = m.spin.localPosition;
            m.spinRestRot = m.spin.localRotation;
            m.spinAxis = Quaternion.Euler(KnifeTilt) * Vector3.forward;   // along the blade
        }
        return m;
    }

    // the draw and reload style: by the weapon's name, else by its category
    static Rack RackFor(WeaponData d)
    {
        switch (d.weaponName)
        {
            case "AK-47": return Rack.SideHandle;
            case "M4A4":
            case "MP9": return Rack.RearHandle;
            case "AWP": return Rack.Bolt;
            case "Glock-18":
            case "USP-S":
            case "Desert Eagle": return Rack.Slide;
            case "Knife": return Rack.Flip;
        }
        if (d.melee || d.category == WeaponCategory.Melee) return Rack.Flip;
        if (d.category == WeaponCategory.Sniper) return Rack.Bolt;
        if (d.category == WeaponCategory.Pistol) return Rack.Slide;
        return Rack.RearHandle;
    }

    // your own model (WeaponData.viewModelPrefab). Its children are found by name:
    // Muzzle (flash, tracers), Eject (shells), Mag (moves on reload), Slide or Bolt (moves on a shot),
    // Hands (if there is none, simple hands are added)
    // the model for a weapon: WeaponData.viewModelPrefab, or Resources/WeaponModels/<weapon name>
    public static GameObject ModelFor(WeaponData d)
    {
        if (d == null) return null;
        if (d.viewModelPrefab != null) return d.viewModelPrefab;
        return Resources.Load<GameObject>("WeaponModels/" + d.weaponName);
    }

    // Makes a copy of a weapon model under the parent, without colliders and shadows.
    // If the model has the empty objects Grip, Up and Muzzle (made in Blender), it is turned
    // so that the grip is at the parent's origin, the barrel looks along +Z and the top is +Y.
    public static GameObject SpawnModel(GameObject prefab, Transform parent, bool shadows)
    {
        GameObject go = Instantiate(prefab, parent, false);
        go.name = prefab.name;

        // colliders go at once, so they never block a shot
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true)) DestroyImmediate(c);
        foreach (Renderer rd in go.GetComponentsInChildren<Renderer>(true))
        {
            rd.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
            rd.receiveShadows = shadows;

            Material[] mats = rd.sharedMaterials;
            for (int i = 0; i < mats.Length; i++) mats[i] = Opaque(mats[i]);
            rd.sharedMaterials = mats;
        }

        Transform t = go.transform;
        Transform grip = FindChild(t, "Grip");
        Transform up = FindChild(t, "Up");
        Transform muzzle = FindChild(t, "Muzzle");
        if (grip != null && up != null && muzzle != null)
        {
            Vector3 g = parent.InverseTransformPoint(grip.position);
            Vector3 u = parent.InverseTransformPoint(up.position);
            Vector3 f = parent.InverseTransformPoint(muzzle.position);
            // the barrel is above the grip: only the part of grip->muzzle across the Up direction is "forward"
            Vector3 upDir = (u - g).normalized;
            Vector3 fwd = Vector3.ProjectOnPlane(f - g, upDir);
            Quaternion q = Quaternion.LookRotation(fwd, upDir);
            t.localRotation = Quaternion.Inverse(q) * t.localRotation;
            t.localPosition -= parent.InverseTransformPoint(grip.position);
        }
        return go;
    }

    static readonly Dictionary<Material, Material> opaqueCopies = new Dictionary<Material, Material>();

    // a model material imported as transparent (alpha 0 in the file) would be invisible: use an opaque copy
    static Material Opaque(Material m)
    {
        if (m == null || !m.HasProperty("_Surface") || m.GetFloat("_Surface") < 0.5f) return m;
        if (opaqueCopies.TryGetValue(m, out Material copy) && copy != null) return copy;

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        copy = new Material(lit != null ? lit : m.shader) { name = m.name + " (opaque)" };
        Color c = m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor") : m.color;
        c.a = 1f;
        copy.SetColor("_BaseColor", c);
        if (m.HasProperty("_BaseMap")) copy.SetTexture("_BaseMap", m.GetTexture("_BaseMap"));
        copy.SetFloat("_Smoothness", m.HasProperty("_Smoothness") ? Mathf.Min(m.GetFloat("_Smoothness"), 0.5f) : 0.3f);
        opaqueCopies[m] = copy;
        return copy;
    }

    void UsePrefab(Model m, Transform r, WeaponData d, GameObject prefab)
    {
        GameObject go = SpawnModel(prefab, r, false);
        Transform t = go.transform;

        m.muzzle = FindChild(t, "Muzzle");
        m.eject = FindChild(t, "Eject");
        m.mag = FindChild(t, "Mag");
        m.slide = FindChild(t, "Slide");
        if (m.slide == null) m.slide = FindChild(t, "Bolt");
        m.boltAction = d.category == WeaponCategory.Sniper;
        m.slideTravel = m.boltAction ? 0.07f : d.category == WeaponCategory.Pistol ? 0.035f : 0.05f;

        if (d.melee)
        {
            // the blade points forward and up, like the knife made of shapes
            Quaternion tilt = Quaternion.Euler(KnifeTilt);
            t.localPosition = tilt * t.localPosition;
            t.localRotation = tilt * t.localRotation;
            m.spin = t;
        }
        else if (m.muzzle != null)
        {
            // the barrel at the same height as on the models made of shapes
            Vector3 mz = r.InverseTransformPoint(m.muzzle.position);
            t.localPosition += new Vector3(0f, 0.03f - mz.y, 0f);
        }

        if (FindChild(t, "Hands") == null)
        {
            ModelGrips(m, r, d, FindChild(t, "Grip"));
            BuildHands(m, r, d);
        }
    }

    // Where the hands hold an imported model. The Grip, the Muzzle and the Mag or Slide parts are found in the model,
    // the rest was measured on the models in Resources/WeaponModels: x right, y up, z forward, meters from the Grip.
    void ModelGrips(Model m, Transform r, WeaponData d, Transform grip)
    {
        Vector3 g = grip != null ? r.InverseTransformPoint(grip.position) : Vector3.zero;
        m.rightPalm = g;
        if (m.rack == Rack.Flip) return;

        Vector3 mz = m.muzzle != null ? r.InverseTransformPoint(m.muzzle.position) : g + new Vector3(0f, 0.08f, 0.5f);
        float high = mz.y - g.y;       // the barrel above the grip
        float length = mz.z - g.z;     // the muzzle in front of the grip

        Vector3 right = new Vector3(0.004f, 0.008f, 0f);
        Vector3 left, handle, mag;
        float pull;
        switch (d.weaponName)
        {
            case "AK-47":
                left = new Vector3(-0.006f, 0.05f, 0.33f);
                handle = new Vector3(0.035f, 0.115f, 0.17f);
                mag = new Vector3(0f, -0.145f, 0.235f);
                pull = 0.07f;
                break;
            case "M4A4":
                left = new Vector3(-0.006f, 0.04f, 0.31f);
                handle = new Vector3(0f, 0.13f, -0.005f);
                mag = new Vector3(0f, -0.155f, 0.175f);
                pull = 0.05f;
                break;
            case "MP9":
                left = new Vector3(-0.004f, 0.025f, 0.19f);   // on the vertical front grip
                handle = new Vector3(0f, 0.13f, -0.03f);
                mag = new Vector3(0f, -0.095f, 0.08f);
                pull = 0.04f;
                break;
            case "AWP":
                right = new Vector3(0.004f, 0.02f, -0.065f);   // the grip of the stock is behind the trigger
                left = new Vector3(-0.006f, 0.025f, 0.3f);
                handle = new Vector3(0.035f, 0.095f, -0.01f);   // the bolt handle
                mag = new Vector3(0f, 0f, 0.06f);
                pull = 0.07f;
                break;
            default:
                if (m.rack == Rack.Slide)
                {
                    // a pistol: the left hand holds the right one, the rear of the slide is above the grip
                    right = new Vector3(0.006f, 0.005f, -0.005f);
                    left = new Vector3(-0.03f, -0.012f, 0.012f);
                    handle = new Vector3(0f, high, -0.02f);
                    mag = new Vector3(0f, -0.075f, -0.012f);
                    pull = 0.03f;
                }
                else
                {
                    // another long gun: the left hand under the handguard, the handle on top above the grip
                    left = new Vector3(-0.006f, high * 0.45f, length * 0.5f);
                    handle = new Vector3(0f, high + 0.035f, 0f);
                    mag = new Vector3(0f, -0.15f, length * 0.28f);
                    pull = 0.05f;
                }
                break;
        }

        m.rightPalm = g + right;
        m.leftPalm = g + left;
        m.handle = g + handle;
        m.magGrab = g + mag;
        m.pull = pull;

        // separate parts tell exactly where the magazine bottom and the rear of the slide are
        if (m.mag != null && TryBoundsIn(m.mag, r, out Bounds mb))
            m.magGrab = new Vector3(mb.center.x, mb.min.y - 0.022f, mb.center.z);
        if (m.slide != null && m.rack == Rack.Slide && TryBoundsIn(m.slide, r, out Bounds sb))
        {
            m.handle = new Vector3(sb.center.x, sb.center.y, sb.min.z + 0.02f);
            m.pull = m.slideTravel;
        }
    }

    // the box around the meshes of a part, in the space of another transform
    static bool TryBoundsIn(Transform part, Transform space, out Bounds bounds)
    {
        bounds = new Bounds();
        bool any = false;
        foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
        {
            if (mf.sharedMesh == null) continue;
            Bounds mb = mf.sharedMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = mb.center + Vector3.Scale(mb.extents,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                Vector3 p = space.InverseTransformPoint(mf.transform.TransformPoint(corner));
                if (any) bounds.Encapsulate(p);
                else bounds = new Bounds(p, Vector3.zero);
                any = true;
            }
        }
        return any;
    }

    // Gloves and sleeves on arms that can move, so a hand can reach the handle, the slide or the magazine.
    // The right hand holds the grip, the left one holds the handguard (or the right hand on a pistol).
    void BuildHands(Model m, Transform r, WeaponData d)
    {
        if (m.rack == Rack.Flip)
        {
            m.rightArm = BuildArm(r, m.rightPalm, RightElbow, new Vector3(0.055f, 0.06f, 0.09f), KnifeTilt, Vector3.zero, Vector3.zero);
            return;
        }

        m.rightArm = BuildArm(r, m.rightPalm, RightElbow, new Vector3(0.05f, 0.08f, 0.075f), new Vector3(15f, 0f, 0f),
                              new Vector3(-0.026f, 0.03f, 0.012f), new Vector3(0.014f, 0.018f, 0.05f));

        if (d.category == WeaponCategory.Pistol)
            m.leftArm = BuildArm(r, m.leftPalm, LeftElbowPistol, new Vector3(0.045f, 0.065f, 0.075f), new Vector3(15f, 10f, -15f),
                                 Vector3.zero, Vector3.zero);
        else if (d.category == WeaponCategory.SMG)   // the hand around a vertical front grip
            m.leftArm = BuildArm(r, m.leftPalm, LeftElbow, new Vector3(0.05f, 0.075f, 0.055f), new Vector3(-8f, 0f, 0f),
                                 new Vector3(-0.026f, 0.03f, -0.01f), new Vector3(0.014f, 0.018f, 0.04f));
        else                                          // the palm under the handguard, the thumb along its side
            m.leftArm = BuildArm(r, m.leftPalm, LeftElbow, new Vector3(0.06f, 0.045f, 0.1f), Vector3.zero,
                                 new Vector3(-0.031f, 0.022f, 0f), new Vector3(0.016f, 0.022f, 0.07f));
    }

    // a sleeve from the elbow to the wrist and a glove (with a thumb) on the palm
    Arm BuildArm(Transform r, Vector3 palm, Vector3 elbowOffset, Vector3 handSize, Vector3 handEuler, Vector3 thumbPos, Vector3 thumbSize)
    {
        Arm a = new Arm { palm = palm, elbow = palm + elbowOffset, length = elbowOffset.magnitude };
        a.root = Point(r, "Arm", a.elbow);
        a.root.localRotation = Quaternion.LookRotation(palm - a.elbow, Vector3.up);

        // the sleeve goes on a bit behind the elbow, off the screen
        float behind = 0.12f, wrist = a.length - handSize.z * 0.45f;
        Box(a.root, new Vector3(0f, 0f, (wrist - behind) * 0.5f), new Vector3(0.07f, 0.07f, wrist + behind), Sleeve());

        // in the rest pose the hand has its own angle to the weapon
        Transform hand = Point(a.root, "Hand", new Vector3(0f, 0f, a.length));
        hand.localRotation = Quaternion.Inverse(a.root.localRotation) * Quaternion.Euler(handEuler);
        Box(hand, Vector3.zero, handSize, Glove());
        if (thumbSize != Vector3.zero) Box(hand, thumbPos, thumbSize, Glove());
        return a;
    }

    // puts the palm at a point (weapon root space): the elbow follows half way (plus a lift), the forearm keeps its length
    static void Reach(Arm a, Vector3 palm, Vector3 elbowLift)
    {
        Vector3 elbow = a.elbow + (palm - a.palm) * 0.5f + elbowLift;
        Vector3 dir = (palm - elbow).normalized;
        a.root.localPosition = palm - dir * a.length;
        a.root.localRotation = Quaternion.LookRotation(dir, Vector3.up);
    }

    public static Transform FindChild(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (string.Equals(t.name, name, System.StringComparison.OrdinalIgnoreCase)) return t;
        return null;
    }

    Material Metal() { return Mat("Metal", new Color(0.13f, 0.13f, 0.14f), 0.7f, 0.5f); }
    Material Polymer() { return Mat("Polymer", new Color(0.06f, 0.06f, 0.07f), 0f, 0.3f); }
    Material Wood() { return Mat("Wood", new Color(0.42f, 0.22f, 0.1f), 0f, 0.35f); }
    Material Silver() { return Mat("Silver", new Color(0.72f, 0.72f, 0.75f), 1f, 0.75f); }
    Material Glove() { return Mat("Glove", new Color(0.5f, 0.4f, 0.28f), 0f, 0.25f); }
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

        m.rightPalm = new Vector3(0.005f, -0.07f, -0.04f);
        m.leftPalm = new Vector3(-0.006f, -0.045f, 0.33f);
        m.handle = new Vector3(0.045f, 0.02f, 0.12f);
        m.magGrab = new Vector3(0f, -0.22f, 0.14f);
        m.pull = m.slideTravel;
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

        m.rightPalm = new Vector3(0.005f, -0.07f, -0.03f);
        m.leftPalm = new Vector3(-0.006f, -0.035f, 0.3f);
        m.handle = new Vector3(0f, 0.06f, -0.1f);
        m.magGrab = new Vector3(0f, -0.19f, 0.1f);
        m.pull = m.slideTravel;
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

        m.rightPalm = new Vector3(0.005f, -0.07f, -0.1f);
        m.leftPalm = new Vector3(-0.006f, -0.05f, 0.3f);
        m.handle = new Vector3(0.1f, 0.03f, -0.04f);   // the bolt knob
        m.magGrab = new Vector3(0f, -0.125f, 0.05f);
        m.pull = m.slideTravel;
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

        m.rightPalm = new Vector3(0.005f, -0.06f, -0.02f);
        m.leftPalm = new Vector3(-0.004f, -0.055f, 0.13f);   // on the front grip
        m.handle = new Vector3(0f, 0.07f, -0.08f);
        m.magGrab = new Vector3(0f, -0.195f, -0.03f);
        m.pull = m.slideTravel;
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

        m.rightPalm = new Vector3(0.006f, -0.05f, -0.015f) * s;
        m.leftPalm = new Vector3(-0.03f, -0.06f, 0f) * s;
        m.handle = new Vector3(0f, 0.04f, -0.02f) * s;   // the rear of the slide
        m.magGrab = new Vector3(0f, -0.135f, -0.02f) * s;
        m.pull = m.slideTravel;
    }

    void BuildKnife(Model m, Transform r)
    {
        Transform k = Point(r, "Knife", Vector3.zero);
        k.localRotation = Quaternion.Euler(KnifeTilt);   // the blade points forward and up

        Box(k, Vector3.zero, new Vector3(0.028f, 0.032f, 0.11f), Polymer());                    // handle
        Box(k, new Vector3(0f, 0f, 0.06f), new Vector3(0.03f, 0.05f, 0.01f), Metal());          // guard
        Box(k, new Vector3(0f, 0.004f, 0.15f), new Vector3(0.005f, 0.03f, 0.17f), Silver());    // blade
        Box(k, new Vector3(0f, 0f, 0.24f), new Vector3(0.005f, 0.02f, 0.03f), Silver(), new Vector3(35f, 0f, 0f));  // tip

        m.muzzle = Point(k, "Muzzle", new Vector3(0f, 0f, 0.25f));
        m.spin = k;
        m.rightPalm = k.localRotation * new Vector3(0f, -0.005f, -0.005f);
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
