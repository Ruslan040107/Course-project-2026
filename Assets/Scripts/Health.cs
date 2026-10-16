using System.Collections.Generic;
using UnityEngine;

public class Health : MonoBehaviour
{
    public static readonly List<Health> All = new List<Health>();
    public static event System.Action<Health, Health> AnyKilled;

    public int team;
    public float maxHealth = 100f;
    public float current;
    public bool deactivateOnDeath = true;
    public bool showHud;

    float pendingDamage;
    Health pendingAttacker;
    GUIStyle style;

    public bool IsDead => current <= 0f;

    void Awake()
    {
        current = maxHealth;
        All.Add(this);
    }

    void OnDestroy()
    {
        All.Remove(this);
    }

    public void Unregister()
    {
        All.Remove(this);
    }

    public void TakeDamage(float amount, Health attacker = null)
    {
        if (IsDead) return;

        pendingDamage += amount;
        if (attacker != null) pendingAttacker = attacker;
    }

    public void ApplyPending()
    {
        if (pendingDamage <= 0f) return;

        if (!IsDead)
        {
            current -= pendingDamage;
            if (IsDead) OnDeath();
        }
        pendingDamage = 0f;
    }

    public void Revive()
    {
        current = maxHealth;
        pendingDamage = 0f;
        pendingAttacker = null;
        gameObject.SetActive(true);
    }

    void OnDeath()
    {
        Debug.Log(name + " died");
        AnyKilled?.Invoke(this, pendingAttacker);
        pendingAttacker = null;

        if (deactivateOnDeath) gameObject.SetActive(false);
    }

    void OnGUI()
    {
        if (!showHud) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 28;
            style.normal.textColor = Color.white;
        }

        GUI.Label(new Rect(20, Screen.height - 50, 300, 40), "HP: " + Mathf.CeilToInt(current), style);
    }
}