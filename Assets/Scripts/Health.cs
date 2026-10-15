using System.Collections.Generic;
using UnityEngine;

public class Health : MonoBehaviour
{
    public static readonly List<Health> All = new List<Health>();

    public int team;
    public float maxHealth = 100f;
    public float current;
    public bool deactivateOnDeath = true;
    public bool showHud;

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

    public void TakeDamage(float amount)
    {
        if (IsDead) return;

        current -= amount;
        if (IsDead) OnDeath();
    }

    public void Revive()
    {
        current = maxHealth;
        gameObject.SetActive(true);
    }
    public void Unregister()
    {
        All.Remove(this);
    }
    void OnDeath()
    {
        Debug.Log(name + " died");
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