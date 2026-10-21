using System.Collections.Generic;
using UnityEngine;

public struct KillInfo
{
    public Health victim;
    public Health killer;
    public bool headshot;
    public bool noScope;
    public bool inAir;
    public bool wallbang;       // the killing bullet went through a wall or another object
    public WeaponData weapon;   // the killer's weapon (null: fall, out of the map)
    public Health assister;   // dealt enough damage to the victim, but did not make the kill
}

public class Health : MonoBehaviour
{
    public static readonly List<Health> All = new List<Health>();
    public static event System.Action<KillInfo> AnyKilled;
    // victim, attacker, damage (not more than the victim had left), headshot
    public static event System.Action<Health, Health, float, bool> AnyDamaged;

    public int team;
    public float maxHealth = 100f;
    public float current;
    public bool deactivateOnDeath = true;
    public bool showHud;
    public event System.Action<Health> Damaged;
    // amount, attacker, headshot; fired when the damage is applied
    public event System.Action<float, Health, bool> DamageTaken;
    // training target: takes damage (and reports it) but never dies
    public bool invulnerable;

    public const float AssistDamage = 41f;

    // damage dealt to this player by every enemy during this life (teammates do not count, so they never get assists)
    readonly Dictionary<Health, float> damageFrom = new Dictionary<Health, float>();
    // damage of the current physics step from each attacker (several players can hit in the same step)
    readonly Dictionary<Health, float> pendingFrom = new Dictionary<Health, float>();

    float pendingDamage;
    Health pendingAttacker;
    bool pendingHeadshot;
    bool pendingNoScope;
    bool pendingInAir;
    bool pendingWallbang;
    WeaponData pendingWeapon;
    GUIStyle style;

    public bool IsDead => current <= 0f;
    Renderer[] bodyRenderers;
    Collider[] bodyColliders;

    void SetBodyVisible(bool visible)
    {
        if (bodyRenderers == null) return;
        foreach (Renderer r in bodyRenderers) r.enabled = visible;
        foreach (Collider c in bodyColliders) c.enabled = visible;
    }

    void Awake()
    {
        current = maxHealth;
        All.Add(this);
        if (!deactivateOnDeath)
        {
            bodyRenderers = GetComponentsInChildren<Renderer>(true);
            bodyColliders = GetComponentsInChildren<Collider>(true);
        }
    }

    void OnDestroy()
    {
        All.Remove(this);
    }

    public void Unregister()
    {
        All.Remove(this);
    }

    public void TakeDamage(float amount, Health attacker = null, bool headshot = false, bool noScope = false, bool inAir = false,
                           bool wallbang = false, WeaponData weapon = null)
    {
        if (IsDead) return;
        if (attacker != null) Damaged?.Invoke(attacker);
        pendingDamage += amount;

        // this attacker already hit in this physics step and made the last hit (the other pellets of a shotgun shell)
        bool sameStep = attacker != null && attacker == pendingAttacker && pendingFrom.ContainsKey(attacker);

        if (attacker != null && amount > 0f)
        {
            pendingFrom.TryGetValue(attacker, out float before);
            pendingFrom[attacker] = before + amount;
        }

        // the kill goes to the last hit of the step, but a teammate's hit (friendly fire)
        // does not take it from an enemy who hit in the same step
        if (IsTeammate(attacker) && IsEnemy(pendingAttacker) && pendingFrom.ContainsKey(pendingAttacker)) return;

        // the pellets of one shotgun shell come in the same step: a kill is a headshot if any of them hit the head
        if (attacker != null) pendingAttacker = attacker;
        pendingHeadshot = headshot || (sameStep && pendingHeadshot);
        pendingNoScope = noScope;
        pendingInAir = inAir;
        pendingWallbang = wallbang;
        pendingWeapon = weapon;
    }

    bool IsEnemy(Health h)
    {
        return h != null && h != this && h.team != team;
    }

    bool IsTeammate(Health h)
    {
        return h != null && h != this && h.team == team;
    }

    void ClearPending()
    {
        pendingFrom.Clear();
        pendingAttacker = null;
        pendingHeadshot = false;
        pendingNoScope = false;
        pendingInAir = false;
        pendingWallbang = false;
        pendingWeapon = null;
    }

    public void ApplyPending()
    {
        if (pendingDamage <= 0f)
        {
            pendingFrom.Clear();
            return;
        }

        DamageTaken?.Invoke(pendingDamage, pendingAttacker, pendingHeadshot);

        if (invulnerable)
        {
            pendingDamage = 0f;
            ClearPending();
            return;
        }

        if (!IsDead)
        {
            float dealt = Mathf.Min(pendingDamage, current);

            // for assists: each enemy of this step gets his part of the damage the victim really lost
            float share = dealt / pendingDamage;
            foreach (KeyValuePair<Health, float> pair in pendingFrom)
            {
                if (!IsEnemy(pair.Key)) continue;
                damageFrom.TryGetValue(pair.Key, out float before);
                damageFrom[pair.Key] = before + pair.Value * share;
            }

            // an error in a handler must not stop the damage (and the coroutine of RoundManager that applies it)
            try
            {
                AnyDamaged?.Invoke(this, pendingAttacker, dealt, pendingHeadshot);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
            current -= pendingDamage;
            if (IsDead) OnDeath();
        }
        pendingDamage = 0f;
        pendingFrom.Clear();
    }

    public void Revive()
    {
        current = maxHealth;
        damageFrom.Clear();
        pendingDamage = 0f;
        ClearPending();
        SetBodyVisible(true);
        gameObject.SetActive(true);
    }

    // the enemy who did the most damage (at least AssistDamage), not counting the killer
    Health FindAssister(Health killer)
    {
        Health best = null;
        float bestDamage = 0f;

        foreach (KeyValuePair<Health, float> pair in damageFrom)
        {
            if (pair.Key == null || pair.Key == killer || pair.Value < AssistDamage) continue;
            if (best == null || pair.Value > bestDamage)
            {
                best = pair.Key;
                bestDamage = pair.Value;
            }
        }

        return best;
    }

    void OnDeath()
    {
        Debug.Log(name + " died");

        // a team kill (friendly fire): the killer is the teammate, and no enemy gets an assist for it
        Health assister = IsTeammate(pendingAttacker) ? null : FindAssister(pendingAttacker);

        KillInfo info = new KillInfo
        {
            victim = this,
            killer = pendingAttacker,
            headshot = pendingHeadshot,
            noScope = pendingNoScope,
            inAir = pendingInAir,
            wallbang = pendingWallbang,
            weapon = pendingWeapon,
            assister = assister
        };

        // every handler is called on its own: an error in one (effects, scoreboard, bodies) must not stop the others,
        // and the victim must still be hidden below, or a dead bot would stay standing
        if (AnyKilled != null)
        {
            foreach (System.Delegate d in AnyKilled.GetInvocationList())
            {
                try
                {
                    ((System.Action<KillInfo>)d)(info);
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
        damageFrom.Clear();
        ClearPending();

        if (deactivateOnDeath) gameObject.SetActive(false);
        else SetBodyVisible(false);
    }

    // GameHud draws the health itself and sets this
    public static bool HudDrawsHealth;

    void OnGUI()
    {
        if (HudDrawsHealth || !showHud || IsDead) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 28;
            style.normal.textColor = Color.white;
        }

        GUI.Label(new Rect(20, Screen.height - 50, 300, 40), "HP: " + Mathf.CeilToInt(current), style);
    }
}