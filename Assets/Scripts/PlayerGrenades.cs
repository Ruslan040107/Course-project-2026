using System.Collections.Generic;
using UnityEngine;

// Player grenade belt and throw controller.
// Limits (like CS2): max 4 grenades total, max 1 of each type (HE, Flash, Smoke, Fire, Decoy).
// Controls when GrenadeSlot is active:
// - LMB: strong throw, RMB: weak lob, LMB + RMB: medium throw
// - Pin is pulled on button press; throw executes on release.
public class PlayerGrenades : MonoBehaviour
{
    public const int MaxTotal = 4;

    public enum ThrowState { Idle, Pulling, Holding, Throwing }

    readonly List<GrenadeData> items = new List<GrenadeData>();
    int selectedIndex = -1;

    Health health;
    PlayerController pc;
    PlayerLoadout loadout;

    ThrowState state = ThrowState.Idle;
    ThrowStrength strength = ThrowStrength.High;
    bool bothHeld;
    float bothReleasedGraceUntil = -10f;
    float stateAt;
    float drawUntil;
    float throwAnimUntil;
    GrenadeData throwingData;

    public IReadOnlyList<GrenadeData> Items => items;
    public int Count => items.Count;
    public bool HasAny => items.Count > 0;
    public GrenadeData Selected => selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null;
    public ThrowState State => state;
    public ThrowStrength CurrentStrength => strength;
    public float StateTime => Time.time - stateAt;
    public bool IsDrawing => Time.time < drawUntil;
    public GrenadeData ThrowingData => throwingData;

    void Awake()
    {
        health = GetComponent<Health>();
        pc = GetComponent<PlayerController>();
        loadout = GetComponent<PlayerLoadout>();
    }

    public void NewRound(bool keep)
    {
        CancelThrow();
        if (!keep)
        {
            items.Clear();
            selectedIndex = -1;
        }
        else if (items.Count > 0 && (selectedIndex < 0 || selectedIndex >= items.Count))
        {
            selectedIndex = 0;
        }
    }

    public int CountOfType(GrenadeType type)
    {
        int n = 0;
        for (int i = 0; i < items.Count; i++)
            if (items[i] != null && items[i].type == type) n++;
        return n;
    }

    public bool CanCarry(GrenadeData g, out string reason)
    {
        reason = "";
        if (g == null) return false;
        if (CountOfType(g.type) >= Mathf.Max(1, g.maxCarry))
        {
            reason = "У вас уже есть такая граната";
            return false;
        }
        if (items.Count >= MaxTotal)
        {
            reason = "Нельзя нести больше " + MaxTotal + " гранат";
            return false;
        }
        return true;
    }

    public bool Add(GrenadeData g)
    {
        if (!CanCarry(g, out _)) return false;
        items.Add(g);
        if (selectedIndex < 0) selectedIndex = items.Count - 1;
        return true;
    }

    public bool HasType(GrenadeType type) => CountOfType(type) > 0;

