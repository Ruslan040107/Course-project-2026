using UnityEngine;

// The player's weapons like in CS: slot 1 = primary, 2 = pistol, 3 = knife, 4 = Zeus (if bought).
// Keys 1-4 take a slot, the mouse wheel goes through the owned slots, Q takes the previous weapon.
// Each slot keeps its magazine, reserve and shot cooldown while another weapon is in the hands.
// RoundManager calls NewRound at the start of every round: a new or dead player gets the knife and the team's
// default pistol (T: Glock-18, CT: USP-S), a survivor keeps his weapons, each with a full magazine and full reserve
// (like CS; a used Zeus is charged, a reload of the last round is over). BuyMenu calls Give.
// WeaponDrops (normal 5v5 games) uses Take (G, death) and PickUp (walking over a weapon, E); a bought weapon that
// replaces another one in its slot drops the old one there.
// The weapons come from Resources/Weapons (Economy.Weapons), the old 'slots' list is only a fallback.
public class PlayerLoadout : MonoBehaviour
{
    public const int SlotCount = 4;
    public const int PrimarySlot = 0;
    public const int PistolSlot = 1;
    public const int KnifeSlot = 2;
    public const int GearSlot = 3;
    public const int GrenadeSlot = 4;
    public const int TotalSlots = 5;

    public Weapon weapon;
    [Tooltip("old test list from the scene, not used for the loadout any more (it comes from Resources/Weapons " +
             "and the buy menu); only a fallback if the knife or the default pistol is missing there")]
    public WeaponData[] slots;

    Health health;
    PlayerShoot shoot;
    PlayerGrenades grenades;
    readonly WeaponData[] owned = new WeaponData[SlotCount];
    // -1 = a full magazine / full reserve (Weapon.Equip)
    readonly int[] ammo = new int[SlotCount];
    readonly int[] reserve = new int[SlotCount];
    readonly float[] readyAt = new float[SlotCount];
    int current = -1;
    int previous = -1;
    bool initialized;
    GUIStyle style;

    // for the HUD and the buy menu
    public int CurrentSlot => current;
    public bool InGrenadeSlot => current == GrenadeSlot && grenades != null && grenades.HasAny;
    public PlayerGrenades Grenades => grenades;
    public WeaponData InSlot(int slot) => slot >= 0 && slot < SlotCount ? owned[slot] : null;
    public bool Owns(WeaponData d) => d != null && InSlot(SlotOf(d)) == d;
    public static int SlotOf(WeaponData d) => d != null ? (int)d.Slot : -1;

    void Awake()
    {
        health = GetComponent<Health>();
        shoot = GetComponent<PlayerShoot>();
        grenades = GetComponent<PlayerGrenades>();
        if (grenades == null) grenades = gameObject.AddComponent<PlayerGrenades>();
        if (weapon == null && shoot != null) weapon = shoot.weapon;
        if (weapon == null) weapon = GetComponentInChildren<Weapon>(true);
    }

    // ---------------- rounds ----------------

    // keep = the player survived the last round: his weapons stay, each with a full magazine and full reserve
    // (the Zeus charged); otherwise the default loadout. Equip below also ends a reload left from the last round.
    public void NewRound(bool keep)
    {
        if (weapon == null) return;

        if (keep && initialized && HasGun())
        {
            for (int i = 0; i < SlotCount; i++)
            {
                ammo[i] = -1;
                reserve[i] = -1;
                readyAt[i] = 0f;
            }
        }
        else
        {
            GiveDefault();
        }

        if (grenades != null) grenades.NewRound(keep);
        previous = -1;
        current = -1;
        if (shoot != null) shoot.ResetZoom();
        // Equip keeps the draw time, so the first shot of the round waits for the draw animation
        EquipSlot(BestSlot());
        // nothing to take (no weapons in Resources): at least no reload of the last round goes on
        if (current < 0) weapon.ResetAmmo();
        initialized = true;
    }

