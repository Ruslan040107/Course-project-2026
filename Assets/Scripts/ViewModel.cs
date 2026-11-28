using System.Collections.Generic;
using UnityEngine;

// The weapon in the player's hands (first person view).
// The model comes from Resources/WeaponModels (or WeaponData.viewModelPrefab); without one it is
// built at runtime from cubes and cylinders. Gloves and sleeves are boxes on movable arms.
// All animations are procedural:
// - draw (every weapon has its own, like in CS), sway, bob, recoil, muzzle flash and ejected shells;
// - reload: the left hand takes the magazine out and in, then pulls the handle, the slide or the pump;
//   shotguns that load shell by shell get one push of the left hand for every shell (Weapon.AnyShellInserted);
// - bolt-action rifles (WeaponData.boltAction: AWP, SSG 08) cycle the bolt after every shot;
// - pump shotguns (Nova, MAG-7) pump after every shot, the empty shell flies out when the pump goes back;
// - R8: the hammer pull turns the cylinder, right click fanning slaps the hammer, the cylinder swings out on reload;
// - Dual Berettas: a pistol in each hand, only the one that fired kicks;
// - optic zoom (SG 553, AUG): the gun comes up to the eye, then it is hidden and PlayerShoot draws the round optic view;
// - burst / auto switch (Glock-18, FAMAS): a short turn of the gun; the Zeus has no shell and a blue flash;
// - knife: slash and stab; the butterfly knife (Knife.fbx with HandleA / HandleB) flips open on draw.
// - the empty magazine falls out of the hand on reload: a real-size copy of the Mag part lands on the floor and lies
//   there a second (CombatEffects.DropPiece); shotgun hulls and the R8 cases do the same (CombatEffects.DropShell).
// PlayerShoot adds this component to the player camera by itself.
// Spectator binds it to the teammate the dead player watches (Bind), then back to the player's own weapon (BindHome):
// the draws, shots, reloads, knife and sniper scope of that teammate's Weapon are shown the same way, its sounds come
// from GameAudio (only the player's own animations send AnimationSound).
public class ViewModel : MonoBehaviour
{
    [Header("Placement (camera space)")]
    public Vector3 hipPosition = new Vector3(0.13f, -0.125f, 0.275f);
    [Tooltip("pistols are held a bit higher, so both hands are on screen")]
    public Vector3 pistolOffset = new Vector3(-0.01f, 0.025f, 0.015f);
    [Tooltip("Dual Berettas: the right pistol; the left one is dualSpread to the left of it")]
    public Vector3 dualOffset = new Vector3(-0.035f, 0.02f, 0.01f);
    public float dualSpread = 0.17f;
    public float modelScale = 0.55f;
    [Tooltip("the camera near clip plane is lowered to this, otherwise the close model is cut")]
    public float nearClip = 0.02f;

    [Header("Optic zoom (SG 553, AUG)")]
    [Tooltip("while zoomed the rear lens of the optic is this far in front of the eye, meters")]
    public float opticEye = 0.035f;
    public float opticSpeed = 8f;

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

    [Header("Butterfly knife")]
    [Tooltip("seconds between the idle flips of the butterfly knife (random in this range), 0 = never")]
    public Vector2 idleFlipEvery = new Vector2(10f, 18f);

    // sounds that belong to the player's first person animations (draw, reload, pump, hammer):
    // "bolt_back", "bolt_forward", "slide_back", "slide_forward", "pump_back", "pump_forward", "mag_out", "mag_in",
    // "shell" (one shell went in), "cylinder" (the R8 hammer is pulled), "knife_flip".
    // GameAudio plays them without position. The weapon is the one in the hands.
    public static event System.Action<string, WeaponData> AnimationSound;
    // a watched bot's reload and draw are already heard from GameAudio (3D at the bot): only the player's own view sends them
    void EmitSound(string key, WeaponData weapon) { if (shoot != null) AnimationSound?.Invoke(key, weapon); }

