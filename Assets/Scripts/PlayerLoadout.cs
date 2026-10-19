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
    float[] readyAt;
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
        readyAt = new float[slots.Length];

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
            // Equip fills the magazine and keeps the draw time, so the first shot waits for the draw animation
            weapon.Equip(slots[0]);
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
        readyAt[current] = weapon.NextShotTime;

        current = index;
        if (shoot != null) shoot.ResetZoom();
        weapon.Equip(slots[index], ammo[index], reserve[index], readyAt[index]);
    }

    // ---------------- weapon slot list (bottom right, above the ammo box of GameHud) ----------------
    // Like in CS: it shows up for about 2 seconds after a switch, a scroll or a slot key, then fades out.

    const float ListShowTime = 2f;
    const float ListFadeTime = 0.4f;

    GUIStyle keyStyle;
    int shownSlot;
    float listShownAt = -100f;
    readonly System.Collections.Generic.Dictionary<WeaponData, Texture2D> icons =
        new System.Collections.Generic.Dictionary<WeaponData, Texture2D>();

    void OnGUI()
    {
        if (slots == null || slots.Length == 0 || !initialized) return;
        if (health != null && health.IsDead) return;
        if (Time.timeScale == 0f) return;   // a menu is open
        if (Event.current.type != EventType.Repaint) return;

        bool locked = Cursor.lockState == CursorLockMode.Locked;
        if (current != shownSlot || (locked && (Input.mouseScrollDelta.y != 0f || SlotKeyDown())))
        {
            shownSlot = current;
            listShownAt = Time.time;
        }

        float age = Time.time - listShownAt;
        if (age > ListShowTime) return;
        float alpha = Mathf.Clamp01((ListShowTime - age) / ListFadeTime);

        float k = GameHud.Scale;
        if (style == null)
        {
            style = GameHud.MakeStyle(FontStyle.Bold);
            keyStyle = GameHud.MakeStyle(FontStyle.Bold);
        }
        style.fontSize = Mathf.Max(6, Mathf.RoundToInt(18f * k));
        keyStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(20f * k));

        int count = 0;
        foreach (WeaponData d in slots) if (d != null) count++;

        float w = GameHud.AmmoBoxWidth * k;
        float rowH = Mathf.Round(34f * k);
        float gap = Mathf.Max(1f, Mathf.Round(4f * k));
        float pad = 14f * k;
        float x = Screen.width - GameHud.Margin * k - w;
        float bottom = (GameHud.Active ? GameHud.BottomBoxTop : Screen.height - 150f) - 8f * k;
        float y = bottom - count * (rowH + gap) + gap;
        Color accent = new Color(1f, 0.8f, 0.3f, alpha);

        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] == null) continue;
            bool selected = i == current;
            Rect r = new Rect(x, Mathf.Round(y), w, rowH);
            y += rowH + gap;

            GameHud.DrawPanel(r, new Color(0.03f, 0.04f, 0.05f, (selected ? 0.75f : 0.45f) * alpha), false);
            if (selected) GameHud.Fill(new Rect(r.xMax - Mathf.Max(2f, Mathf.Round(3f * k)), r.y, Mathf.Max(2f, Mathf.Round(3f * k)), r.height), accent);

            Color text = new Color(1f, 1f, 1f, (selected ? 1f : 0.5f) * alpha);

            // slot key at the right edge, the weapon icon left of it, the name on the left
            float keyW = 20f * k;
            GameHud.Text(new Rect(r.xMax - pad - keyW, r.y, keyW, rowH), GameHud.Num(i + 1), keyStyle,
                selected ? accent : text, TextAnchor.MiddleRight);

            Texture2D icon = Icon(slots[i]);
            if (icon != null && icon.height > 0)
            {
                float ih = Mathf.Round(22f * k);
                float iw = Mathf.Round(ih * icon.width / icon.height);
                GUI.color = text;
                GUI.DrawTexture(new Rect(r.xMax - pad - keyW - 10f * k - iw, Mathf.Round(r.center.y - ih * 0.5f), iw, ih), icon);
                GUI.color = Color.white;
            }

            GameHud.Text(new Rect(r.x + pad, r.y, w * 0.5f, rowH), slots[i].weaponName, style, text, TextAnchor.MiddleLeft);
        }
        GUI.color = Color.white;
    }

    bool SlotKeyDown()
    {
        for (int i = 0; i < slots.Length && i < 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) return true;
        return false;
    }

    // the killfeed silhouette of the weapon (Resources/KillIcons/<weapon name>), null if there is none
    Texture2D Icon(WeaponData d)
    {
        if (!icons.TryGetValue(d, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + d.weaponName);
            icons[d] = tex;
        }
        return tex;
    }
}