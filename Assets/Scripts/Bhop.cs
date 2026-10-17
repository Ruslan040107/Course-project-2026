using UnityEngine;

// Paid movement upgrade (like the stealth levels): chained jumps give extra running speed.
// level 0 = off. With a level above 0 the player can hold Jump to keep hopping.
public class Bhop : MonoBehaviour
{
    [Range(0, 3)] public int level = 0;

    [Tooltip("extra speed gained by each chained jump, index = level")]
    public float[] gainPerJump = { 0f, 0.08f, 0.10f, 0.12f };

    [Tooltip("the biggest extra speed, index = level (0.5 = +50%)")]
    public float[] maxBonus = { 0f, 0.25f, 0.5f, 0.8f };

    [Tooltip("if the owner stays on the ground longer than this, the chain is broken")]
    public float chainWindow = 0.2f;

    [Tooltip("how fast the bonus fades after the chain is broken, per second")]
    public float decayPerSecond = 1.5f;

    float bonus;
    float landedAt = -1f;
    bool wasGrounded = true;

    public bool Enabled => level > 0;
    public float Bonus => bonus;
    public float SpeedMultiplier => 1f + bonus;

    public void ResetBonus()
    {
        bonus = 0f;
        landedAt = -1f;
        wasGrounded = true;
    }

    // call every frame
    public void Tick(bool grounded, bool moving)
    {
        if (!Enabled)
        {
            bonus = 0f;
            return;
        }

        if (grounded && !wasGrounded) landedAt = Time.time;
        if (!grounded) landedAt = -1f;
        wasGrounded = grounded;

        bool chainBroken = grounded && landedAt >= 0f && Time.time - landedAt > chainWindow;
        bool standing = grounded && !moving;

        if (chainBroken || standing)
            bonus = Mathf.MoveTowards(bonus, 0f, decayPerSecond * Time.deltaTime);
    }

    // call when a jump starts
    public void Jumped()
    {
        if (!Enabled) return;

        float gain = gainPerJump[Mathf.Clamp(level, 0, gainPerJump.Length - 1)];
        float max = maxBonus[Mathf.Clamp(level, 0, maxBonus.Length - 1)];
        bonus = Mathf.Min(bonus + gain, max);
    }
}
