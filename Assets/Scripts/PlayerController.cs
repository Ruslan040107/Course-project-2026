using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    public Transform cameraTransform;
    public float moveSpeed = 5f;
    public float jumpHeight = 1.2f;
    public float gravity = -20f;
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

    [Header("Spread multipliers (1 = weapon spreadAngle)")]
    public float crouchSpread = 0.4f;
    public float sneakSpread = 0.6f;
    public float standSpread = 0.8f;
    public float runSpread = 1f;
    public float airSpread = 2f;
    public float spreadChangeSpeed = 6f;

    [Header("Camera bob")]
    public float bobFrequency = 9f;
    public float bobHeight = 0.04f;
    public float bobSide = 0.025f;
    public float bobSmoothing = 10f;

    [Header("Recoil (degrees)")]
    public float recoilUp = 0.6f;
    public float recoilSide = 0.25f;
    public float recoilRoll = 0.5f;
    public float recoilMax = 5f;
    public float recoilReturnSpeed = 9f;

    CharacterController controller;
    Health health;
    float verticalVelocity;
    float pitch;
    float currentHeight;
    float bobTimer;
    float bobBlend;
    float moveAmount;
    float spreadNow = 1f;
    float kickPitch;
    float kickYaw;
    float kickRoll;

    public bool IsCrouching => StandFactor < 0.5f;
    public bool IsSneaking { get; private set; }
    public bool IsMoving => moveAmount > 0.1f;

    // for the future sound system: true when the player makes no noise
    public bool IsSilent => controller != null && controller.isGrounded && (!IsMoving || IsSneaking || IsCrouching);

    public float StandFactor => Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);

    // current spread multiplier, the weapon multiplies its spreadAngle by it
    public float SpreadMultiplier => spreadNow;

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
        currentHeight = standHeight;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void OnShot(ShotInfo shot)
    {
        if (health == null || shot.shooter != health) return;

        kickPitch = Mathf.Min(kickPitch + recoilUp * Random.Range(0.8f, 1.2f), recoilMax);
        kickYaw = Mathf.Clamp(kickYaw + Random.Range(-recoilSide, recoilSide), -recoilMax, recoilMax);
        kickRoll = Mathf.Clamp(kickRoll + Random.Range(-recoilRoll, recoilRoll), -recoilMax, recoilMax);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (health != null && health.IsDead) return;

        IsSneaking = Input.GetKey(sneakKey) && controller.isGrounded;

        UpdateCrouch();
        Look();
        Move();
        UpdateSpread();
    }

    void UpdateCrouch()
    {
        float target = Input.GetKey(crouchKey) ? crouchHeight : standHeight;
        currentHeight = Mathf.MoveTowards(currentHeight, target, heightChangeSpeed * Time.deltaTime);

        controller.height = currentHeight;
        controller.center = new Vector3(0f, (currentHeight - standHeight) * 0.5f, 0f);

        float k = StandFactor;
        float baseY = Mathf.Lerp(crouchCameraY, standCameraY, k);

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
        return f;
    }

    void UpdateSpread()
    {
        float target;

        if (!controller.isGrounded) target = airSpread;
        else if (IsCrouching) target = crouchSpread;
        else if (IsSneaking) target = sneakSpread;
        else if (!IsMoving) target = standSpread;
        else target = runSpread;

        spreadNow = Mathf.MoveTowards(spreadNow, target, spreadChangeSpeed * Time.deltaTime);
    }

    void Look()
    {
        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        transform.Rotate(0f, mx, 0f);
        pitch = Mathf.Clamp(pitch - my, -85f, 85f);

        float t = 1f - Mathf.Exp(-recoilReturnSpeed * Time.deltaTime);
        kickPitch = Mathf.Lerp(kickPitch, 0f, t);
        kickYaw = Mathf.Lerp(kickYaw, 0f, t);
        kickRoll = Mathf.Lerp(kickRoll, 0f, t);

        cameraTransform.localRotation = Quaternion.Euler(pitch - kickPitch, kickYaw, kickRoll);
    }

    void Move()
    {
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");

        Vector3 move = transform.right * x + transform.forward * z;
        if (move.magnitude > 1f) move.Normalize();
        moveAmount = move.magnitude;

        float speed = moveSpeed * SpeedFactor();

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        if (controller.isGrounded && Input.GetButtonDown("Jump"))
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move * speed + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }
}