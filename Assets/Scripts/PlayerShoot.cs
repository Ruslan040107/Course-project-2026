using UnityEngine;

public class PlayerShoot : MonoBehaviour
{
    public Weapon weapon;

    void Update()
    {
        if (Cursor.lockState != CursorLockMode.Locked) return;

        bool pressed = weapon.automatic
            ? Input.GetButton("Fire1")
            : Input.GetButtonDown("Fire1");

        if (pressed) weapon.TryFire();
    }
}