    static ViewModel active;
    static Mesh cubeMesh, cylinderMesh, quadMesh;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        active = null;
        cubeMesh = null;
        cylinderMesh = null;
        quadMesh = null;
        AnimationSound = null;
        opaqueCopies.Clear();
    }

    // draw timeline (parts of WeaponData.drawTime): the weapon comes up, then the handle is pulled and released
    const float DrawRaise = 0.38f, DrawPull = 0.52f, DrawRelease = 0.66f;
    // reload timeline (parts of the reload time)
    const float MagOutAt = 0.18f, MagInAt = 0.6f, PullAt = 0.8f, ReleaseAt = 0.88f;
    // bolt after a shot (parts of the time between shots)
    const float BoltBackAt = 0.3f, BoltForwardAt = 0.5f;
    // pump after a shot (parts of the pump time)
    const float PumpBackAt = 0.2f, PumpForwardAt = 0.55f;
    // R8 reload (parts of the reload time): the cylinder swings out, the shells drop, new ones go in, it closes, the hammer
    const float CylinderOutAt = 0.12f, EjectAt = 0.3f, LoadAt = 0.62f, CloseAt = 0.8f, CockAt = 0.88f;
    // a handle pulled after a shell reload that started empty (XM1014), seconds
    const float RackTime = 0.5f;
    // the empty magazine falls out of the hand when it is this far out (magOut 0..1); the new one is shown from this part
    // of the reload on (when it comes back up)
    const float MagReleaseAt = 0.3f, MagBackAt = 0.55f;
    // butterfly knife: the flip on draw and the idle flip, seconds
    const float FlipTime = 0.9f, IdleFlipTime = 1f;
    // R8 fanning: the left hand slaps the hammer, seconds
    const float FanTime = 0.22f;
    // Dual Berettas: the barrels turn in a bit, degrees
    const float DualToe = 3f;
    // burst / auto switch (Glock-18, FAMAS): the gun turns to the thumb on the selector, seconds
    const float ModeTime = 0.35f;

    // the magazine drop on reload, in the weapon root's space
    static readonly Vector3 MagDrop = new Vector3(0f, -0.3f, 0.02f);
    // a magazine on top of the gun (P90) is lifted off backwards
    static readonly Vector3 MagLift = new Vector3(0f, 0.18f, -0.06f);
    // the reload pose: the weapon turns its magazine towards the camera
    static readonly Vector3 ReloadPos = new Vector3(-0.03f, 0.005f, 0.005f);
    static readonly Vector3 ReloadRot = new Vector3(-8f, -15f, -28f);
    // the R8 reload pose: the left side (the cylinder) towards the camera
    static readonly Vector3 RevolverPos = new Vector3(-0.035f, 0.02f, 0f);
    static readonly Vector3 RevolverRot = new Vector3(-10f, -10f, -40f);
    // where the R8 cylinder swings out to, root space
    static readonly Vector3 CylinderSwing = new Vector3(-0.04f, -0.012f, 0f);
    // the knife blade points forward and up
    static readonly Vector3 KnifeTilt = new Vector3(-35f, -10f, -25f);
    // where the elbows are, from the palms (weapon root space)
    static readonly Vector3 RightElbow = new Vector3(0.07f, -0.17f, -0.30f);
    static readonly Vector3 LeftElbow = new Vector3(-0.16f, -0.2f, -0.22f);
    static readonly Vector3 LeftElbowPistol = new Vector3(-0.1f, -0.2f, -0.22f);
    // Dual Berettas: the pistol twirls round its trigger guard (pistol space)
    static readonly Vector3 TwirlPivot = new Vector3(0f, 0.035f, 0.03f);
    static readonly Vector3 Mirror = new Vector3(-1f, 1f, 1f);
    static readonly Color GunFlash = new Color(1f, 0.85f, 0.5f, 1f);
    static readonly Color TaserFlash = new Color(0.55f, 0.75f, 1f, 1f);

    // how the hands work a weapon on draw and reload
    enum Rack
    {
        SideHandle,   // AK-47, Galil AR, PP-Bizon, Negev, XM1014: the handle on the right side of the receiver
        RearHandle,   // M4A4, M4A1-S, SG 553, M249, FAMAS, MP9, MP7, MAC-10: the handle at the rear top
        FrontHandle,  // UMP-45, MP5-SD, P90, AUG, SCAR-20, G3SG1: the lever on the left side, in front
        Bolt,         // AWP, SSG 08: the right hand works the bolt
        Slide,        // pistols: the left hand racks the slide
        Pump,         // Nova, MAG-7: the left hand works the pump
        Revolver,     // R8: the left thumb cocks the hammer
        Dual,         // Dual Berettas: a pistol in each hand, they twirl on draw
        Flip,         // knife: a twirl on draw (the butterfly knife flips open)
        None          // Sawed-Off, Zeus: they are only raised
    }

    // how the left hand holds the weapon
    enum Hold
    {
        Handguard,    // the palm under the handguard (or the pump), the thumb along its side
        FrontGrip,    // around a vertical front grip (MP9, MP7, AUG)
        Pistol,       // around the right hand
        None          // knife
    }

    // where the hands hold an imported model, measured on the models in Resources/WeaponModels:
    // x right, y up, z forward, meters from the Grip of the model
    struct GripRow
    {
        public Hold hold;
        public Vector3 left;     // the left palm
        public Vector3 handle;   // where a hand grabs the charging handle, the bolt, the slide or the hammer
        public Vector3 right;    // the right palm
        public float pull;       // how far the handle (or the pump) goes back
    }

    static GripRow Row(Hold hold, float lx, float ly, float lz, float hx, float hy, float hz, float pull)
    {
        Vector3 right = hold == Hold.Pistol ? new Vector3(0.006f, 0.005f, -0.005f) : new Vector3(0.004f, 0.008f, 0f);
        return new GripRow { hold = hold, left = new Vector3(lx, ly, lz), handle = new Vector3(hx, hy, hz), right = right, pull = pull };
    }

    // the same with the right palm moved (the stock grip of a sniper rifle is behind the trigger)
    static GripRow Row(Hold hold, float lx, float ly, float lz, float hx, float hy, float hz, float pull, float ry, float rz)
    {
        GripRow row = Row(hold, lx, ly, lz, hx, hy, hz, pull);
        row.right = new Vector3(0.004f, ry, rz);
        return row;
    }

    // a shotgun with a "Pump" part takes its left palm from the pump (see ModelGrips), its row has only the pump travel
    static readonly Dictionary<string, GripRow> Grips = new Dictionary<string, GripRow>
    {
        // rifles
        { "AK-47",         Row(Hold.Handguard, -0.006f,  0.050f, 0.33f,   0.035f, 0.115f,  0.17f,  0.07f) },
        { "Galil AR",      Row(Hold.Handguard, -0.006f,  0.050f, 0.33f,   0.035f, 0.120f,  0.17f,  0.07f) },
        { "M4A4",          Row(Hold.Handguard, -0.006f,  0.040f, 0.31f,   0f,     0.130f, -0.005f, 0.05f) },
        { "M4A1-S",        Row(Hold.Handguard, -0.006f,  0.040f, 0.31f,   0f,     0.130f, -0.005f, 0.05f) },
        { "SG 553",        Row(Hold.Handguard, -0.006f,  0.044f, 0.31f,   0f,     0.115f, -0.015f, 0.05f) },
        { "FAMAS",         Row(Hold.Handguard, -0.006f,  0.070f, 0.25f,   0f,     0.165f,  0.06f,  0.05f) },
        { "AUG",           Row(Hold.FrontGrip, -0.004f,  0.050f, 0.31f,  -0.03f,  0.140f,  0.20f,  0.05f) },
        // machine guns
        { "Negev",         Row(Hold.Handguard, -0.006f,  0.052f, 0.33f,   0.035f, 0.135f,  0.17f,  0.07f) },
        { "M249",          Row(Hold.Handguard, -0.006f,  0.057f, 0.36f,   0f,     0.158f, -0.006f, 0.06f) },
        // SMGs
        { "MP9",           Row(Hold.FrontGrip, -0.004f,  0.025f, 0.19f,   0f,     0.130f, -0.03f,  0.04f) },
        { "MP7",           Row(Hold.FrontGrip, -0.004f,  0.020f, 0.23f,   0f,     0.141f, -0.03f,  0.04f) },
        { "MAC-10",        Row(Hold.Handguard, -0.006f,  0.031f, 0.18f,   0f,     0.108f,  0f,     0.035f) },
        { "UMP-45",        Row(Hold.Handguard, -0.006f,  0.062f, 0.26f,  -0.03f,  0.140f,  0.25f,  0.05f) },
        { "MP5-SD",        Row(Hold.Handguard, -0.006f,  0.054f, 0.23f,  -0.028f, 0.130f,  0.24f,  0.05f) },
        { "PP-Bizon",      Row(Hold.Handguard, -0.006f, -0.027f, 0.25f,   0.025f, 0.085f,  0.13f,  0.06f) },
        { "P90",           Row(Hold.Handguard, -0.006f,  0.040f, 0.17f,  -0.025f, 0.060f,  0.15f,  0.04f) },
        // shotguns
        { "Nova",          Row(Hold.Handguard,  0f,      0f,     0f,      0f,     0f,      0f,     0.075f) },
        { "MAG-7",         Row(Hold.Handguard,  0f,      0f,     0f,      0f,     0f,      0f,     0.06f) },
        { "Sawed-Off",     Row(Hold.Handguard,  0f,      0f,     0f,      0f,     0f,      0f,     0f) },
        { "XM1014",        Row(Hold.Handguard, -0.006f, -0.028f, 0.34f,   0.03f,  0.035f,  0.10f,  0.05f) },
        // sniper rifles
        { "AWP",           Row(Hold.Handguard, -0.006f,  0.025f, 0.30f,   0.035f, 0.095f, -0.01f,  0.07f, 0.02f, -0.065f) },
        { "SSG 08",        Row(Hold.Handguard, -0.006f, -0.019f, 0.30f,   0.035f, 0.070f,  0.02f,  0.06f) },
        { "SCAR-20",       Row(Hold.Handguard, -0.006f,  0.017f, 0.30f,  -0.035f, 0.100f,  0.20f,  0.06f) },
        { "G3SG1",         Row(Hold.Handguard, -0.006f,  0.018f, 0.33f,  -0.03f,  0.095f,  0.36f,  0.05f) },
        // pistols (the handle is the rear of the slide, the R8 hammer)
        { "P2000",         Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.068f, -0.01f,  0.03f) },
        { "P250",          Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.078f, -0.01f,  0.03f) },
        { "CZ75-Auto",     Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.078f, -0.01f,  0.03f) },
        { "Five-SeveN",    Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.054f, -0.005f, 0.03f) },
        { "Tec-9",         Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.120f,  0f,     0.035f) },
        { "Dual Berettas", Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.080f, -0.01f,  0.03f) },
        { "R8 Revolver",   Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.072f, -0.012f, 0.02f) },
        { "Zeus x27",      Row(Hold.Pistol,    -0.03f,  -0.012f, 0.012f,  0f,     0.080f, -0.01f,  0f) },
    };

    // a sleeve with a glove at its end; the root sits at the elbow and looks at the palm
    class Arm
    {
        public Transform root, hand;
        public Vector3 palm, elbow;   // rest pose, in the weapon root's space
        public float length;
    }

    class Model
    {
        public GameObject root;
        public Transform muzzle, eject, mag, slide;
        public Vector3 magRest, slideRest;
        public Vector3 magDrop = new Vector3(0f, -0.3f, 0.02f);   // where the magazine goes on reload, in its parent's space
        public Vector3 magDropRoot = MagDrop;                     // the same in the weapon root's space
        public Vector3 slideBack = Vector3.back;                  // the weapon's "back" and barrel axis, in the slide's parent space
        public Vector3 slideAxis = Vector3.forward;
        public Quaternion slideRestRot = Quaternion.identity;
        public float slideTravel;
        public bool boltAction;

        // hands; all points are in the weapon root's space
        public Rack rack;
        public Hold hold;
        public Arm leftArm, rightArm;
        public Vector3 rightPalm, leftPalm;   // where the hands hold the weapon
        public Vector3 handle;                // where a hand grabs the charging handle, the bolt, the slide or the pump
        public Vector3 magGrab;               // the palm under the magazine
        public float pull;                    // how far the hand pulls the handle back
        public Vector3 restOffset;            // camera space

        // the knife model that twirls on draw
        public Transform spin;
        public Vector3 spinAxis, spinRestPos;
        public Quaternion spinRestRot;

        // butterfly knife: the safe handle (A, held) and the bite handle (B) turn round their pins (axes in their parents' space);
        // pinA and pinAxis are in the root's space
        public Transform handleA, handleB;
        public Quaternion handleARest, handleBRest;
        public Vector3 axisA, axisB, pinA, pinAxis;
        public float signA = 1f, signB = -1f;

        // pump of a shotgun: slides back along the barrel
        public Transform pump;
        public Vector3 pumpRest, pumpBack;

        // shotguns: shell by shell reload into the loading port (root space); the shell shown in the left hand
        public bool shellReload, breakAction;
        public Vector3 shellPort;
        public Transform shellInHand;

        // R8 cylinder: turns one chamber per shot, swings out on reload
        public Transform cylinder;
        public Vector3 cylinderRest, cylinderAxis, cylinderOut, cylinderCenter;
        public Quaternion cylinderRestRot;

        // optic (SG 553, AUG): a point on the optic's axis at its rear lens, root space
        public bool optic;
        public Vector3 sight;

        // Dual Berettas: each pistol in its own holder (the left one mirrored); the palm in the holder's space
        public bool dual;
        public Transform rightGun, leftGun, leftMuzzle, leftEject, leftMag;
        public Vector3 rightGunRest, leftGunRest, dualPalm, leftMagRest, leftMagDrop;
    }

    class Shell
    {
        public Transform t;
        public Vector3 velocity, spin;
        public float until;
    }

    // the character the view shows: the player (Init), or the teammate the dead player watches (Bind; shoot is null for a bot)
    Weapon weapon;
    PlayerShoot shoot;
    PlayerLoadout loadout;
    PlayerGrenades grenades;
    Health owner;
    CharacterController controller;
    BotController bot;
    Weapon homeWeapon;
    PlayerShoot homeShoot;
    Health homeOwner;

    // the owner's speed (the player's controller, a bot's movement from frame to frame) and the view turn of a bot
    Vector3 ownerVelocity, lastOwnerPos;
    bool ownerGrounded = true, ownerPosSet;
    Quaternion lastViewRot;
    bool viewRotSet;
    // the empty magazine of this reload has fallen out: hidden until the hand brings the new one
    bool magDropped;

    Transform pivot;
    Transform flash;
    Material flashMaterial, lensMaterial;
    float flashUntil;

    readonly Dictionary<WeaponData, Model> models = new Dictionary<WeaponData, Model>();
    readonly Dictionary<GrenadeData, GrenadeView> grenadeViews = new Dictionary<GrenadeData, GrenadeView>();
    readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    readonly List<Shell> shells = new List<Shell>();
    int nextShell;
    // the idle flips of the knife use their own random numbers (UnityEngine.Random is left to the game)
    readonly System.Random rng = new System.Random();

    Model current;
    WeaponData currentData;
    GrenadeView currentGrenadeView;
    GrenadeData currentGrenadeData;
    float grenadeDrawStart = -10f;

    class GrenadeView
    {
        public GameObject root;
        public Transform modelHolder;
        public Transform pin;
        public Vector3 pinRest;
        public GameObject wickFlame;
        public Arm leftArm, rightArm;
        public Vector3 leftRest, rightRest, pinGrab;
    }

    float drawStart = -10f, drawTime = 0.5f;
    float boltStart = -10f, boltTime = 1f;
    bool boltEjected = true;
    float meleeStart = -10f;
    bool meleeStab;
    float slideKick;
    Vector3 kickPos, kickRot;
    Vector2 sway;
    float bobTimer, bobBlend, airOffset;
    float aim;                                   // optic zoom pose, 0..1

    // pump after a shot (or after an empty shell reload) and the handle pulled after an empty shell reload
    float pumpStart = -10f, pumpTime = 0.5f;
    bool pumpEjected = true;
    float rackStart = -10f;
    // reload: when it started, if the gun was empty, shells already in, the shell reload pose 0..1
    float reloadStartTime = -10f;
    bool reloadFromEmpty, wasReloading;
    int shellsLoaded;
    float shellBlend;
    int shotFrame = -1;
    // R8: shots fired (the cylinder turns one chamber each), the fanning slap, the shown cylinder angle
    int cylinderShots;
    float fanStart = -10f, cylinderAngle;
    bool hammerWas;
    // butterfly knife idle flip
    float idleFlipStart = -10f, nextIdleFlip;
    // the fire mode switch
    float modeStart = -10f;
    // Dual Berettas: recoil of each pistol (0 right, 1 left)
    readonly Vector3[] dualKickPos = new Vector3[2], dualKickRot = new Vector3[2];

    // sounds already played by the current draw, reload, bolt, pump, rack and idle flip (one bit per sound)
    int drawCues, reloadCues = ~0, boltCues = ~0, pumpCues = ~0, rackCues = ~0, idleCues = ~0;

    // the pose built this frame
    Vector3 posePos, poseRot;          // weapon offset from the camera
    Vector3 leftHand, rightHand;       // where the palms go
    Vector3 leftElbowLift;             // the left elbow goes up to reach over the receiver
    float handleBack;                  // handle, slide or bolt pulled back (0..1)
    float boltLift;                    // bolt handle lifted (0..1)
    float magOut;                      // magazine dropped (0..1)
    float spinAngle;                   // knife twirl
    float pumpPull, pumpBack;          // pump pulled by the hand on draw / reload, and after a shot (0..1)
    float cylinderOut, cylinderSpin;   // R8 cylinder swung out (0..1), extra spin after loading
    bool shellShown;                   // a shell is in the left hand
    float flipA, flipB;                // butterfly knife: the handles turned from the open pose (0 = open, 180 = closed)
    readonly Vector3[] dualPos = new Vector3[2], dualRot = new Vector3[2];
    readonly float[] dualSpin = new float[2];

    // true when the main camera shows this character's weapon in first person: the player himself, or the teammate
    // the dead player watches (its magazines and shells then fall from this view, not from the third person gun)
    public static bool IsFirstPerson(Health h)
    {
        return h != null && active != null && active.pivot != null && active.owner == h;
    }

    // a bot looks through the sniper scope of its weapon (AWP, SSG 08, SCAR-20, G3SG1): in a fight, with the weapon
    // ready, and not while a bolt-action rifle loads the next round (like the player, it leaves the scope then)
    public static bool InScope(BotController b)
    {
        if (b == null || b.weapon == null || b.Target == null) return false;
        Weapon w = b.weapon;
        if (!w.HasSniperScope || !w.Scoped || w.IsDrawing || w.IsReloading) return false;
        return !w.BoltAction || Time.time >= w.NextShotTime;
    }

    // where the shots of the shown character (the player, or the watched teammate) come out of the gun model;
    // false when the model is hidden (scope, death)
    public static bool TryGetMuzzle(Health shooter, out Vector3 position)
    {
        position = Vector3.zero;
        if (active == null || active.owner != shooter || active.current == null) return false;
        if (!active.pivot.gameObject.activeInHierarchy) return false;

        // Dual Berettas: the pistol that fired the last shot
        Transform muzzle = active.current.muzzle;
        if (active.current.dual && active.current.leftMuzzle != null && active.weapon != null && active.weapon.LeftHandShot)
            muzzle = active.current.leftMuzzle;
        position = muzzle.position;
        return true;
    }

    public void Init(Weapon w, PlayerShoot s, Health h)
    {
        if (pivot != null) return;

        weapon = w;
        shoot = s;
        owner = h;
        loadout = h != null ? h.GetComponent<PlayerLoadout>() : null;
        grenades = loadout != null ? loadout.Grenades : null;
        controller = h != null ? h.GetComponent<CharacterController>() : null;
        bot = h != null ? h.GetComponent<BotController>() : null;
        homeWeapon = w;
        homeShoot = s;
        homeOwner = h;

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
        Weapon.AnyShellInserted += OnShellInserted;
        Weapon.AnyModeSwitched += OnModeSwitched;
        if (weapon != null) weapon.ReloadStarted += OnReloadStarted;
    }

    // Shows another character's weapon in first person: the teammate the dead player watches (s = null for a bot).
    // Its weapon is in the hands at once (a draw that is going on is shown); then its draws, shots, reloads, knife
    // and scope are shown like the player's own.
    public void Bind(Weapon w, Health h, PlayerShoot s)
    {
        // not set up yet (PlayerShoot.Start calls Init with the player's own weapon)
        if (pivot == null) return;
        if (w == weapon && h == owner && s == shoot) return;

        if (weapon != null) weapon.ReloadStarted -= OnReloadStarted;
        weapon = w;
        owner = h;
        shoot = s;
        loadout = h != null ? h.GetComponent<PlayerLoadout>() : null;
        grenades = loadout != null ? loadout.Grenades : null;
        bot = h != null ? h.GetComponent<BotController>() : null;
        controller = h != null && bot == null ? h.GetComponent<CharacterController>() : null;
        if (weapon != null) weapon.ReloadStarted += OnReloadStarted;

        // the shells and the sway of the other view are gone
        HideShells();
        sway = Vector2.zero;
        bobBlend = 0f;
        airOffset = 0f;
        ownerPosSet = false;
        viewRotSet = false;
        ownerVelocity = Vector3.zero;

        SwitchTo(weapon != null ? weapon.data : null);
        if (weapon == null || !weapon.IsDrawing)
        {
            drawStart = -10f;
            drawCues = ~0;
        }
        // a reload that is going on goes on from where it is (its magazine may have fallen out already)
        if (weapon != null && weapon.IsReloading)
        {
            wasReloading = true;
            reloadFromEmpty = false;
            reloadStartTime = Time.time;
            shellsLoaded = 0;
            magDropped = weapon.ReloadProgress >= 0.22f;
        }
    }

    // back to the player's own weapon (the player is alive again, or there is nobody to watch)
    public void BindHome()
    {
        Bind(homeWeapon, homeOwner, homeShoot);
    }

    void OnDestroy()
    {
        Weapon.AnyShot -= OnShot;
        Weapon.AnyEquipped -= OnEquipped;
        Weapon.AnyShellInserted -= OnShellInserted;
        Weapon.AnyModeSwitched -= OnModeSwitched;
        if (weapon != null) weapon.ReloadStarted -= OnReloadStarted;
        if (active == this) active = null;
        foreach (Shell s in shells)
            if (s.t != null) Destroy(s.t.gameObject);
    }

    // ---------------- events ----------------

    void OnShot(ShotInfo s)
    {
        if (owner == null || s.shooter != owner || current == null) return;
        // the other pellets of a shotgun shell: one kick, one flash and one shell for the whole trigger pull
        if (s.pellet) return;

        if (s.melee)
        {
            meleeStart = Time.time;
            meleeStab = s.stab;
            // an attack ends the flips of the butterfly knife at once
            idleFlipStart = -10f;
            // (the flip clicks not played yet are dropped, else they would all sound at once)
            if (Time.time - drawStart < FlipTime) { drawStart = Time.time - FlipTime; drawCues = ~0; }
            return;
        }

        Model m = current;
        shotFrame = Time.frameCount;
        float r = Mathf.Max(0.3f, weapon.recoilFactor);
        bool left = m.dual && s.leftHand;

        if (m.dual)
        {
            // only the pistol that fired kicks, the hands a little
            int i = left ? 1 : 0;
            float side = left ? -1f : 1f;
            dualKickPos[i] += new Vector3(0f, kickBack * 0.2f, -kickBack) * r;
            dualKickRot[i] += new Vector3(-kickUp * 1.5f, side * Random.Range(0f, 1f) * kickUp * 0.4f, side * Random.Range(0f, 1f) * kickUp) * r;
            dualKickPos[i].z = Mathf.Max(dualKickPos[i].z, -0.09f);
            dualKickRot[i].x = Mathf.Max(dualKickRot[i].x, -25f);
            kickRot += new Vector3(-kickUp * 0.2f, 0f, 0f) * r;
        }
        else
        {
            kickPos += new Vector3(0f, kickBack * 0.15f, -kickBack) * r;
            kickRot += new Vector3(-kickUp, Random.Range(-1f, 1f) * kickUp * 0.3f, Random.Range(-1f, 1f) * kickUp * 0.4f) * r;
            kickPos.z = Mathf.Max(kickPos.z, -0.09f);
            kickRot.x = Mathf.Max(kickRot.x, -16f);
            slideKick = 1f;
        }

        // the flash at the muzzle that fired: small for silenced weapons, blue for the Zeus
        Transform muzzle = left && m.leftMuzzle != null ? m.leftMuzzle : m.muzzle;
        if (flash.parent != muzzle) flash.SetParent(muzzle, false);
        float size = s.silenced ? 0.4f : s.taser ? 0.75f : 1f;
        flashMaterial.color = s.taser ? TaserFlash : GunFlash;
        flashUntil = Time.time + (s.silenced ? 0.025f : 0.035f);
        flash.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
        flash.localScale = Vector3.one * Random.Range(0.8f, 1.3f) * size;

        if (m.rack == Rack.Revolver)
        {
            // the next chamber; fanning: the left hand slapped the hammer
            cylinderShots++;
            if (shoot != null && Input.GetMouseButton(1) && !Input.GetButton("Fire1"))
            {
                fanStart = Time.time;
                kickRot += new Vector3(-kickUp * 0.5f, 0f, 6f) * r;
            }
        }

        if (m.boltAction)
        {
            boltStart = Time.time;
            boltTime = 1f / Mathf.Max(weapon.fireRate, 0.01f);
            boltEjected = false;
            boltCues = 0;
        }
        else if (m.rack == Rack.Pump)
        {
            // pump action: the empty shell flies out when the pump goes back (not after the last shell, a reload comes then)
            if (weapon.Ammo > 0) StartPump(true);
        }
        else if (!m.breakAction && !s.taser && m.rack != Rack.Revolver)
        {
            // (the R8 keeps its cases in the cylinder, they drop out on reload)
            EjectShell(left);
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
        reloadStartTime = Time.time;
        reloadFromEmpty = weapon != null && weapon.Ammo == 0;
        shellsLoaded = 0;
        magDropped = false;

        // a break-action gun drops its empty shells when it opens (they land on the floor, see DropShells)
        if (current != null && current.breakAction && pivot.gameObject.activeInHierarchy)
            DropShells(current.shellPort + new Vector3(0f, 0.06f, -0.02f), 2, false);
    }

    // shell by shell reload: one shell went in (the left hand has just pushed it)
    void OnShellInserted(Weapon w)
    {
        if (w != weapon || current == null || !current.shellReload) return;
        shellsLoaded++;
        EmitSound("shell", currentData);
        kickPos += new Vector3(0f, 0.002f, 0.003f);
        kickRot += new Vector3(-1f, 0f, -1.5f);
    }

    // burst / auto switched (right click with the Glock-18 or the FAMAS)
    void OnModeSwitched(Weapon w)
    {
        if (w == weapon) modeStart = Time.time;
    }

    // ---------------- every frame ----------------

    void LateUpdate()
    {
        if (pivot == null) return;

        float dt = Time.deltaTime;
        UpdateShells(dt);
        TrackOwner(dt);

        bool alive = owner != null && !owner.IsDead;
        bool inGrenade = alive && shoot != null && loadout != null && loadout.InGrenadeSlot && grenades != null
            && (grenades.Selected != null || grenades.ThrowingData != null);
        if (inGrenade)
        {
            if (current != null && current.root.activeSelf) current.root.SetActive(false);
            if (flash != null && flash.gameObject.activeSelf) flash.gameObject.SetActive(false);
            if (!pivot.gameObject.activeSelf) pivot.gameObject.SetActive(true);
            RenderGrenadeView(dt);
            return;
        }
        if (currentGrenadeView != null && currentGrenadeView.root.activeSelf)
            currentGrenadeView.root.SetActive(false);
        currentGrenadeData = null;

        if (weapon != null && weapon.data != currentData) SwitchTo(weapon.data);

        // sounds go on even when the model is hidden (the AWP bolt in the scope)
        if (alive && current != null && weapon != null)
        {
            TrackReload();
            PlayCues();
        }

        // a sniper scope hides the model; an optic (SG 553, AUG) brings it up to the eye, and once it is there the
        // model is hidden and PlayerShoot draws the round optic view (like CS 2: the scope body would cover the target);
        // a watched bot in its sniper scope: Spectator draws the scope (a bot's optic stays at the hip)
        bool optic = shoot != null && shoot.IsOpticZoom;
        bool scoped = shoot != null ? shoot.IsScoped && !optic : InScope(bot);
        bool show = alive && current != null && !scoped;
        aim = Mathf.MoveTowards(aim, show && optic && current.optic ? 1f : 0f, opticSpeed * dt);
        if (show && optic && current.optic && aim >= 0.999f) show = false;
        if (pivot.gameObject.activeSelf != show) pivot.gameObject.SetActive(show);
        shellBlend = Mathf.MoveTowards(shellBlend, current != null && current.shellReload && weapon.IsReloading ? 1f : 0f, 4f * dt);
        if (!show) return;

        Model m = current;
        posePos = hipPosition + m.restOffset;
        poseRot = Vector3.zero;
        leftHand = m.leftPalm;
        rightHand = m.rightPalm;
        leftElbowLift = Vector3.zero;
        handleBack = 0f;
        boltLift = 0f;
        magOut = 0f;
        spinAngle = 0f;
        pumpPull = 0f;
        pumpBack = 0f;
        cylinderOut = 0f;
        cylinderSpin = 0f;
        shellShown = false;
        flipA = 0f;
        flipB = 0f;
        for (int i = 0; i < 2; i++)
        {
            dualPos[i] = Vector3.zero;
            dualRot[i] = Vector3.zero;
            dualSpin[i] = 0f;
        }

        AnimateDraw(Time.time - drawStart);
        Sway(dt);

        // recoil kick, recovers quickly
        float k = 1f - Mathf.Exp(-kickRecover * dt);
        kickPos = Vector3.Lerp(kickPos, Vector3.zero, k);
        kickRot = Vector3.Lerp(kickRot, Vector3.zero, k);
        posePos += kickPos;
        poseRot += kickRot;
        slideKick = Mathf.MoveTowards(slideKick, 0f, dt * 12f);
        handleBack = Mathf.Max(handleBack, slideKick);
        if (m.dual)
        {
            for (int i = 0; i < 2; i++)
            {
                dualKickPos[i] = Vector3.Lerp(dualKickPos[i], Vector3.zero, k);
                dualKickRot[i] = Vector3.Lerp(dualKickRot[i], Vector3.zero, k);
                dualPos[i] += dualKickPos[i];
                dualRot[i] += dualKickRot[i];
            }
        }

        if (m.shellReload)
        {
            AnimateShellReload();
        }
        else if (weapon.IsReloading && !weapon.IsMelee)
        {
            float p = weapon.ReloadProgress;
            if (m.dual) AnimateDualReload(p);
            else if (m.rack == Rack.Revolver) AnimateRevolverReload(p);
            else AnimateReload(p);
        }

        float bt = (Time.time - boltStart) / boltTime;
        if (m.boltAction && bt >= 0f && bt < 1f) AnimateBolt(bt);

        float pt = (Time.time - pumpStart) / pumpTime;
        if (pt >= 0f && pt < 1f) AnimatePump(pt);

        float rk = (Time.time - rackStart) / RackTime;
        if (rk >= 0f && rk < 1f) WorkDraw(0.3f + 0.7f * rk);

        if (m.rack == Rack.Revolver) AnimateRevolver(dt);

        // the fire mode switch: a short turn of the gun, the thumb works the selector
        float mt = (Time.time - modeStart) / ModeTime;
        if (mt >= 0f && mt < 1f)
        {
            float f = Window(mt, 0f, 0.3f, 0.6f, 1f);
            poseRot += new Vector3(-4f, 6f, -12f) * f;
            posePos += new Vector3(-0.008f, 0.006f, 0f) * f;
        }

        if (weapon.IsMelee)
        {
            AnimateMelee();
            if (m.handleA != null) AnimateIdleFlip();
        }

        AnimateAim();
        ApplyPose();
        DropMagazines();

        bool flashOn = Time.time < flashUntil;
        if (flash.gameObject.activeSelf != flashOn) flash.gameObject.SetActive(flashOn);
    }

    // the end of a shell reload that started with an empty gun: the pump (Nova) or the handle (XM1014) chambers a shell
    void TrackReload()
    {
        bool reloading = weapon.IsReloading;
        if (wasReloading && !reloading && reloadFromEmpty && current.shellReload && shotFrame != Time.frameCount && !weapon.IsMelee)
        {
            // the hull of the last shot is still in the chamber: the pump throws it out
            if (current.rack == Rack.Pump) StartPump(true);
            else if (current.rack != Rack.None)
            {
                rackStart = Time.time;
                rackCues = 0;
            }
        }
        wasReloading = reloading;
    }

    // how the owner moves: the player's controller, a watched bot from its position (in the air while it hops)
    void TrackOwner(float dt)
    {
        if (controller != null && bot == null)
        {
            ownerVelocity = controller.velocity;
            ownerGrounded = controller.isGrounded;
            return;
        }

        ownerGrounded = bot == null || !bot.IsHopping;
        if (owner == null)
        {
            ownerVelocity = Vector3.zero;
            return;
        }
        Vector3 p = owner.transform.position;
        Vector3 v = ownerPosSet && dt > 0f ? (p - lastOwnerPos) / dt : Vector3.zero;
        // a jump of the position (a respawn) is not a speed
        ownerVelocity = v.sqrMagnitude > 15f * 15f ? Vector3.zero : Vector3.Lerp(ownerVelocity, v, 1f - Mathf.Exp(-20f * dt));
        lastOwnerPos = p;
        ownerPosSet = true;
    }

    // sway: the weapon lags behind the mouse; bob while walking, breathing while standing, lag while in the air
    void Sway(float dt)
    {
        Vector2 mouse = Vector2.zero;
        if (shoot != null)
        {
            if (Cursor.lockState == CursorLockMode.Locked) mouse = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
        }
        else if (viewRotSet)
        {
            // a watched bot: the turn of the view this frame instead of the mouse (about 2 degrees per mouse unit)
            Vector3 e = (Quaternion.Inverse(lastViewRot) * transform.rotation).eulerAngles;
            mouse = new Vector2(Mathf.DeltaAngle(0f, e.y), -Mathf.DeltaAngle(0f, e.x)) * 0.5f;
        }
        lastViewRot = transform.rotation;
        viewRotSet = true;

        Vector2 swayTarget = Vector2.ClampMagnitude(new Vector2(-mouse.x, mouse.y) * swayAmount, swayMax);
        sway = Vector2.Lerp(sway, swayTarget, 1f - Mathf.Exp(-swaySmooth * dt));
        poseRot += new Vector3(sway.y, sway.x, sway.x * 0.6f);
        posePos += new Vector3(sway.x, -sway.y, 0f) * 0.002f;

        Vector3 vel = ownerVelocity;
        bool grounded = ownerGrounded;
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

    // draw: the weapon comes up from below, then the hands work it like in CS (t: seconds since the draw started)
    void AnimateDraw(float t)
    {
        if (current.handleA != null)
        {
            ButterflyDraw(t / FlipTime);
            return;
        }

        float u = t / drawTime;
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

            case Rack.Dual:
                // both pistols twirl round the trigger finger, the left one a moment later
                dualSpin[0] = 360f * Ease(Mathf.Clamp01((u - 0.3f) / 0.45f));
                dualSpin[1] = 360f * Ease(Mathf.Clamp01((u - 0.42f) / 0.45f));
                break;

            case Rack.None:
                // the Zeus and the Sawed-Off: a turn of the wrist while they come up
                poseRot += new Vector3(-6f, 0f, -18f) * Mathf.Sin(Mathf.Clamp01(u / 0.8f) * Mathf.PI);
                break;

            default:
                WorkDraw(u);
                break;
        }
    }

    // the hands work the handle on draw (u is the draw progress; also used for the handle pulled after a reload)
    void WorkDraw(float u)
    {
        if (current.rack == Rack.Bolt)
        {
            // the right hand lifts the bolt, pulls it back, pushes it forward and lowers it
            WorkHandle(Window(u, 0.25f, 0.4f, 0.85f, 1f), Window(u, 0.33f, 0.43f, 0.84f, 0.97f),
                       Window(u, DrawPull, 0.6f, DrawRelease, 0.74f), 0f, Window(u, 0.42f, 0.5f, 0.76f, 0.84f));
            return;
        }

        // the left hand goes to the handle (slide, pump), pulls it back and lets it go;
        // the handle snaps forward, the hand follows a moment later (the pump is pushed forward by the hand)
        bool pump = current.rack == Rack.Pump;
        WorkHandle(Window(u, 0.3f, 0.45f, 0.85f, 1f), Window(u, 0.35f, 0.5f, 0.75f, 0.95f),
                   Window(u, DrawPull, DrawPull + 0.1f, DrawRelease, DrawRelease + 0.04f),
                   pump ? 0f : Window(u, DrawPull, DrawPull + 0.1f, DrawRelease + 0.06f, 0.9f), 0f);
    }

    // butterfly knife on draw (f: part of FlipTime): it comes up closed, flips open (the blade swings round the
    // pin of the held safe handle, the bite handle makes a full turn and closes on it), then spins once in the hand
    void ButterflyDraw(float f)
    {
        float raise = Ease(Mathf.Clamp01(f / 0.22f));
        posePos += new Vector3(0f, -0.2f, -0.05f) * (1f - raise);
        poseRot += new Vector3(40f, -10f, 0f) * (1f - raise);
        if (f >= 1f) return;

        float open = Ease(Mathf.Clamp01((f - 0.2f) / 0.42f));
        flipA = 180f * (1f - open);
        flipB = 180f * (1f - open);
        spinAngle = 360f * Ease(Mathf.Clamp01((f - 0.66f) / 0.28f));

        // the wrist throws the knife open
        float wrist = Mathf.Sin(open * Mathf.PI);
        poseRot += new Vector3(-10f, 0f, 28f) * wrist;
        posePos += new Vector3(0f, 0.01f, 0f) * wrist;
    }

    // butterfly knife now and then: it closes in the hand and flips open again
    void AnimateIdleFlip()
    {
        float t = Time.time;
        bool busy = t - drawStart < FlipTime + 0.5f || t - meleeStart < 2f;
        if (idleFlipEvery.y > 0f && idleFlipStart < 0f && !busy && t >= nextIdleFlip)
        {
            idleFlipStart = t;
            idleCues = 0;
        }
        if (busy) idleFlipStart = -10f;
        if (idleFlipStart < 0f) return;

        float u = (t - idleFlipStart) / IdleFlipTime;
        if (u >= 1f)
        {
            idleFlipStart = -10f;
            ScheduleIdleFlip();
            return;
        }

        float closed = Window(u, 0.05f, 0.4f, 0.55f, 0.9f);
        flipA = Mathf.Max(flipA, 180f * closed);
        flipB = Mathf.Max(flipB, 180f * closed);
        poseRot += new Vector3(-8f, 0f, 22f) * Mathf.Sin(u * Mathf.PI);
    }

    void ScheduleIdleFlip()
    {
        float a = Mathf.Max(1f, idleFlipEvery.x), b = Mathf.Max(a, idleFlipEvery.y);
        nextIdleFlip = Time.time + a + (float)rng.NextDouble() * (b - a);
    }

    // reload: the weapon turns its magazine to the camera, the left hand takes the magazine out and puts it back,
    // then the handle (slide, bolt, pump) is pulled
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
        leftHand = Vector3.Lerp(leftHand, current.magGrab + current.magDropRoot * magOut * follow, onMag);

        // the slide or the charging handle is pulled at the end; the bolt is lifted first, by the right hand
        float back = Window(p, 0.78f, 0.86f, 0.88f, 0.95f);
        if (current.rack == Rack.Bolt)
            WorkHandle(Window(p, 0.66f, 0.76f, 0.92f, 1f), Window(p, 0.66f, 0.74f, 0.95f, 1f), back, 0f,
                       Window(p, 0.74f, 0.78f, 0.95f, 0.98f));
        else if (current.rack != Rack.None)
            WorkHandle(Window(p, 0.7f, 0.8f, 0.92f, 1f), Window(p, 0.72f, 0.8f, 0.92f, 1f), back,
                       current.rack == Rack.Pump ? 0f : Window(p, 0.78f, 0.86f, 0.9f, 0.97f), 0f);
    }

    // shell by shell (Nova, XM1014, Sawed-Off): the gun turns its loading port to the left hand, the hand takes a shell
    // from below and pushes it in; the push ends when Weapon puts the shell in (ShellProgress reaches 1)
    void AnimateShellReload()
    {
        float tilt = Ease(shellBlend);
        if (tilt <= 0f) return;
        posePos += ReloadPos * tilt;
        poseRot += ReloadRot * tilt;

        Model m = current;
        Vector3 port = m.shellPort + new Vector3(0f, -0.03f, 0f);      // the palm under the port
        Vector3 entry = port + new Vector3(-0.012f, -0.05f, -0.01f);   // just below it, with the shell
        Vector3 pouch = m.shellPort + new Vector3(-0.1f, -0.22f, -0.05f);
        float q = weapon.ShellProgress;
        Vector3 hand;

        if (weapon.IsReloading && shellsLoaded == 0 && q <= 0f)
        {
            // the start of the reload: the hand goes down for the first shell
            float s = (Time.time - reloadStartTime) / Mathf.Max(weapon.shellStartDelay, 0.05f);
            hand = Vector3.Lerp(m.leftPalm, pouch, Ease(Mathf.Clamp01(s)));
        }
        else
        {
            Vector3 from = shellsLoaded == 0 ? pouch : port;
            if (q < 0.25f) hand = Vector3.Lerp(from, pouch, Ease(q / 0.25f));
            else if (q < 0.7f) hand = Vector3.Lerp(pouch, entry, Ease((q - 0.25f) / 0.45f));
            else hand = Vector3.Lerp(entry, port, Ease((q - 0.7f) / 0.3f));
            shellShown = weapon.IsReloading && q > 0.2f && q < 0.97f;
        }

        leftHand = Vector3.Lerp(leftHand, hand, tilt);
        leftElbowLift += new Vector3(0f, -0.05f, 0f) * tilt;
    }

    // Dual Berettas: both pistols turn their magazines in, the magazines drop and come back, then both slides
    void AnimateDualReload(float p)
    {
        float tilt = Window(p, 0f, 0.15f, 0.85f, 1f);
        float together = Window(p, 0.35f, 0.5f, 0.62f, 0.75f);
        posePos += new Vector3(0f, -0.01f, 0f) * tilt;
        dualPos[0] += new Vector3(-0.03f, 0.02f, 0f) * tilt + new Vector3(-0.03f, -0.02f, 0f) * together;
        dualPos[1] += new Vector3(0.03f, 0.02f, 0f) * tilt + new Vector3(0.03f, -0.02f, 0f) * together;
        dualRot[0] += new Vector3(-10f, 10f, -30f) * tilt;
        dualRot[1] += new Vector3(-10f, -10f, 30f) * tilt;
        magOut = Window(p, 0.15f, 0.35f, 0.55f, 0.75f);

        // both slides at the end
        handleBack = Mathf.Max(handleBack, Window(p, 0.78f, 0.86f, 0.88f, 0.95f));
        float back = Window(p, 0.78f, 0.86f, 0.88f, 0.95f);
        dualPos[0] += new Vector3(0f, 0.003f, -0.01f) * back;
        dualPos[1] += new Vector3(0f, 0.003f, -0.01f) * back;
    }

    // R8 reload: the cylinder swings out, the left hand pushes the ejector (the shells drop), takes new rounds from below,
    // puts them in, closes the cylinder and spins it
    void AnimateRevolverReload(float p)
    {
        Model m = current;
        float tilt = Window(p, 0f, 0.12f, 0.88f, 1f);
        posePos += RevolverPos * tilt;
        poseRot += RevolverRot * tilt;

        cylinderOut = Window(p, CylinderOutAt, CylinderOutAt + 0.08f, CloseAt - 0.04f, CloseAt + 0.02f);
        cylinderSpin = 720f * Ease(Mathf.Clamp01((p - CloseAt) / 0.12f));

        Vector3 atCylinder = m.cylinderCenter + CylinderSwing * cylinderOut + new Vector3(-0.012f, -0.03f, 0f);
        Vector3 below = m.cylinderCenter + new Vector3(-0.09f, -0.2f, 0f);
        Vector3 hand = atCylinder;
        if (p < 0.12f) hand = Vector3.Lerp(m.leftPalm, atCylinder, Ease(p / 0.12f));
        else if (p < 0.3f) hand = atCylinder + new Vector3(0f, 0f, -0.02f) * Window(p, 0.22f, 0.27f, 0.28f, 0.3f);   // the ejector
        else if (p < 0.42f) hand = Vector3.Lerp(atCylinder, below, Ease((p - 0.3f) / 0.12f));
        else if (p < 0.58f) hand = Vector3.Lerp(below, atCylinder + new Vector3(0f, -0.01f, 0.03f), Ease((p - 0.42f) / 0.16f));
        else if (p < 0.66f) hand = Vector3.Lerp(atCylinder + new Vector3(0f, -0.01f, 0.03f), atCylinder, Ease((p - 0.58f) / 0.08f));
        else if (p < 0.8f) hand = atCylinder + new Vector3(0.015f, 0f, 0f) * Window(p, 0.74f, 0.8f, 0.8f, 0.82f);    // closes it
        else hand = Vector3.Lerp(atCylinder, m.leftPalm, Ease((p - 0.8f) / 0.15f));
        leftHand = Vector3.Lerp(leftHand, hand, tilt);
    }

    // R8 every frame: the hammer pull turns the gun a little and the cylinder to the next chamber,
    // fanning: the left hand slaps the hammer
    void AnimateRevolver(float dt)
    {
        float h = weapon.IsHammerPulling ? weapon.HammerProgress : 0f;
        poseRot += new Vector3(-2.5f, 0f, 3f) * h;
        posePos += new Vector3(0f, -0.002f, -0.006f) * h;

        float ft = (Time.time - fanStart) / FanTime;
        if (ft >= 0f && ft < 1f)
        {
            float slap = Window(ft, 0f, 0.35f, 0.45f, 1f);
            leftHand = Vector3.Lerp(leftHand, current.handle + new Vector3(-0.01f, 0.035f, -0.01f), slap);
            leftElbowLift += new Vector3(0f, 0.08f, 0f) * slap;
        }

        float target = cylinderShots * 45f + 45f * h;
        cylinderAngle = Mathf.MoveTowards(cylinderAngle, target, 900f * dt);
    }

    // The hands work the handle. turn: the weapon is turned to show the handle; reach: a hand is on it;
    // back: the handle (slide, bolt, pump) is pulled back; handBack: the hand is pulled back (0 = same as the handle);
    // lift: the bolt handle is lifted.
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
            case Rack.FrontHandle:  // the left side turns to the camera
                posePos += new Vector3(-0.01f, 0.012f, -0.01f) * turn;
                poseRot += new Vector3(-6f, 8f, -14f) * turn;
                break;
            case Rack.Bolt:
                posePos += new Vector3(0f, 0.005f, 0f) * turn;
                poseRot += new Vector3(-3f, 4f, 14f) * turn;
                break;
            case Rack.Slide:
            case Rack.Revolver:
                posePos += new Vector3(-0.015f, 0.01f, 0f) * turn;
                poseRot += new Vector3(-6f, -8f, 15f) * turn;
                break;
            case Rack.Pump:         // the gun comes up a bit, the pump goes back and forward
                posePos += new Vector3(-0.01f, 0.012f, -0.012f) * turn;
                poseRot += new Vector3(-8f, -3f, 8f) * turn;
                break;
        }

        if (m.rack == Rack.Pump)
            pumpPull = Mathf.Max(pumpPull, back);
        else
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
        if (m.rack == Rack.FrontHandle) leftElbowLift += new Vector3(0f, 0.04f, 0f) * reach;

        // a pistol model without a separate slide: the whole gun jerks back a little
        if ((m.rack == Rack.Slide || m.rack == Rack.Revolver) && m.slide == null)
        {
            posePos += new Vector3(0f, 0.003f, -0.008f) * back;
            poseRot += new Vector3(-3f, 0f, 0f) * back;
        }
    }

    // bolt-action rifle after a shot: the right hand lifts the bolt handle, pulls it back, pushes it forward, lowers it
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
            EjectShell(false);
        }
    }

    void StartPump(bool eject)
    {
        pumpStart = Time.time;
        pumpTime = Mathf.Clamp(0.7f / Mathf.Max(weapon.fireRate, 0.01f), 0.35f, 0.6f);
        pumpEjected = !eject;
        pumpCues = 0;
    }

    // pump shotgun after a shot: the left hand pulls the pump back (the empty shell flies out) and pushes it forward
    void AnimatePump(float pt)
    {
        float stroke = Window(pt, 0.15f, 0.42f, 0.5f, 0.78f);
        pumpBack = Mathf.Max(pumpBack, stroke);
        poseRot += new Vector3(-4f, 2f, 6f) * stroke;
        posePos += new Vector3(0f, -0.004f, -0.008f) * stroke;

        if (!pumpEjected && pt > 0.4f)
        {
            pumpEjected = true;
            EjectShell(false);
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

    // optic zoom: the optic comes in front of the eye (a quarter of the sway, bob and kick stays)
    void AnimateAim()
    {
        float a = Ease(aim);
        if (a <= 0f || !current.optic) return;

        Vector3 rest = hipPosition + current.restOffset;
        Vector3 target = new Vector3(0f, 0f, opticEye) - current.sight * modelScale;
        posePos = Vector3.Lerp(posePos, target + (posePos - rest) * 0.25f, a);
        poseRot = Vector3.Lerp(poseRot, poseRot * 0.25f, a);
    }

    // moves the parts of the model to the pose built this frame
    void ApplyPose()
    {
        Model m = current;

        // the dropped magazine is gone until the new one comes back up with the hand
        bool magGone = MagGone();
        if (m.mag != null)
        {
            m.mag.localPosition = m.magRest + m.magDrop * magOut;
            bool magVisible = magOut < 0.95f && !magGone;
            if (m.mag.gameObject.activeSelf != magVisible) m.mag.gameObject.SetActive(magVisible);
        }

        if (m.slide != null)
        {
            m.slide.localPosition = m.slideRest + m.slideBack * m.slideTravel * handleBack;
            m.slide.localRotation = Quaternion.AngleAxis(70f * boltLift, m.slideAxis) * m.slideRestRot;
        }

        if (m.pump != null)
        {
            float back = Mathf.Max(pumpPull, pumpBack);
            m.pump.localPosition = m.pumpRest + m.pumpBack * m.pull * back;
            // the hand on the pump goes with it after a shot (on draw and reload WorkHandle moves the hand)
            leftHand += Vector3.back * m.pull * pumpBack;
        }
        else if (m.rack == Rack.Pump)
        {
            // no pump part: the hand goes back and forward under the barrel
            leftHand += Vector3.back * 0.05f * pumpBack;
        }

        if (m.cylinder != null)
        {
            m.cylinder.localPosition = m.cylinderRest + m.cylinderOut * cylinderOut;
            m.cylinder.localRotation = Quaternion.AngleAxis(cylinderAngle + cylinderSpin, m.cylinderAxis) * m.cylinderRestRot;
        }

        if (m.shellInHand != null && m.shellInHand.gameObject.activeSelf != shellShown) m.shellInHand.gameObject.SetActive(shellShown);

        if (m.spin != null)
        {
            Vector3 pos = m.spinRestPos;
            Quaternion rot = m.spinRestRot;
            if (m.handleA != null)
            {
                // the hand holds the safe handle: the rest of the knife turns round its pin
                Quaternion hold = Quaternion.AngleAxis(-m.signA * flipA, m.pinAxis);
                pos = m.pinA + hold * (pos - m.pinA);
                rot = hold * rot;
                m.handleA.localRotation = Quaternion.AngleAxis(m.signA * flipA, m.axisA) * m.handleARest;
                m.handleB.localRotation = Quaternion.AngleAxis(m.signB * flipB, m.axisB) * m.handleBRest;
            }
            Quaternion q = Quaternion.AngleAxis(spinAngle, m.spinAxis);
            m.spin.localPosition = q * pos;
            m.spin.localRotation = q * rot;
        }

        if (m.dual)
        {
            PoseGun(m.rightGun, m.rightGunRest, -DualToe, 0);
            PoseGun(m.leftGun, m.leftGunRest, DualToe, 1);
            rightHand = m.rightGun.localPosition + m.rightGun.localRotation * m.dualPalm;
            leftHand = m.leftGun.localPosition + m.leftGun.localRotation * Vector3.Scale(m.dualPalm, Mirror);
            if (m.leftMag != null)
            {
                m.leftMag.localPosition = m.leftMagRest + m.leftMagDrop * magOut;
                bool magVisible = magOut < 0.95f && !magGone;
                if (m.leftMag.gameObject.activeSelf != magVisible) m.leftMag.gameObject.SetActive(magVisible);
            }
        }

        if (m.leftArm != null) Reach(m.leftArm, leftHand, leftElbowLift);
        if (m.rightArm != null) Reach(m.rightArm, rightHand, Vector3.zero);

        pivot.localPosition = posePos;
        pivot.localRotation = Quaternion.Euler(poseRot);
    }

    // Dual Berettas: one pistol in its holder (i: 0 right, 1 left): kick, reload pose and the twirl round the trigger guard
    void PoseGun(Transform gun, Vector3 rest, float toe, int i)
    {
        Quaternion baseRot = Quaternion.Euler(dualRot[i] + new Vector3(0f, toe, 0f));
        Quaternion rot = baseRot * Quaternion.AngleAxis(-dualSpin[i], Vector3.right);
        gun.localPosition = rest + dualPos[i] + baseRot * TwirlPivot - rot * TwirlPivot;
        gun.localRotation = rot;
    }

    // sounds of the first person animations, each one once per animation
    void PlayCues()
    {
        Model m = current;
        string back = BackKey(m.rack);
        string forward = ForwardKey(m.rack);
        float now = Time.time;

        if (m.handleA != null)
        {
            // butterfly knife: the latch opens, the handles snap together, the knife is caught after the spin
            float f = (now - drawStart) / FlipTime;
            if (f < 1.2f)
            {
                Cue(ref drawCues, 1, f >= 0.2f, "knife_flip");
                Cue(ref drawCues, 2, f >= 0.6f, "knife_flip");
                Cue(ref drawCues, 4, f >= 0.92f, "knife_flip");
            }
            if (idleFlipStart >= 0f)
            {
                float u = (now - idleFlipStart) / IdleFlipTime;
                Cue(ref idleCues, 1, u >= 0.38f, "knife_flip");
                Cue(ref idleCues, 2, u >= 0.88f, "knife_flip");
            }
        }
        else
        {
            float u = (now - drawStart) / drawTime;
            if (u < 1.2f)
            {
                if (m.rack == Rack.Flip)
                {
                    Cue(ref drawCues, 1, true, "knife_flip");
                }
                else if (m.rack == Rack.Dual)
                {
                    // both pistols caught after the twirl
                    Cue(ref drawCues, 1, u >= 0.72f, "slide_back");
                    if (Cue(ref drawCues, 2, u >= 0.86f, "slide_forward")) Jerk();
                }
                else if (m.rack != Rack.None)
                {
                    Cue(ref drawCues, 1, u >= DrawPull, back);
                    if (Cue(ref drawCues, 2, u >= DrawRelease, forward)) Jerk();
                }
            }
        }

        // reload (a shell reload is heard shell by shell, see OnShellInserted)
        if (weapon.IsReloading && !weapon.IsMelee && !m.shellReload)
        {
            float p = weapon.ReloadProgress;
            if (m.rack == Rack.Revolver)
            {
                Cue(ref reloadCues, 1, p >= EjectAt, "mag_out");
                Cue(ref reloadCues, 2, p >= LoadAt, "mag_in");
                Cue(ref reloadCues, 4, p >= CloseAt, back);
                if (Cue(ref reloadCues, 8, p >= CockAt, forward)) Jerk();
                if (Cue(ref reloadCues, 16, p >= EjectAt, null)) DropShells(m.cylinderCenter + CylinderSwing, 4, true);
            }
            else
            {
                Cue(ref reloadCues, 1, p >= MagOutAt, "mag_out");
                Cue(ref reloadCues, 2, p >= MagInAt, "mag_in");
                if (m.rack != Rack.None)
                {
                    // MAG-7 fired dry: the pump pulled at the end throws out the hull of the last shot
                    if (Cue(ref reloadCues, 4, p >= PullAt, back) && m.rack == Rack.Pump && reloadFromEmpty) EjectShell(false);
                    if (Cue(ref reloadCues, 8, p >= ReleaseAt, forward)) Jerk();
                }
            }
        }

        float bt = (now - boltStart) / boltTime;
        if (m.boltAction && bt >= 0f && bt < 1f)
        {
            Cue(ref boltCues, 1, bt >= BoltBackAt, "bolt_back");
            Cue(ref boltCues, 2, bt >= BoltForwardAt, "bolt_forward");
        }

        float pt = (now - pumpStart) / pumpTime;
        if (pt >= 0f && pt < 1.2f)
        {
            Cue(ref pumpCues, 1, pt >= PumpBackAt, "pump_back");
            Cue(ref pumpCues, 2, pt >= PumpForwardAt, "pump_forward");
        }

        float rk = 0.3f + 0.7f * (now - rackStart) / RackTime;
        if (rk < 1.2f)
        {
            Cue(ref rackCues, 1, rk >= DrawPull, back);
            if (Cue(ref rackCues, 2, rk >= DrawRelease, forward)) Jerk();
        }

        // the R8 hammer: a click when the pull starts
        bool hammer = weapon.IsHammerPulling;
        if (hammer && !hammerWas) EmitSound("cylinder", currentData);
        hammerWas = hammer;
    }

    static string BackKey(Rack rack)
    {
        if (rack == Rack.Pump) return "pump_back";
        if (rack == Rack.Slide || rack == Rack.Revolver || rack == Rack.Dual) return "slide_back";
        return "bolt_back";
    }

    static string ForwardKey(Rack rack)
    {
        if (rack == Rack.Pump) return "pump_forward";
        if (rack == Rack.Slide || rack == Rack.Revolver || rack == Rack.Dual) return "slide_forward";
        return "bolt_forward";
    }

    // plays a sound once: the bit marks it as played (a null key only marks the moment)
    bool Cue(ref int played, int bit, bool due, string key)
    {
        if (!due || (played & bit) != 0) return false;
        played |= bit;
        if (key != null) EmitSound(key, currentData);
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
        if (currentGrenadeView != null) currentGrenadeView.root.SetActive(false);
        currentGrenadeData = null;

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
        pumpStart = -10f;
        pumpEjected = true;
        rackStart = -10f;
        fanStart = -10f;
        modeStart = -10f;
        reloadFromEmpty = false;
        wasReloading = false;
        magDropped = false;
        shellBlend = 0f;
        aim = 0f;
        cylinderShots = 0;
        cylinderAngle = 0f;
        hammerWas = false;
        idleFlipStart = -10f;
        ScheduleIdleFlip();
        for (int i = 0; i < 2; i++)
        {
            dualKickPos[i] = Vector3.zero;
            dualKickRot[i] = Vector3.zero;
        }

        // a new draw; the sounds of the old weapon's reload, bolt and pump never come
        drawCues = 0;
        reloadCues = ~0;
        boltCues = ~0;
        pumpCues = ~0;
        rackCues = ~0;
        idleCues = ~0;
    }

    // ---------------- dropped magazines ----------------

    // the magazine of this reload fell out and the new one is not back yet
    bool MagGone()
    {
        return magDropped && weapon != null && weapon.IsReloading && weapon.ReloadProgress < MagBackAt;
    }

    // the empty magazine leaves the hand on its way out: a real-size copy falls to the floor (both of the Dual Berettas)
    void DropMagazines()
    {
        Model m = current;
        if (magDropped || m == null || weapon == null || !weapon.IsReloading || magOut < MagReleaseAt) return;
        if (weapon.ReloadProgress >= MagBackAt || (m.mag == null && m.leftMag == null)) return;

        magDropped = true;
        Vector3 down = m.root.transform.TransformDirection(m.magDropRoot.normalized);
        if (m.mag != null) DropPart(m.mag, "mag:" + currentData.weaponName, down);
        if (m.leftMag != null) DropPart(m.leftMag, "mag:" + currentData.weaponName + ":left", down);
    }

    // A part of the first person model falls into the world. The view model is the world made smaller round the eye,
    // so the copy is put at the same place on the screen made bigger round the eye: it is at real size, it does not
    // jump, and everybody sees it on the floor.
    void DropPart(Transform part, string key, Vector3 down)
    {
        float k = ViewScale();
        if (k <= 1e-4f) return;
        Vector3 eye = transform.position;
        WorldPose(part, out Quaternion rot, out Vector3 scale);
        Vector3 pos = eye + (part.position - eye) / k;
        Vector3 velocity = down * 1.2f + ownerVelocity + Random.insideUnitSphere * 0.15f;
        if (CombatEffects.DropPiece(part.gameObject, key, pos, rot, scale / k, velocity, Random.insideUnitSphere * 150f, eye))
            part.gameObject.SetActive(false);
    }

    // how much smaller than real the first person model is (the models are in meters at scale 1 under the root)
    float ViewScale()
    {
        return current != null ? Mathf.Abs(current.root.transform.lossyScale.x) : 0f;
    }

    // the world rotation and scale of a transform (for a root copy that looks the same), right also under a mirrored
    // or rotated and scaled parent (the left Dual Beretta, the parts of an imported model)
    public static void WorldPose(Transform t, out Quaternion rotation, out Vector3 scale)
    {
        Vector3 x = t.TransformVector(Vector3.right), y = t.TransformVector(Vector3.up), z = t.TransformVector(Vector3.forward);
        if (y.sqrMagnitude < 1e-12f || z.sqrMagnitude < 1e-12f)
        {
            rotation = t.rotation;
            scale = t.lossyScale;
            return;
        }
        rotation = Quaternion.LookRotation(z, y);
        float sign = Vector3.Dot(x, Vector3.Cross(y, z)) < 0f ? -1f : 1f;
        scale = new Vector3(sign * x.magnitude, y.magnitude, z.magnitude);
    }

    // ---------------- shells ----------------

    // shotgun hulls and the cases of the R8 fall to the floor and lie there a second (CombatEffects);
    // the brass of the other guns flies out and is gone in the air
    bool LandingShells()
    {
        return currentData != null && current != null && (currentData.IsShotgun || current.rack == Rack.Revolver);
    }

    // a real-size shell in the world at the screen place of this first person point (see DropPart)
    bool DropWorldShell(Vector3 point, Quaternion rotation, Vector3 velocity)
    {
        float k = ViewScale();
        if (k <= 1e-4f) return false;
        Vector3 eye = transform.position;
        return CombatEffects.DropShell(currentData, eye + (point - eye) / k, rotation, velocity + ownerVelocity,
                                       Random.insideUnitSphere * 720f, eye);
    }

    // the empty shell flies to the right (from the left pistol of the Dual Berettas to the left)
    void EjectShell(bool leftGun)
    {
        if (current == null || shells.Count == 0) return;
        Transform from = leftGun && current.leftEject != null ? current.leftEject : current.eject;
        if (from == null) return;

        Transform cam = transform;
        float side = leftGun ? -1f : 1f;
        Quaternion rot = cam.rotation * Quaternion.Euler(0f, 0f, 90f);
        if (LandingShells())
        {
            Vector3 v = cam.right * side * Random.Range(1.4f, 2.2f) + cam.up * Random.Range(1.1f, 1.7f) - cam.forward * Random.Range(0f, 0.4f);
            if (DropWorldShell(from.position, rot, v)) return;
        }

        Shell s = shells[nextShell];
        nextShell = (nextShell + 1) % shells.Count;

        s.t.gameObject.SetActive(true);
        s.t.position = from.position;
        s.t.rotation = rot;
        s.velocity = cam.right * side * Random.Range(1.2f, 1.8f) + cam.up * Random.Range(0.8f, 1.3f)
                     - cam.forward * Random.Range(0f, 0.3f)
                     + ownerVelocity;
        s.spin = Random.insideUnitSphere * 900f;
        s.until = Time.time + shellLifetime;
    }

    // empty shells fall out of the gun at a point of the weapon root (R8 cylinder, Sawed-Off breech)
    void DropShells(Vector3 point, int count, bool down)
    {
        if (current == null || shells.Count == 0) return;
        Transform r = current.root.transform;
        Transform cam = transform;
        bool landing = LandingShells();
        for (int i = 0; i < count; i++)
        {
            Vector3 pos = r.TransformPoint(point + Random.insideUnitSphere * 0.012f);
            Quaternion rot = cam.rotation * Quaternion.Euler(90f, 0f, 0f);
            Vector3 v = (down ? -cam.up * Random.Range(0.4f, 0.9f) : cam.up * Random.Range(0.6f, 1f) - cam.forward * 0.5f)
                        + cam.right * Random.Range(-0.3f, 0.3f);
            if (landing && DropWorldShell(pos, rot, v)) continue;

            Shell s = shells[nextShell];
            nextShell = (nextShell + 1) % shells.Count;
            s.t.gameObject.SetActive(true);
            s.t.position = pos;
            s.t.rotation = rot;
            s.velocity = v + ownerVelocity;
            s.spin = Random.insideUnitSphere * 400f;
            s.until = Time.time + shellLifetime;
        }
    }

    // the shells of the view that was shown before (another character) are gone
    void HideShells()
    {
        foreach (Shell s in shells)
            if (s.t != null && s.t.gameObject.activeSelf) s.t.gameObject.SetActive(false);
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
        m.shellReload = d.shellReload;
        m.breakAction = d.IsShotgun && m.rack == Rack.None;
        m.boltAction = d.boltAction;
        m.hold = HoldFor(d);
        if (Grips.TryGetValue(d.weaponName, out GripRow row)) m.hold = row.hold;
        if (m.hold == Hold.Pistol) m.restOffset = pistolOffset;
        if (d.dual) m.restOffset = dualOffset;

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
            m.boltAction = d.boltAction;
            m.shellPort = new Vector3(0f, -0.04f, 0.15f);
            BuildHands(m, r, d);
        }

        if (m.muzzle == null) m.muzzle = Point(r, "Muzzle", new Vector3(0f, 0f, 0.3f));
        if (m.mag != null)
        {
            m.magRest = m.mag.localPosition;
            // the drop is "down" in the weapon's space, whatever the scale of an imported model is
            m.magDrop = m.mag.parent.InverseTransformVector(r.TransformVector(m.magDropRoot));
        }
        if (m.leftMag != null)
        {
            m.leftMagRest = m.leftMag.localPosition;
            m.leftMagDrop = m.leftMag.parent.InverseTransformVector(r.TransformVector(m.magDropRoot));
        }
        if (m.slide != null)
        {
            m.slideRest = m.slide.localPosition;
            m.slideRestRot = m.slide.localRotation;
            // the same for the slide: "back" and the bolt axis of the weapon in its parent's space
            m.slideBack = m.slide.parent.InverseTransformVector(r.TransformVector(Vector3.back));
            m.slideAxis = m.slide.parent.InverseTransformDirection(r.TransformDirection(Vector3.forward));
        }
        if (m.pump != null)
        {
            m.pumpRest = m.pump.localPosition;
            m.pumpBack = m.pump.parent.InverseTransformVector(r.TransformVector(Vector3.back));
        }
        if (m.cylinder != null)
        {
            m.cylinderRest = m.cylinder.localPosition;
            m.cylinderRestRot = m.cylinder.localRotation;
            m.cylinderAxis = m.cylinder.parent.InverseTransformDirection(r.TransformDirection(Vector3.forward));
            m.cylinderOut = m.cylinder.parent.InverseTransformVector(r.TransformVector(CylinderSwing));
            m.cylinderCenter = TryBoundsIn(m.cylinder, r, out Bounds cb) ? cb.center : r.InverseTransformPoint(m.cylinder.position);
        }
        else
        {
            m.cylinderCenter = m.handle + new Vector3(0f, -0.02f, 0.07f);
        }
        if (m.spin != null)
        {
            m.spinRestPos = m.spin.localPosition;
            m.spinRestRot = m.spin.localRotation;
            m.spinAxis = Quaternion.Euler(KnifeTilt) * Vector3.forward;   // along the blade
        }
        if (m.shellReload && m.leftArm != null && m.leftArm.hand != null) m.shellInHand = BuildShellInHand(m.leftArm.hand);
        return m;
    }

    // a pump-action shotgun (Nova, MAG-7): it pumps after every shot; the Sawed-Off is break-action, the XM1014 semi-auto.
    // GameAudio uses it too, so the sounds match the animation
    public static bool IsPumpAction(WeaponData d)
    {
        return d != null && RackFor(d) == Rack.Pump;
    }

    // a break-action shotgun (Sawed-Off): its empty hulls drop out when it opens for a reload, not after a shot
    public static bool IsBreakAction(WeaponData d)
    {
        return d != null && d.IsShotgun && RackFor(d) == Rack.None;
    }

    // the draw and reload style: by the weapon's name, else by its data
    static Rack RackFor(WeaponData d)
    {
        switch (d.weaponName)
        {
            case "AK-47":
            case "Galil AR":
            case "PP-Bizon":
            case "Negev":
            case "XM1014": return Rack.SideHandle;
            case "M4A4":
            case "M4A1-S":
            case "SG 553":
            case "M249":
            case "FAMAS":
            case "MP9":
            case "MP7":
            case "MAC-10": return Rack.RearHandle;
            case "UMP-45":
            case "MP5-SD":
            case "P90":
            case "AUG":
            case "SCAR-20":
            case "G3SG1": return Rack.FrontHandle;
            case "AWP":
            case "SSG 08": return Rack.Bolt;
            case "Nova":
            case "MAG-7": return Rack.Pump;
            // the Sawed-Off is a break-action gun: no pump, only the recoil and the reload pose
            case "Sawed-Off":
            case "Zeus x27": return Rack.None;
            case "R8 Revolver": return Rack.Revolver;
            case "Dual Berettas": return Rack.Dual;
            case "Glock-18":
            case "USP-S":
            case "Desert Eagle":
            case "P2000":
            case "P250":
            case "CZ75-Auto":
            case "Five-SeveN":
            case "Tec-9": return Rack.Slide;
            case "Knife": return Rack.Flip;
        }
        if (d.melee || d.category == WeaponCategory.Melee) return Rack.Flip;
        if (d.dual) return Rack.Dual;
        if (d.boltAction) return Rack.Bolt;
        if (d.taser || d.category == WeaponCategory.Gear) return Rack.None;
        if (d.category == WeaponCategory.Pistol) return Rack.Slide;
        if (d.category == WeaponCategory.Shotgun) return d.automatic || d.fireRate >= 2f ? Rack.SideHandle : Rack.Pump;
        return Rack.RearHandle;
    }

    // how the left hand holds a weapon that is not in the Grips table
    static Hold HoldFor(WeaponData d)
    {
        if (d.melee || d.category == WeaponCategory.Melee) return Hold.None;
        if (d.category == WeaponCategory.Pistol || d.category == WeaponCategory.Gear) return Hold.Pistol;
        if (d.category == WeaponCategory.SMG) return Hold.FrontGrip;
        return Hold.Handguard;
    }

    // your own model (WeaponData.viewModelPrefab). Its children are found by name:
    // Muzzle (flash, tracers), Eject (shells), Mag (moves on reload), Slide or Bolt (moves on a shot),
    // Pump (shotguns), Cylinder (revolver), HandleA / HandleB (butterfly knife), Hands (if there is none, simple hands are added)
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

    // a copy of the model under the parent, its barrel at the same height as on the models made of shapes
    static Transform SpawnGun(GameObject prefab, Transform parent, Transform r)
    {
        Transform t = SpawnModel(prefab, parent, false).transform;
        Transform muzzle = FindChild(t, "Muzzle");
        if (muzzle != null)
        {
            Vector3 mz = r.InverseTransformPoint(muzzle.position);
            t.localPosition += parent.InverseTransformVector(r.TransformVector(new Vector3(0f, 0.03f - mz.y, 0f)));
        }
        return t;
    }

    void UsePrefab(Model m, Transform r, WeaponData d, GameObject prefab)
    {
        Transform t;
        if (d.dual)
        {
            // a pistol in each hand: the left one is a mirrored copy
            m.dual = true;
            m.rightGunRest = Vector3.zero;
            m.leftGunRest = new Vector3(-dualSpread / modelScale, 0f, 0f);
            m.rightGun = Point(r, "RightGun", m.rightGunRest);
            m.leftGun = Point(r, "LeftGun", m.leftGunRest);
            t = SpawnGun(prefab, m.rightGun, r);
            Transform l = SpawnGun(prefab, m.leftGun, r);
            m.leftGun.localScale = Mirror;
            m.leftMuzzle = FindChild(l, "Muzzle");
            m.leftMag = FindChild(l, "Mag");
            Transform le = FindChild(l, "Eject");
            if (le != null)
            {
                // the empty shells of the left pistol fly out on its outer side
                Vector3 e = r.InverseTransformPoint(le.position);
                m.leftEject = Point(m.leftGun, "EjectOut", Vector3.Scale(m.leftGun.InverseTransformPoint(le.position), new Vector3(0f, 1f, 1f))
                                                           + new Vector3(Mathf.Abs(e.x - m.leftGunRest.x), 0f, 0f));
            }
            Transform grip = FindChild(t, "Grip");
            m.dualPalm = (grip != null ? m.rightGun.InverseTransformPoint(grip.position) : Vector3.zero) + new Vector3(0.006f, 0.005f, -0.005f);
            m.rightGun.localRotation = Quaternion.Euler(0f, -DualToe, 0f);
            m.leftGun.localRotation = Quaternion.Euler(0f, DualToe, 0f);
        }
        else if (d.melee)
        {
            t = SpawnModel(prefab, r, false).transform;
        }
        else
        {
            t = SpawnGun(prefab, r, r);
        }

        m.muzzle = FindChild(t, "Muzzle");
        m.eject = FindChild(t, "Eject");
        m.mag = FindChild(t, "Mag");
        m.slide = FindChild(t, "Slide");
        if (m.slide == null) m.slide = FindChild(t, "Bolt");
        m.pump = FindChild(t, "Pump");
        m.cylinder = FindChild(t, "Cylinder");
        m.slideTravel = m.boltAction ? 0.07f : m.hold == Hold.Pistol ? 0.035f : 0.05f;

        // the models made in Blender have their Eject point on the left side: the shells come out on the right
        if (m.eject != null)
        {
            Vector3 e = r.InverseTransformPoint(m.eject.position);
            if (e.x < m.rightGunRest.x) m.eject = Point(r, "EjectRight", new Vector3(2f * m.rightGunRest.x - e.x, e.y, e.z));
        }

        if (d.melee)
        {
            // the blade points forward and up, like the knife made of shapes
            Quaternion tilt = Quaternion.Euler(KnifeTilt);
            t.localPosition = tilt * t.localPosition;
            t.localRotation = tilt * t.localRotation;
            m.spin = t;
            SetUpButterfly(m, r, t);
        }

        // an optic: the lenses are see-through, so the zoomed view goes through it
        if (d.scopeIsOptic)
        {
            Material lens = LensMaterial();
            foreach (Renderer rd in t.GetComponentsInChildren<Renderer>(true))
            {
                Material[] mats = rd.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null && mats[i].name.ToLowerInvariant().Contains("glass"))
                    {
                        mats[i] = lens;
                        changed = true;
                    }
                }
                if (changed) rd.sharedMaterials = mats;
            }
        }

        if (FindChild(t, "Hands") == null)
        {
            ModelGrips(m, r, d, FindChild(t, "Grip"));
            BuildHands(m, r, d);
        }
    }

    // The butterfly knife (HandleA, HandleB with their origins on the pins): the axis of the pins is across the blade,
    // the normal of the plane through Grip, Up and Muzzle. The safe handle (A) swings over the spine (towards Up),
    // the bite handle (B) the other way; 180 degrees close each of them over the blade.
    static void SetUpButterfly(Model m, Transform r, Transform t)
    {
        Transform a = FindChild(t, "HandleA"), b = FindChild(t, "HandleB");
        Transform grip = FindChild(t, "Grip"), up = FindChild(t, "Up"), muzzle = FindChild(t, "Muzzle");
        if (a == null || b == null || grip == null || up == null || muzzle == null) return;

        Vector3 upWorld = up.position - grip.position;
        Vector3 axisWorld = Vector3.Cross(muzzle.position - grip.position, upWorld);
        if (axisWorld.sqrMagnitude < 1e-10f) return;

        m.handleA = a;
        m.handleB = b;
        m.handleARest = a.localRotation;
        m.handleBRest = b.localRotation;
        m.axisA = a.parent.InverseTransformDirection(axisWorld).normalized;
        m.axisB = b.parent.InverseTransformDirection(axisWorld).normalized;
        m.pinAxis = r.InverseTransformDirection(axisWorld).normalized;
        m.pinA = r.InverseTransformPoint(a.position);

        // which way round each handle turns: the middle of the handle at 90 degrees is above (A) or below (B) its pin
        Vector3 upRoot = r.InverseTransformDirection(upWorld);
        m.signA = TurnSign(a, r, m.pinAxis, upRoot);
        m.signB = -TurnSign(b, r, m.pinAxis, upRoot);
    }

    // +1 if turning the handle by +90 degrees round the axis moves its middle towards "up"
    static float TurnSign(Transform handle, Transform r, Vector3 axis, Vector3 up)
    {
        Vector3 pin = r.InverseTransformPoint(handle.position);
        Vector3 middle = TryBoundsIn(handle, r, out Bounds b) ? b.center : pin + Vector3.back * 0.05f;
        Vector3 turned = Quaternion.AngleAxis(90f, axis) * (middle - pin);
        return Vector3.Dot(turned, up) >= 0f ? 1f : -1f;
    }

    // Where the hands hold an imported model. The Grip, the Muzzle and the Mag, Slide or Pump parts are found in the model,
    // the rest comes from the Grips table (x right, y up, z forward, meters from the Grip).
    void ModelGrips(Model m, Transform r, WeaponData d, Transform grip)
    {
        Vector3 g = grip != null ? r.InverseTransformPoint(grip.position) : Vector3.zero;
        m.rightPalm = g;
        if (m.rack == Rack.Flip) return;

        Vector3 mz = m.muzzle != null ? r.InverseTransformPoint(m.muzzle.position) : g + new Vector3(0f, 0.08f, 0.5f);
        float high = mz.y - g.y;       // the barrel above the grip
        float length = mz.z - g.z;     // the muzzle in front of the grip

        Vector3 right, left, handle, mag;
        float pull;
        if (Grips.TryGetValue(d.weaponName, out GripRow row))
        {
            right = row.right;
            left = row.left;
            handle = row.handle;
            pull = row.pull;
            mag = m.hold == Hold.Pistol ? new Vector3(0f, -0.075f, -0.012f) : new Vector3(0f, -0.15f, length * 0.28f);
        }
        else if (m.hold == Hold.Pistol)
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
            right = new Vector3(0.004f, 0.008f, 0f);
            left = new Vector3(-0.006f, high * 0.45f, length * 0.5f);
            handle = new Vector3(0f, high + 0.035f, 0f);
            mag = new Vector3(0f, -0.15f, length * 0.28f);
            pull = 0.05f;
        }

        m.rightPalm = g + right;
        m.leftPalm = g + left;
        m.handle = g + handle;
        m.magGrab = g + mag;
        m.pull = pull;

        // separate parts tell exactly where the magazine bottom, the rear of the slide and the pump are
        if (m.mag != null && TryBoundsIn(m.mag, r, out Bounds mb))
        {
            if (mb.center.y > g.y + high * 0.6f)
            {
                // a magazine on top (P90): the hand takes it from above and lifts it off backwards
                m.magGrab = new Vector3(mb.center.x, mb.max.y + 0.022f, mb.center.z);
                m.magDropRoot = MagLift;
            }
            else
            {
                m.magGrab = new Vector3(mb.center.x, mb.min.y - 0.022f, mb.center.z);
            }
        }
        if (m.slide != null && m.rack == Rack.Slide && TryBoundsIn(m.slide, r, out Bounds sb))
        {
            m.handle = new Vector3(sb.center.x, sb.center.y, sb.min.z + 0.02f);
            m.pull = m.slideTravel;
        }
        if (m.pump != null && TryBoundsIn(m.pump, r, out Bounds pb))
        {
            // the palm under the pump (or the fore-end of the Sawed-Off); the pump is also the "handle" of a pump gun
            m.leftPalm = new Vector3(pb.center.x - 0.006f, pb.min.y - 0.019f, pb.center.z);
            m.handle = m.leftPalm;
            if (m.pull <= 0f) m.pull = 0.06f;
        }

        // where the shells go in: under the receiver in front of the trigger guard
        m.shellPort = g + ShellPortOf(d.weaponName, length);

        // the optic: its rear lens, measured on the models
        m.optic = d.scopeIsOptic;
        m.sight = g + SightOf(d.weaponName, high);
    }

    static Vector3 ShellPortOf(string weaponName, float length)
    {
        switch (weaponName)
        {
            case "Nova": return new Vector3(0f, -0.01f, 0.13f);
            case "XM1014": return new Vector3(0f, -0.01f, 0.15f);
            case "Sawed-Off": return new Vector3(0f, 0.005f, 0.15f);
        }
        return new Vector3(0f, -0.01f, length * 0.2f);
    }

    static Vector3 SightOf(string weaponName, float high)
    {
        switch (weaponName)
        {
            case "SG 553": return new Vector3(0f, 0.1264f, 0.021f);
            case "AUG": return new Vector3(0f, 0.1746f, 0.0242f);
        }
        return new Vector3(0f, high + 0.05f, 0.02f);
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

        if (m.dual)
        {
            // each hand holds its own pistol, the left arm is the mirror of the right one
            m.rightPalm = m.rightGunRest + Quaternion.Euler(0f, -DualToe, 0f) * m.dualPalm;
            m.leftPalm = m.leftGunRest + Quaternion.Euler(0f, DualToe, 0f) * Vector3.Scale(m.dualPalm, Mirror);
            m.rightArm = BuildArm(r, m.rightPalm, RightElbow, new Vector3(0.05f, 0.08f, 0.075f), new Vector3(15f, 0f, 0f),
                                  new Vector3(-0.026f, 0.03f, 0.012f), new Vector3(0.014f, 0.018f, 0.05f));
            m.leftArm = BuildArm(r, m.leftPalm, Vector3.Scale(RightElbow, Mirror), new Vector3(0.05f, 0.08f, 0.075f), new Vector3(15f, 0f, 0f),
                                 new Vector3(0.026f, 0.03f, 0.012f), new Vector3(0.014f, 0.018f, 0.05f));
            return;
        }

        m.rightArm = BuildArm(r, m.rightPalm, RightElbow, new Vector3(0.05f, 0.08f, 0.075f), new Vector3(15f, 0f, 0f),
                              new Vector3(-0.026f, 0.03f, 0.012f), new Vector3(0.014f, 0.018f, 0.05f));

        if (m.hold == Hold.Pistol)
            m.leftArm = BuildArm(r, m.leftPalm, LeftElbowPistol, new Vector3(0.045f, 0.065f, 0.075f), new Vector3(15f, 10f, -15f),
                                 Vector3.zero, Vector3.zero);
        else if (m.hold == Hold.FrontGrip)   // the hand around a vertical front grip
            m.leftArm = BuildArm(r, m.leftPalm, LeftElbow, new Vector3(0.05f, 0.075f, 0.055f), new Vector3(-8f, 0f, 0f),
                                 new Vector3(-0.026f, 0.03f, -0.01f), new Vector3(0.014f, 0.018f, 0.04f));
        else                                  // the palm under the handguard, the thumb along its side
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
        a.hand = hand;
        return a;
    }

    // a shotgun shell (red with a brass base) on top of the left palm, along the barrel
    Transform BuildShellInHand(Transform hand)
    {
        Transform s = Point(hand, "ShellInHand", new Vector3(0f, 0.033f, 0.01f));
        Tube(s, Vector3.zero, 0.0105f, 0.06f, Mat("ShellRed", new Color(0.6f, 0.08f, 0.06f), 0f, 0.4f));
        Tube(s, new Vector3(0f, 0f, -0.034f), 0.011f, 0.012f, Mat("Brass", new Color(0.8f, 0.6f, 0.25f), 1f, 0.7f));
        s.gameObject.SetActive(false);
        return s;
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

    // the lenses of an optic: a faint see-through tint
    Material LensMaterial()
    {
        if (lensMaterial != null) return lensMaterial;
        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        lensMaterial = new Material(shader) { name = "Lens" };
        lensMaterial.color = new Color(0.55f, 0.8f, 0.9f, 0.12f);
        return lensMaterial;
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
        flashMaterial = new Material(Shader.Find("Sprites/Default"));
        flashMaterial.mainTexture = MakeFlashTexture(64);
        flashMaterial.color = GunFlash;
        Material m = flashMaterial;

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

    // ---------------- grenades first person view ----------------

    void RenderGrenadeView(float dt)
    {
        GrenadeData gd = grenades.State == PlayerGrenades.ThrowState.Throwing && grenades.ThrowingData != null
            ? grenades.ThrowingData : grenades.Selected;
        if (gd == null) return;

        if (gd != currentGrenadeData)
        {
            if (currentGrenadeView != null) currentGrenadeView.root.SetActive(false);
            currentGrenadeData = gd;
            if (!grenadeViews.TryGetValue(gd, out currentGrenadeView))
            {
                currentGrenadeView = BuildGrenadeView(gd);
                grenadeViews[gd] = currentGrenadeView;
            }
            currentGrenadeView.root.SetActive(true);
            grenadeDrawStart = Time.time;
        }
        else if (currentGrenadeView != null && !currentGrenadeView.root.activeSelf)
        {
            currentGrenadeView.root.SetActive(true);
            grenadeDrawStart = Time.time;
        }

        GrenadeView gv = currentGrenadeView;
        if (gv == null) return;

        posePos = hipPosition + new Vector3(0.02f, -0.02f, 0.02f);
        poseRot = Vector3.zero;
        Sway(dt);

        // Draw raise animation
        float drawU = Mathf.Clamp01((Time.time - grenadeDrawStart) / Mathf.Max(0.15f, gd.drawTime));
        float drawEase = Ease(drawU);
        posePos += new Vector3(0f, -0.20f, -0.04f) * (1f - drawEase);
        poseRot += new Vector3(38f, -8f, 0f) * (1f - drawEase);

        Vector3 rHand = gv.rightRest;
        Vector3 lHand = gv.leftRest;
        Vector3 lElbowLift = Vector3.zero;
        bool showModel = true;
        bool pinPulled = false;
        Vector3 pinOffset = Vector3.zero;
        bool molotovLit = false;

        PlayerGrenades.ThrowState st = grenades.State;
        float stTime = grenades.StateTime;

        if (st == PlayerGrenades.ThrowState.Pulling)
        {
            float p = Mathf.Clamp01(stTime / Mathf.Max(0.05f, gd.pinPullTime));
            float pullBack = Mathf.SmoothStep(0f, 1f, p);
            lHand = Vector3.Lerp(gv.leftRest, gv.pinGrab, Mathf.Clamp01(p * 1.5f));
            lHand += new Vector3(-0.07f, 0.02f, -0.03f) * Mathf.Max(0f, p - 0.35f) * 1.6f;
            lElbowLift = new Vector3(0f, 0.04f, 0f) * pullBack;
            pinOffset = new Vector3(-0.04f, 0.02f, -0.02f) * pullBack;
            poseRot += new Vector3(4f, 8f, -6f) * pullBack;
            molotovLit = p > 0.4f;
        }
        else if (st == PlayerGrenades.ThrowState.Holding)
        {
            pinPulled = true;
            molotovLit = true;
            ThrowStrength str = grenades.CurrentStrength;
            if (str == ThrowStrength.High)
            {
                posePos += new Vector3(0.05f, 0.06f, -0.07f);
                poseRot += new Vector3(-22f, 14f, -12f);
                lHand = gv.leftRest + new Vector3(-0.04f, -0.04f, 0.04f);
            }
            else if (str == ThrowStrength.Mid)
            {
                posePos += new Vector3(0.02f, 0.01f, -0.05f);
                poseRot += new Vector3(-10f, 6f, -5f);
                lHand = gv.leftRest + new Vector3(-0.03f, -0.02f, 0.02f);
            }
            else
            {
                posePos += new Vector3(-0.02f, -0.05f, -0.03f);
                poseRot += new Vector3(14f, -4f, 6f);
                lHand = gv.leftRest + new Vector3(-0.02f, -0.05f, 0.01f);
            }
        }
        else if (st == PlayerGrenades.ThrowState.Throwing)
        {
            pinPulled = true;
            showModel = false;
            float p = Mathf.Clamp01(stTime / 0.22f);
            float swing = Mathf.Sin(p * Mathf.PI);
            posePos += new Vector3(-0.05f, -0.02f, 0.16f) * swing;
            poseRot += new Vector3(28f, -16f, -18f) * swing;
            lHand = gv.leftRest + new Vector3(-0.05f, -0.06f, 0f) * swing;
        }

        if (gv.modelHolder != null && gv.modelHolder.gameObject.activeSelf != showModel)
            gv.modelHolder.gameObject.SetActive(showModel);

        if (gv.pin != null)
        {
            if (gv.pin.gameObject.activeSelf == pinPulled) gv.pin.gameObject.SetActive(!pinPulled);
            gv.pin.localPosition = gv.pinRest + pinOffset;
        }

        if (gv.wickFlame != null && gv.wickFlame.activeSelf != (molotovLit && showModel))
            gv.wickFlame.SetActive(molotovLit && showModel);

        if (gv.leftArm != null) Reach(gv.leftArm, lHand, lElbowLift);
        if (gv.rightArm != null) Reach(gv.rightArm, rHand, Vector3.zero);

        pivot.localPosition = posePos;
        pivot.localRotation = Quaternion.Euler(poseRot);
    }

    GrenadeView BuildGrenadeView(GrenadeData gd)
    {
        GrenadeView gv = new GrenadeView();
        gv.root = new GameObject("GrenadeView_" + gd.grenadeName);
        Transform r = gv.root.transform;
        r.SetParent(pivot, false);
        r.localScale = Vector3.one * modelScale;

        Transform holder = new GameObject("ModelHolder").transform;
        holder.SetParent(r, false);
        gv.modelHolder = holder;

        GameObject prefab = gd.worldPrefab != null ? gd.worldPrefab : Resources.Load<GameObject>("GrenadeModels/" + gd.grenadeName);
        if (prefab != null)
        {
            GameObject model = SpawnModel(prefab, holder, false);
            model.transform.localScale = Vector3.one * 1.55f;
            gv.pin = FindChild(model.transform, "Pin");
            if (gv.pin != null)
            {
                gv.pinRest = gv.pin.localPosition;
                gv.pinGrab = r.InverseTransformPoint(gv.pin.position);
            }
            Transform wick = FindChild(model.transform, "Wick");
            if (wick != null)
            {
                GameObject flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                DestroyImmediate(flame.GetComponent<Collider>());
                flame.name = "WickFlame";
                flame.transform.SetParent(wick, false);
                flame.transform.localPosition = Vector3.zero;
                flame.transform.localScale = Vector3.one * 0.025f;
                Material fm = new Material(Shader.Find("Sprites/Default")) { color = new Color(1f, 0.62f, 0.12f, 0.95f) };
                flame.GetComponent<MeshRenderer>().sharedMaterial = fm;
                flame.SetActive(false);
                gv.wickFlame = flame;
                if (gv.pinGrab == Vector3.zero) gv.pinGrab = r.InverseTransformPoint(wick.position);
            }
        }
        else
        {
            Box(holder, Vector3.zero, new Vector3(0.06f, 0.1f, 0.06f), Metal());
        }

        gv.rightRest = new Vector3(0.008f, -0.02f, -0.01f);
        gv.leftRest = new Vector3(-0.16f, -0.06f, 0.08f);
        if (gv.pinGrab == Vector3.zero) gv.pinGrab = new Vector3(-0.02f, 0.07f, 0.01f);

        gv.rightArm = BuildArm(r, gv.rightRest, RightElbow, new Vector3(0.055f, 0.065f, 0.085f), new Vector3(12f, 0f, -10f),
                               new Vector3(-0.026f, 0.025f, 0.01f), new Vector3(0.014f, 0.018f, 0.045f));
        gv.leftArm = BuildArm(r, gv.leftRest, LeftElbowPistol, new Vector3(0.052f, 0.06f, 0.08f), new Vector3(10f, 15f, -20f),
                              new Vector3(0.024f, 0.025f, 0.01f), new Vector3(0.014f, 0.018f, 0.045f));

        gv.root.SetActive(false);
        return gv;
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
