using System.Collections.Generic;
using UnityEngine;

public struct KillInfo
{
    public Health victim;
    public Health killer;
    public bool headshot;
    public bool noScope;
    public bool inAir;
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

    // damage dealt to this player by every enemy during this life
    readonly Dictionary<Health, float> damageFrom = new Dictionary<Health, float>();

    float pendingDamage;
    Health pendingAttacker;
    bool pendingHeadshot;
    bool pendingNoScope;
    bool pendingInAir;
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

    public void TakeDamage(float amount, Health attacker = null, bool headshot = false, bool noScope = false, bool inAir = false)
    {
        if (IsDead) return;
        if (attacker != null) Damaged?.Invoke(attacker);
        pendingDamage += amount;
        if (attacker != null) pendingAttacker = attacker;
        pendingHeadshot = headshot;
        pendingNoScope = noScope;
        pendingInAir = inAir;
    }

    public void ApplyPending()
    {
        if (pendingDamage <= 0f) return;

        DamageTaken?.Invoke(pendingDamage, pendingAttacker, pendingHeadshot);

        if (invulnerable)
        {
            pendingDamage = 0f;
            pendingAttacker = null;
            pendingHeadshot = false;
            pendingNoScope = false;
            pendingInAir = false;
            return;
        }

        if (!IsDead)
        {
            float dealt = Mathf.Min(pendingDamage, current);

            if (pendingAttacker != null && pendingAttacker != this && pendingAttacker.team != team)
            {
                damageFrom.TryGetValue(pendingAttacker, out float before);
                damageFrom[pendingAttacker] = before + dealt;
            }

            AnyDamaged?.Invoke(this, pendingAttacker, dealt, pendingHeadshot);
            current -= pendingDamage;
            if (IsDead) OnDeath();
        }
        pendingDamage = 0f;
    }

    public void Revive()
    {
        current = maxHealth;
        damageFrom.Clear();
        pendingDamage = 0f;
        pendingAttacker = null;
        pendingHeadshot = false;
        pendingNoScope = false;
        pendingInAir = false;
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
        AnyKilled?.Invoke(new KillInfo
        {
            victim = this,
            killer = pendingAttacker,
            headshot = pendingHeadshot,
            noScope = pendingNoScope,
            inAir = pendingInAir,
            assister = FindAssister(pendingAttacker)
        });
        damageFrom.Clear();
        pendingAttacker = null;
        pendingHeadshot = false;
        pendingNoScope = false;
        pendingInAir = false;

        if (deactivateOnDeath) gameObject.SetActive(false);
        else SetBodyVisible(false);
    }

    void OnGUI()
    {
        if (!showHud || IsDead) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 28;
            style.normal.textColor = Color.white;
        }

        GUI.Label(new Rect(20, Screen.height - 50, 300, 40), "HP: " + Mathf.CeilToInt(current), style);
    }
}