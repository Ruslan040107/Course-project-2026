using UnityEngine;

public class PlayerShoot : MonoBehaviour
{
    public Weapon weapon;
    Health health;

    void Start()
    {
        health = GetComponent<Health>();
    }

    void Update()
    {
        if (health != null && health.IsDead) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        if (Input.GetKeyDown(KeyCode.R)) weapon.StartReload();

        bool pressed = weapon.automatic
            ? Input.GetButton("Fire1")
            : Input.GetButtonDown("Fire1");

        if (pressed) weapon.TryFire();
    }
}