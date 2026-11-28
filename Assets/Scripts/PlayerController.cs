using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public Transform cameraTransform;
    public float moveSpeed = 5f;
    public float jumpHeight = 1.2f;
    public float gravity = -20f;
    [Tooltip("not used any more: the sensitivity comes from GameSettings (Esc → Настройки)")]
    public float mouseSensitivity = 2f;

    [Header("Crouch")]
    public KeyCode crouchKey = KeyCode.C;
    public float standHeight = 2f;
    public float crouchHeight = 1.2f;
    public float crouchSpeedFactor = 0.5f;
    public float heightChangeSpeed = 6f;
    public float standCameraY = 0.6f;
    public float crouchCameraY = 0.05f;

    [Header("Sneak (Shift)")]
    public KeyCode sneakKey = KeyCode.LeftShift;
    public float sneakSpeedFactor = 0.5f;

    [Header("Camera bob")]
    public float bobFrequency = 9f;
    public float bobHeight = 0.04f;
    public float bobSide = 0.025f;
    public float bobSmoothing = 10f;

    // The accuracy (stance, movement, shots) and the recoil pattern are in Weapon (data from WeaponData).
    // The bullets go along AimRotation + the weapon's aim punch; the camera shows only part of the punch (like CS 2),
    // so the bullets of a spray land a little above the crosshair.
    [Header("Recoil view")]
    [Tooltip("part of the weapon's aim punch the camera shows (CS 2: about half); the bullets follow the whole punch")]
    [Range(0f, 1f)] public float viewPunchScale = 0.5f;
    [Tooltip("1/s: how fast the camera follows the punch (smooths the jumps of single shots)")]
    public float viewPunchFollow = 25f;
    [Tooltip("a short extra kick of the camera on every shot, degrees x the weapon's recoil; it does not move the bullets")]
    public float viewKick = 0.15f;
    [Tooltip("1/s: how fast the extra kick goes back")]
    public float viewKickReturn = 20f;

    [Header("Noclip (developer console)")]
    [Tooltip("metres per second of the noclip flight")]
    public float noclipSpeed = 8f;
    [Tooltip("x speed with Shift held")]
    public float noclipFastFactor = 3f;

    CharacterController controller;
    Health health;
    PlayerShoot shoot;
    PlayerLoadout loadout;
    Bhop bhop;
    Weapon weapon;
    float verticalVelocity;
    float pitch;
    float currentHeight;
    float bobTimer;
    float bobBlend;
    float moveAmount;
    float moveInaccuracy;
    Vector2 viewPunch;   // the punch the camera shows now (degrees: x up, y right)
    float kickNow;       // the extra kick of the last shots (degrees up)
    bool noclip;
    MapBounds bounds;
    bool boundsSearched;

    // developer console "noclip": the player flies along the view through walls (WASD, Space up, C / Ctrl down,
    // Shift faster), no gravity; the CharacterController is off meanwhile, so nothing stops him. He stays inside MapBounds.
    public bool Noclip
    {
        get => noclip;
        set
        {
            if (noclip == value) return;
            noclip = value;
            verticalVelocity = 0f;
            if (controller == null) controller = GetComponent<CharacterController>();
            if (health == null) health = GetComponent<Health>();
            // a dead player's capsule stays off (Revive / the round start turn it on)
            if (controller != null) controller.enabled = !value && (health == null || !health.IsDead);
        }
    }

    // 0 = standing or creeping, 1 = running, above 1 = in the air
    public float MoveInaccuracy => moveInaccuracy;

    public bool IsGrounded => controller != null && controller.isGrounded;
    public Vector3 Velocity => controller != null ? controller.velocity : Vector3.zero;

    public bool IsCrouching => StandFactor < 0.5f;
    public bool IsSneaking { get; private set; }
    public bool IsMoving => moveAmount > 0.1f;

    // for the future sound system: true when the player makes no noise
    public bool IsSilent => controller != null && controller.isGrounded && (!IsMoving || IsSneaking || IsCrouching);

    // 0 = crouched, 1 = standing (the weapon's inaccuracy goes from crouchInaccuracy to standInaccuracy with it)
    public float StandFactor => Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);

    // where the eyes look, without any recoil: the body's turn and the pitch of the mouse.
    // The shots go along it plus the weapon's aim punch (Weapon.FireShot); the camera may show less of the punch and
    // an extra kick, and Spectator moves the camera to another player after death, so it is not taken from the camera
    public Quaternion AimRotation => transform.rotation * Quaternion.Euler(pitch, 0f, 0f);

    void Awake()
    {
        // saved settings (sensitivity and the rest) before the first frame
        GameSettings.Load();
    }

    void OnEnable()
    {
        Weapon.AnyShot += OnShot;
    }

    void OnDisable()
    {
        Weapon.AnyShot -= OnShot;
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        health = GetComponent<Health>();
        shoot = GetComponent<PlayerShoot>();
        loadout = GetComponent<PlayerLoadout>();
        weapon = shoot != null ? shoot.weapon : GetComponentInChildren<Weapon>();
        currentHeight = standHeight;

        // the main menu keeps the cursor free while it is open
        if (!MainMenu.IsOpen)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void OnShot(ShotInfo shot)
    {
        // a shotgun shell kicks once: the other pellets of the shot are skipped
        if (health == null || shot.shooter != health || shot.melee || shot.pellet) return;

        // only a short visual kick here: the spray itself is the weapon's aim punch (Weapon.AimPunch)
        float r = shot.recoil > 0f ? shot.recoil : 1f;
        kickNow = Mathf.Min(kickNow + viewKick * r, 1.5f);
    }

    void Update()
    {
        // Escape and the cursor belong to MainMenu (pause menu); without it Escape frees the cursor and a click takes it back
        // (the buy menu and the developer console keep the cursor free while they are open)
        if (!MainMenu.Active && !BuyMenu.IsOpen && !DevConsole.IsOpen)
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        // paused (a menu is open): no look, no movement, no jump queued for later
        if (MainMenu.IsOpen || Time.timeScale <= 0f) return;

        if (health != null && health.IsDead) return;

        // the developer console is open: the keys and the mouse type into it, the player stands still (gravity goes on)
        bool keys = !DevConsole.IsOpen;

        IsSneaking = keys && !noclip && Input.GetKey(sneakKey) && controller.isGrounded;

        // in noclip C means "down": no crouching then
        UpdateCrouch(keys && !noclip);
        Look(keys);
        if (noclip) Fly(keys);
        else Move(keys);
        UpdateMoveInaccuracy();
    }

    // noclip: along the view, through walls, no gravity; not out of the map (MapBounds kills whoever stays outside)
    void Fly(bool keys)
    {
        // RoundManager turns the controller on again when it places the player at a round start
        if (controller.enabled) controller.enabled = false;
        verticalVelocity = 0f;
        moveAmount = 0f;
        if (!keys) return;

        float x = Input.GetAxisRaw("Horizontal");
        float z = Input.GetAxisRaw("Vertical");
        float y = 0f;
        if (Input.GetButton("Jump")) y += 1f;
        if (Input.GetKey(crouchKey) || Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) y -= 1f;

        Transform view = cameraTransform != null ? cameraTransform : transform;
        Vector3 dir = view.forward * z + view.right * x + Vector3.up * y;
        if (dir.sqrMagnitude > 1f) dir.Normalize();
        bool fast = Input.GetKey(sneakKey) || Input.GetKey(KeyCode.RightShift);
        Vector3 p = transform.position + dir * (noclipSpeed * (fast ? noclipFastFactor : 1f) * Time.deltaTime);

        if (!boundsSearched)
        {
            boundsSearched = true;
            bounds = FindAnyObjectByType<MapBounds>();
        }
        if (bounds != null)
        {
            p.x = Mathf.Clamp(p.x, -bounds.halfSizeX + 0.5f, bounds.halfSizeX - 0.5f);
            p.z = Mathf.Clamp(p.z, -bounds.halfSizeZ + 0.5f, bounds.halfSizeZ - 0.5f);
            p.y = Mathf.Clamp(p.y, bounds.minY + 1f, bounds.maxY - 1f);
        }
        transform.position = p;
    }

    void UpdateCrouch(bool keys)
    {
        float target = keys && Input.GetKey(crouchKey) ? crouchHeight : standHeight;
        currentHeight = Mathf.MoveTowards(currentHeight, target, heightChangeSpeed * Time.deltaTime);

        controller.height = currentHeight;
        controller.center = new Vector3(0f, (currentHeight - standHeight) * 0.5f, 0f);

        float k = StandFactor;
        float footY = -standHeight * 0.5f;
        float eyeHeight = (Mathf.Lerp(crouchCameraY, standCameraY, k) - footY) * CharacterBody.BodyScale;
        float baseY = footY + Mathf.Min(eyeHeight, currentHeight - 0.1f);

        float bobTarget = controller.isGrounded ? moveAmount * SpeedFactor() : 0f;
        bobBlend = Mathf.MoveTowards(bobBlend, bobTarget, bobSmoothing * Time.deltaTime);
        bobTimer += Time.deltaTime * bobFrequency * bobBlend;

        float offsetX = Mathf.Cos(bobTimer) * bobSide * bobBlend;
        float offsetY = Mathf.Sin(bobTimer * 2f) * bobHeight * bobBlend;

        cameraTransform.localPosition = new Vector3(offsetX, baseY + offsetY, 0f);
    }

    float SpeedFactor()
    {
        float f = Mathf.Lerp(crouchSpeedFactor, 1f, StandFactor);
        if (IsSneaking) f *= sneakSpeedFactor;

        if (loadout != null && loadout.InGrenadeSlot && loadout.Grenades != null && loadout.Grenades.Selected != null)
        {
            f *= loadout.Grenades.Selected.moveSpeedFactor;
        }
        else if (weapon != null)
        {
            // slower while firing for some weapons (R8, G3SG1), see Weapon.CurrentMoveSpeedFactor
            f *= weapon.CurrentMoveSpeedFactor;
            if (shoot != null && shoot.IsScoped && weapon.data != null) f *= weapon.data.scopedMoveFactor;
        }

        return f;
    }

    void UpdateMoveInaccuracy()
    {
        Vector3 v = controller.velocity;
        v.y = 0f;
        float ratio = v.magnitude / Mathf.Max(moveSpeed, 0.01f);

        float target = controller.isGrounded ? Mathf.Clamp01(Mathf.InverseLerp(0.25f, 0.8f, ratio)) : 1.5f;
        moveInaccuracy = Mathf.MoveTowards(moveInaccuracy, target, 8f * Time.deltaTime);
    }

    // a new round: the view looks straight ahead again (RoundManager turns the body towards the map centre)
    public void ResetLook()
    {
        pitch = 0f;
        viewPunch = Vector2.zero;
        kickNow = 0f;
        if (weapon != null) weapon.ResetRecoil();
    }

    void Look(bool keys)
    {
        float sens = GameSettings.Sensitivity * (shoot != null ? shoot.SensitivityFactor : 1f);
        // the own sensitivity of the zoom (scopes and optics), on top of the scope's FOV factor
        if (shoot != null && shoot.IsScoped) sens *= GameSettings.ZoomSensitivity;
        float mx = Input.GetAxis("Mouse X") * sens;
        float my = Input.GetAxis("Mouse Y") * sens;

        // locking the cursor after a menu can give one big mouse jump;
        // the mouse moves the cursor of the buy menu (or the console is open), not the view (like in CS)
        if (!keys || BuyMenu.IsOpen || Time.frameCount - MainMenu.LastClosedFrame <= 1)
        {
            mx = 0f;
            my = 0f;
        }

        transform.Rotate(0f, mx, 0f);
        pitch = Mathf.Clamp(pitch - my, -85f, 85f);

        // the camera shows part of the weapon's aim punch (smoothly) and the short kick of the last shots
        Vector2 target = weapon != null ? weapon.AimPunch * viewPunchScale : Vector2.zero;
        float dt = Time.deltaTime;
        viewPunch = Vector2.Lerp(viewPunch, target, 1f - Mathf.Exp(-viewPunchFollow * dt));
        kickNow = Mathf.Lerp(kickNow, 0f, 1f - Mathf.Exp(-viewKickReturn * dt));

        cameraTransform.localRotation = Quaternion.Euler(pitch - viewPunch.x - kickNow, viewPunch.y, 0f);
    }

    void Move(bool keys)
    {
        float x = keys ? Input.GetAxis("Horizontal") : 0f;
        float z = keys ? Input.GetAxis("Vertical") : 0f;

        // freeze time at the round start: no walking and no jumping, looking around and crouching still work
        bool frozen = RoundManager.InFreezeTime;
        if (frozen)
        {
            x = 0f;
            z = 0f;
        }

        Vector3 move = transform.right * x + transform.forward * z;
        if (move.magnitude > 1f) move.Normalize();
        moveAmount = move.magnitude;

        if (bhop == null) bhop = GetComponent<Bhop>();
        bool bhopOn = bhop != null && bhop.Enabled;

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        // with the bhop upgrade holding Jump keeps hopping, and every chained jump adds speed
        bool jumpPressed = keys && !frozen && (bhopOn ? Input.GetButton("Jump") : Input.GetButtonDown("Jump"));

        if (controller.isGrounded && jumpPressed)
        {
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            if (bhopOn) bhop.Jumped();
        }

        if (bhop != null) bhop.Tick(controller.isGrounded, moveAmount > 0.1f);

        float speed = moveSpeed * SpeedFactor() * (bhop != null ? bhop.SpeedMultiplier : 1f);

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move * speed + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }
}