    void GiveDefault()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            owned[i] = null;
            ammo[i] = -1;
            reserve[i] = -1;
            readyAt[i] = 0f;
        }

        int team = health != null ? health.team : 0;
        owned[KnifeSlot] = Economy.Knife() ?? FromList(WeaponCategory.Melee, team);
        owned[PistolSlot] = Economy.DefaultPistol(team) ?? FromList(WeaponCategory.Pistol, team);

        // nothing in Resources and nothing in the list: keep whatever the weapon already has
        if (owned[KnifeSlot] == null && owned[PistolSlot] == null && weapon.data != null)
        {
            int s = SlotOf(weapon.data);
            if (s >= 0) owned[s] = weapon.data;
        }
    }

    // the first weapon of this category in the old scene list (the team's own one first)
    WeaponData FromList(WeaponCategory category, int team)
    {
        if (slots == null) return null;
        WeaponData any = null;
        foreach (WeaponData d in slots)
        {
            if (d == null || d.category != category) continue;
            if (Economy.TeamCanBuy(d, team)) return d;
            if (any == null) any = d;
        }
        return any;
    }

    bool HasGun()
    {
        for (int i = 0; i < SlotCount; i++)
            if (owned[i] != null) return true;
        return false;
    }

    // primary, else pistol, else knife, else the Zeus
    int BestSlot()
    {
        if (owned[PrimarySlot] != null) return PrimarySlot;
        if (owned[PistolSlot] != null) return PistolSlot;
        if (owned[KnifeSlot] != null) return KnifeSlot;
        if (owned[GearSlot] != null) return GearSlot;
        return -1;
    }

    // ---------------- buying ----------------

    // puts the weapon into its slot with full ammo; the old one there falls to the floor in a normal 5v5 game
    // (WeaponDrops, like in CS), otherwise it is gone. A bought gun is taken in the hands at once, the Zeus is not
    public bool Give(WeaponData d)
    {
        int slot = SlotOf(d);
        if (weapon == null || slot < 0 || slot >= SlotCount) return false;
        if (!initialized) NewRound(false);

        if (owned[slot] != null && owned[slot] != d && slot != KnifeSlot && WeaponDrops.Active)
            WeaponDrops.Drop(health, StateOf(slot));

        owned[slot] = d;
        ammo[slot] = -1;
        reserve[slot] = -1;
        readyAt[slot] = 0f;

        bool take = slot == current || slot == PrimarySlot || slot == PistolSlot || current < 0;
        if (take)
        {
            if (slot != current)
            {
                SaveCurrent();
                if (current >= 0) previous = current;
            }
            current = -1;   // the old weapon of this slot is not saved: it is replaced
            EquipSlot(slot);
        }
        return true;
    }

    // takes the weapon of this slot away (nothing happens to the knife); the hands get the best weapon left
    public void Remove(int slot)
    {
        Take(slot);
    }

    // ---------------- weapons on the floor (WeaponDrops) ----------------

    // the weapon of this slot with the ammo it has now (the one in the hands with its live magazine)
    GunState StateOf(int slot)
    {
        if (slot == current) SaveCurrent();
        return new GunState { data = owned[slot], ammo = ammo[slot], reserve = reserve[slot], readyAt = readyAt[slot] };
    }

    // takes the weapon of this slot out with its ammo (G, death); never the knife.
    // A living player's hands take the best weapon left, a dead player's stay as they are
    public GunState Take(int slot)
    {
        if (weapon == null || slot < 0 || slot >= SlotCount || slot == KnifeSlot || owned[slot] == null) return default;

        GunState gun = StateOf(slot);
        owned[slot] = null;
        ammo[slot] = -1;
        reserve[slot] = -1;
        readyAt[slot] = 0f;
        if (previous == slot) previous = -1;
        if (current == slot)
        {
            current = -1;
            if (health == null || !health.IsDead) EquipSlot(BestSlot());
        }
        return gun;
    }

    // a weapon from the floor goes into its slot with its ammo; the weapon that was in that slot comes back in 'old'
    // (not Valid when the slot was empty), the caller drops it. take = the hands switch to it
    // (always when it replaces the weapon in the hands)
    public bool PickUp(GunState gun, bool take, out GunState old)
    {
        old = default;
        int slot = SlotOf(gun.data);
        if (weapon == null || slot < 0 || slot >= SlotCount || slot == KnifeSlot) return false;
        if (health != null && health.IsDead) return false;
        if (!initialized) NewRound(false);

        if (owned[slot] != null) old = StateOf(slot);
        owned[slot] = gun.data;
        ammo[slot] = gun.ammo;
        reserve[slot] = gun.reserve;
        readyAt[slot] = gun.readyAt;

        if (slot == current)
        {
            // the weapon in the hands was swapped: the old one is not saved again
            current = -1;
            EquipSlot(slot);
        }
        else if (take || current < 0)
        {
            SaveCurrent();
            if (current >= 0) previous = current;
            EquipSlot(slot);
        }
        return true;
    }

    // ---------------- switching ----------------

    void Update()
    {
        if (weapon == null) return;
        if (!initialized) NewRound(false);
        if (health != null && health.IsDead) return;
        if (MainMenu.IsOpen || Time.timeScale <= 0f) return;
        // the developer console owns the keys (1-4, Q) and the wheel while it is open
        if (Cursor.lockState != CursorLockMode.Locked || DevConsole.IsOpen) return;

        for (int i = 0; i < SlotCount; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha1 + i))
            {
                if (i == GearSlot && owned[GearSlot] == null && grenades != null && grenades.HasAny)
                    Select(GrenadeSlot);
                else
                    Select(i);
            }
        }

        if (Input.GetKeyDown(KeyCode.Alpha5) && grenades != null && grenades.HasAny)
        {
            if (current == GrenadeSlot) grenades.CycleNext();
            else Select(GrenadeSlot);
        }

        if (grenades != null && grenades.HasAny)
        {
            if (Input.GetKeyDown(KeyCode.Alpha6)) SelectGrenade(GrenadeType.HE);
            else if (Input.GetKeyDown(KeyCode.Alpha7)) SelectGrenade(GrenadeType.Flash);
            else if (Input.GetKeyDown(KeyCode.Alpha8)) SelectGrenade(GrenadeType.Smoke);
            else if (Input.GetKeyDown(KeyCode.Alpha9)) SelectGrenade(GrenadeType.Fire);
            else if (Input.GetKeyDown(KeyCode.Alpha0)) SelectGrenade(GrenadeType.Decoy);
        }

        if (Input.GetKeyDown(KeyCode.Q) && previous >= 0) Select(previous);

        float wheel = Input.mouseScrollDelta.y;
        if (wheel > 0f) Select(NextOwned(-1));
        else if (wheel < 0f) Select(NextOwned(1));
    }

    bool SlotOwned(int slot)
    {
        if (slot == GrenadeSlot) return grenades != null && grenades.HasAny;
        return slot >= 0 && slot < SlotCount && owned[slot] != null;
    }

    // the next owned slot in this direction, going round
    int NextOwned(int dir)
    {
        int start = current < 0 ? 0 : current;
        for (int step = 1; step <= TotalSlots; step++)
        {
            int i = ((start + dir * step) % TotalSlots + TotalSlots) % TotalSlots;
            if (SlotOwned(i)) return i;
        }
        return current;
    }

    public bool SelectGrenade(GrenadeType type)
    {
        if (grenades == null || !grenades.SelectType(type)) return false;
        if (current != GrenadeSlot) Select(GrenadeSlot);
        listShownAt = Time.time;
        return true;
    }

    public void SwitchAfterGrenadeThrow()
    {
        if (grenades != null && grenades.HasAny)
        {
            grenades.BeginDraw();
            listShownAt = Time.time;
            return;
        }
        current = -1;
        int target = previous >= 0 && previous < SlotCount && owned[previous] != null ? previous : BestSlot();
        if (target >= 0) EquipSlot(target);
    }

    void Select(int slot)
    {
        if (slot == GrenadeSlot)
        {
            if (grenades == null || !grenades.HasAny) return;
            if (current == GrenadeSlot)
            {
                grenades.CycleNext();
                listShownAt = Time.time;
                return;
            }
            SaveCurrent();
            if (current >= 0) previous = current;
            current = GrenadeSlot;
            if (shoot != null) shoot.ResetZoom();
            grenades.BeginDraw();
            listShownAt = Time.time;
            return;
        }

        if (slot == current || slot < 0 || slot >= SlotCount || owned[slot] == null) return;

        if (current == GrenadeSlot && grenades != null) grenades.CancelThrow();
        SaveCurrent();
        if (current >= 0) previous = current;
        EquipSlot(slot);
    }

    // the ammo and the shot cooldown of the weapon in the hands go back to its slot
    void SaveCurrent()
    {
        if (current < 0 || current >= SlotCount || owned[current] == null || weapon.data != owned[current]) return;
        ammo[current] = weapon.Ammo;
        reserve[current] = weapon.Reserve;
        readyAt[current] = weapon.NextShotTime;
    }

    void EquipSlot(int slot)
    {
        if (slot < 0 || slot >= SlotCount || owned[slot] == null) return;
        current = slot;
        if (shoot != null) shoot.ResetZoom();
        weapon.Equip(owned[slot], ammo[slot], reserve[slot], readyAt[slot]);
    }

    // ---------------- weapon slot list (bottom right, above the ammo box of GameHud) ----------------
    // Like in CS: it shows up for about 2 seconds after a switch, a scroll or a slot key, then fades out.

    const float ListShowTime = 2f;
    const float ListFadeTime = 0.4f;

    GUIStyle keyStyle;
    int shownSlot = -1;
    WeaponData shownData;
    float listShownAt = -100f;

    void OnGUI()
    {
        if (!initialized) return;
        if (health != null && health.IsDead) return;
        if (Time.timeScale == 0f) return;   // a menu is open
        if (BuyMenu.IsOpen) return;
        if (Event.current.type != EventType.Repaint) return;

        WeaponData inHands = current >= 0 && current < SlotCount ? owned[current] : null;
        GrenadeData curGrenade = current == GrenadeSlot && grenades != null ? grenades.Selected : null;
        bool locked = Cursor.lockState == CursorLockMode.Locked && !DevConsole.IsOpen;
        if (current != shownSlot || inHands != shownData || curGrenade != shownGrenade || (locked && (Input.mouseScrollDelta.y != 0f || SlotKeyDown())))
        {
            shownSlot = current;
            shownData = inHands;
            shownGrenade = curGrenade;
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

        bool hasGrenades = grenades != null && grenades.HasAny;
        int count = hasGrenades ? 1 : 0;
        foreach (WeaponData d in owned) if (d != null) count++;

        float w = GameHud.AmmoBoxWidth * k;
        float rowH = Mathf.Round(34f * k);
        float gap = Mathf.Max(1f, Mathf.Round(4f * k));
        float pad = 14f * k;
        float x = Screen.width - GameHud.Margin * k - w;
        float bottom = (GameHud.Active ? GameHud.BottomBoxTop : Screen.height - 150f) - 8f * k;
        float y = bottom - count * (rowH + gap) + gap;
        Color accent = new Color(1f, 0.8f, 0.3f, alpha);

        for (int i = 0; i < SlotCount; i++)
        {
            if (owned[i] == null) continue;
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

            Texture2D icon = Icon(owned[i]);
            if (icon != null && icon.height > 0)
            {
                float ih = Mathf.Round(22f * k);
                float iw = Mathf.Round(ih * icon.width / icon.height);
                GUI.color = text;
                GUI.DrawTexture(new Rect(r.xMax - pad - keyW - 10f * k - iw, Mathf.Round(r.center.y - ih * 0.5f), iw, ih), icon);
                GUI.color = Color.white;
            }

            GameHud.Text(new Rect(r.x + pad, r.y, w * 0.5f, rowH), owned[i].weaponName, style, text, TextAnchor.MiddleLeft);
        }

        if (hasGrenades)
        {
            bool selected = current == GrenadeSlot;
            Rect r = new Rect(x, Mathf.Round(y), w, rowH);
            GameHud.DrawPanel(r, new Color(0.03f, 0.04f, 0.05f, (selected ? 0.75f : 0.45f) * alpha), false);
            if (selected) GameHud.Fill(new Rect(r.xMax - Mathf.Max(2f, Mathf.Round(3f * k)), r.y, Mathf.Max(2f, Mathf.Round(3f * k)), r.height), accent);

            Color text = new Color(1f, 1f, 1f, (selected ? 1f : 0.5f) * alpha);
            float keyW = 20f * k;
            GameHud.Text(new Rect(r.xMax - pad - keyW, r.y, keyW, rowH), "5", keyStyle,
                selected ? accent : text, TextAnchor.MiddleRight);

            float iconRight = r.xMax - pad - keyW - 10f * k;
            for (int gIdx = grenades.Items.Count - 1; gIdx >= 0; gIdx--)
            {
                GrenadeData gd = grenades.Items[gIdx];
                if (gd == null) continue;
                Texture2D icon = IconByName(gd.grenadeName);
                if (icon == null || icon.height <= 0) continue;
                float ih = Mathf.Round(22f * k);
                float iw = Mathf.Round(ih * icon.width / icon.height);
                GUI.color = gd == curGrenade && selected ? accent : text;
                GUI.DrawTexture(new Rect(iconRight - iw, Mathf.Round(r.center.y - ih * 0.5f), iw, ih), icon);
                iconRight -= iw + 4f * k;
            }
            GUI.color = Color.white;

            string label = curGrenade != null ? curGrenade.grenadeName : "Гранаты (" + grenades.Count + ")";
            GameHud.Text(new Rect(r.x + pad, r.y, w * 0.52f, rowH), label, style, text, TextAnchor.MiddleLeft);
        }
        GUI.color = Color.white;
    }

    GrenadeData shownGrenade;

    bool SlotKeyDown()
    {
        for (int i = 0; i < TotalSlots; i++)
            if (Input.GetKeyDown(KeyCode.Alpha1 + i)) return true;
        return Input.GetKeyDown(KeyCode.Q);
    }

    Texture2D Icon(WeaponData d) => d != null ? IconByName(d.weaponName) : null;

    Texture2D IconByName(string itemName)
    {
        if (string.IsNullOrEmpty(itemName)) return null;
        if (!iconCache.TryGetValue(itemName, out Texture2D tex))
        {
            tex = Resources.Load<Texture2D>("KillIcons/" + itemName);
            iconCache[itemName] = tex;
        }
        return tex;
    }

    readonly System.Collections.Generic.Dictionary<string, Texture2D> iconCache =
        new System.Collections.Generic.Dictionary<string, Texture2D>();
}
