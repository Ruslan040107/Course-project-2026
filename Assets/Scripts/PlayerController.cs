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

    [Header("Camera bob")]
    public float bobFrequency = 9f;
    public float bobHeight = 0.04f;
    public float bobSide = 0.025f;
    public float bobSmoothing = 10f;

    CharacterController controller;
    Health health;
    float verticalVelocity;
    float pitch;
    float currentHeight;
    float bobTimer;
    float bobBlend;
    float moveAmount;

    public float StandFactor => Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);

    public float SpreadFactor(float crouchFactor)
    {
        return Mathf.Lerp(crouchFactor, 1f, StandFactor);
    }

    void Start()
    {
        controller = GetComponent<CharacterController>();
        health = GetComponent<Health>();
        currentHeight = standHeight;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        if (health != null && health.IsDead) return;

        UpdateCrouch();
        Look();
        Move();
    }

    void UpdateCrouch()
    {
        float target = Input.GetKey(crouchKey) ? crouchHeight : standHeight;
        currentHeight = Mathf.MoveTowards(currentHeight, target, heightChangeSpeed * Time.deltaTime);

        controller.height = currentHeight;
        controller.center = new Vector3(0f, (currentHeight - standHeight) * 0.5f, 0f);

        float k = StandFactor;
        float baseY = Mathf.Lerp(crouchCameraY, standCameraY, k);

        float bobTarget = controller.isGrounded ? moveAmount * Mathf.Lerp(crouchSpeedFactor, 1f, k) : 0f;
        bobBlend = Mathf.MoveTowards(bobBlend, bobTarget, bobSmoothing * Time.deltaTime);
        bobTimer += Time.deltaTime * bobFrequency * bobBlend;

        float offsetX = Mathf.Cos(bobTimer) * bobSide * bobBlend;
        float offsetY = Mathf.Sin(bobTimer * 2f) * bobHeight * bobBlend;

        cameraTransform.localPosition = new Vector3(offsetX, baseY + offsetY, 0f);
    }

    void Look()
    {
        float mx = Input.GetAxis("Mouse X") * mouseSensitivity;
        float my = Input.GetAxis("Mouse Y") * mouseSensitivity;

        transform.Rotate(0f, mx, 0f);
        pitch = Mathf.Clamp(pitch - my, -85f, 85f);
        cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move()
    {
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");

        Vector3 move = transform.right * x + transform.forward * z;
        if (move.magnitude > 1f) move.Normalize();
        moveAmount = move.magnitude;

        float k = StandFactor;
        float speed = moveSpeed * Mathf.Lerp(crouchSpeedFactor, 1f, k);

        if (controller.isGrounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        if (controller.isGrounded && Input.GetButtonDown("Jump"))
            verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);

        verticalVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move * speed + Vector3.up * verticalVelocity;
        controller.Move(velocity * Time.deltaTime);
    }
}