using UnityEngine;

public class Health : MonoBehaviour
{
    public float maxHealth = 100f;
    public float current;

    public bool IsDead => current <= 0f;

    void Awake()
    {
        current = maxHealth;
    }

    public void TakeDamage(float amount)
    {
        if (IsDead) return;

        current -= amount;
        if (IsDead) OnDeath();
    }

    void OnDeath()
    {
        Debug.Log(name + " died");
        gameObject.SetActive(false);
    }
}