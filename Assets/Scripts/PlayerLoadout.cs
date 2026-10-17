using UnityEngine;

// Test loadout until the buy menu exists: keys 1-9 or the mouse wheel switch weapons.
public class PlayerLoadout : MonoBehaviour
{
    public Weapon weapon;
    public WeaponData[] slots;

    Health health;
    PlayerShoot shoot;
    int[] ammo;
    int[] reserve;
    int current;
    bool initialized;
    GUIStyle style;

    void Awake()
    {
        health = GetComponent<Health>();
        shoot = GetComponent<PlayerShoot>();
    }

    void OnEnable()
    {
        RoundManager.RoundStarted += ResetSlots;
    }

    void OnDisable()
    {
        RoundManager.RoundStarted -= ResetSlots;
    }

    void ResetSlots()
    {
        if (weapon == null || slots == null || slots.Length == 0) return;

        ammo = new int[slots.Length];
        reserve = new int[slots.Length];

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            ammo[i] = slots[i].magazineSize;
            reserve[i] = slots[i].reserveAmmo;
        }

        current = 0;
        if (shoot != null) shoot.ResetZoom();
        if (slots[0] != null)
        {
            weapon.Equip(slots[0]);
            weapon.ResetAmmo();
        }

        initialized = true;
    }

    void Update()
    {
        if (weapon == null || slots == null || slots.Length == 0) return;
        if (!initialized || ammo == null || ammo.Length != slots.Length) ResetSlots();
        if (!initialized) return;
        if (health != null && health.IsDead) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        for (int i = 0; i < slots.Length && i < 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) Select(i);

        float wheel = Input.mouseScrollDelta.y;
        if (wheel > 0f) Select((current - 1 + slots.Length) % slots.Length);
        else if (wheel < 0f) Select((current + 1) % slots.Length);
    }

    void Select(int index)
    {
        if (index == current || index < 0 || index >= slots.Length || slots[index] == null) return;

        ammo[current] = weapon.Ammo;
        reserve[current] = weapon.Reserve;

        current = index;
        if (shoot != null) shoot.ResetZoom();
        weapon.Equip(slots[index], ammo[index], reserve[index]);
    }

    void OnGUI()
    {
        if (slots == null || slots.Length == 0) return;
        if (health != null && health.IsDead) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 18;
            style.alignment = TextAnchor.LowerRight;
        }

        float w = 320f;
        float x = Screen.width - w - 24f;
        float y = Screen.height - 170f - slots.Length * 24f;

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            style.normal.textColor = i == current ? Color.white : new Color(1f, 1f, 1f, 0.45f);
            GUI.Label(new Rect(x, y + i * 24f, w, 26f), (i + 1) + "  " + slots[i].weaponName, style);
        }
    }
}