    public bool SelectType(GrenadeType type)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null && items[i].type == type)
            {
                if (selectedIndex != i)
                {
                    CancelThrow();
                    selectedIndex = i;
                    BeginDraw();
                }
                return true;
            }
        }
        return false;
    }

    public void CycleNext()
    {
        if (items.Count == 0)
        {
            selectedIndex = -1;
            return;
        }
        CancelThrow();
        selectedIndex = (selectedIndex + 1) % items.Count;
        BeginDraw();
    }

    public void BeginDraw()
    {
        GrenadeData g = Selected;
        drawUntil = Time.time + (g != null ? g.drawTime : 0.4f);
    }

    public void CancelThrow()
    {
        if (state == ThrowState.Pulling || state == ThrowState.Holding)
        {
            state = ThrowState.Idle;
            bothHeld = false;
            stateAt = Time.time;
        }
    }

    void Update()
    {
        if (state == ThrowState.Throwing && Time.time >= throwAnimUntil)
        {
            state = ThrowState.Idle;
            throwingData = null;
            if (loadout != null && loadout.InGrenadeSlot)
                loadout.SwitchAfterGrenadeThrow();
            return;
        }

        if (loadout == null || !loadout.InGrenadeSlot)
        {
            CancelThrow();
            return;
        }

        if (health != null && health.IsDead)
        {
            CancelThrow();
            return;
        }

        if (MainMenu.IsOpen || BuyMenu.IsOpen || DevConsole.IsOpen || Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked)
            return;

        if (Time.frameCount - MainMenu.LastClosedFrame <= 1) return;
        if (RoundManager.InFreezeTime)
        {
            CancelThrow();
            return;
        }

        GrenadeData g = Selected;
        if (g == null) return;

        bool lmb = Input.GetMouseButton(0);
        bool rmb = Input.GetMouseButton(1);
        bool anyHeld = lmb || rmb;

        if (state == ThrowState.Idle)
        {
            if (!IsDrawing && anyHeld)
            {
                state = ThrowState.Pulling;
                stateAt = Time.time;
                bothHeld = lmb && rmb;
                strength = ResolveStrength(lmb, rmb);
                GrenadeSystem.NotifyPinPulled(health, g);
            }
            return;
        }

        if (state == ThrowState.Pulling || state == ThrowState.Holding)
        {
            if (lmb && rmb)
            {
                bothHeld = true;
                bothReleasedGraceUntil = Time.time + 0.09f;
            }

            if (anyHeld)
            {
                if (lmb && rmb) strength = ThrowStrength.Mid;
                else if (Time.time <= bothReleasedGraceUntil) strength = ThrowStrength.Mid;
                else strength = lmb ? ThrowStrength.High : ThrowStrength.Low;

                if (state == ThrowState.Pulling && Time.time - stateAt >= g.pinPullTime)
                {
                    state = ThrowState.Holding;
                    stateAt = Time.time;
                }
            }
            else
            {
                // Release executes the throw; if both were just released together, keep Mid
                if (bothHeld && Time.time <= bothReleasedGraceUntil) strength = ThrowStrength.Mid;
                ExecuteThrow(g, strength);
            }
        }
    }

    static ThrowStrength ResolveStrength(bool lmb, bool rmb)
    {
        if (lmb && rmb) return ThrowStrength.Mid;
        return lmb ? ThrowStrength.High : ThrowStrength.Low;
    }

    void ExecuteThrow(GrenadeData g, ThrowStrength str)
    {
        Transform eye = pc != null && pc.cameraTransform != null ? pc.cameraTransform : transform;
        Vector3 forward = pc != null ? (pc.AimRotation * Vector3.forward) : eye.forward;

        // Spawn slightly in front of eye, clamped against walls so grenade never starts inside geometry
        Vector3 spawnPos = eye.position + forward * 0.42f - Vector3.up * 0.08f;
        Vector3 toSpawn = spawnPos - eye.position;
        float spawnDist = toSpawn.magnitude;
        if (spawnDist > 0.02f && Physics.Raycast(eye.position, toSpawn / spawnDist, out RaycastHit wall, spawnDist + 0.08f, GrenadeSystem.WorldMask, QueryTriggerInteraction.Ignore))
            spawnPos = wall.point + wall.normal * 0.1f;

        Vector3 playerVel = pc != null ? pc.Velocity : Vector3.zero;
        Vector3 launchVel = forward * g.SpeedFor(str) + Vector3.up * g.UpBoostFor(str) + playerVel * 0.55f;

        WeaponData mimic = null;
        if (g.type == GrenadeType.Decoy && loadout != null)
            mimic = loadout.InSlot(PlayerLoadout.PrimarySlot) ?? loadout.InSlot(PlayerLoadout.PistolSlot);

        GrenadeSystem.SpawnThrow(health, g, spawnPos, launchVel, mimic);

        throwingData = g;
        state = ThrowState.Throwing;
        stateAt = Time.time;
        throwAnimUntil = Time.time + 0.22f;
        bothHeld = false;

        if (!Weapon.InfiniteAmmo)
        {
            if (selectedIndex >= 0 && selectedIndex < items.Count)
                items.RemoveAt(selectedIndex);
            if (items.Count == 0) selectedIndex = -1;
            else if (selectedIndex >= items.Count) selectedIndex = items.Count - 1;
        }
    }
}
