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

    CharacterController controller;
    Health health;
    float verticalVelocity;
    float pitch;
    float currentHeight;

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

        float k = Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);
        cameraTransform.localPosition = new Vector3(0f, Mathf.Lerp(crouchCameraY, standCameraY, k), 0f);
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

        float k = Mathf.InverseLerp(crouchHeight, standHeight, currentHeight);
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