using System.Collections.Generic;
using UnityEngine;

// How hard it is for a bullet to go through this object (wallbang).
// In each object a bullet loses strength * thickness (m) / weapon penetration of its damage, see Weapon.TryFire.
// Objects without this component get a default strength by their material or object name (see StrengthOf).
public class Penetrable : MonoBehaviour
{
    // this strength or more: bullets never go through
    public const float Impenetrable = 100f;

    [Tooltip("damage loss per meter of thickness: wood 0.5, thin metal 0.5, plaster 1.5, concrete 2.5, bricks 3; 100 or more = bullets never go through")]
    public float strength = 2f;

    // defaults of colliders without the component, so the names are checked only once per collider
    static readonly Dictionary<Collider, float> defaults = new Dictionary<Collider, float>();

    public static bool IsImpenetrable(float strength)
    {
        return strength >= Impenetrable;
    }

    public static float StrengthOf(Collider c)
    {
        Penetrable p = c.GetComponentInParent<Penetrable>();
        if (p != null) return p.strength;

        if (!defaults.TryGetValue(c, out float s))
        {
            s = DefaultStrength(c);
            defaults[c] = s;
        }
        return s;
    }

    // for maps built without the component (the arena in SampleScene): by the object and material names
    static float DefaultStrength(Collider c)
    {
        string n = c.name.ToLowerInvariant();
        Renderer r = c.GetComponentInParent<Renderer>();
        if (r != null && r.sharedMaterial != null) n += " " + r.sharedMaterial.name.ToLowerInvariant();

        if (Has(n, "wood", "crate", "plank")) return 0.5f;
        if (Has(n, "block", "shelter")) return 1.2f;
        if (n.Contains("wall")) return 2f;
        if (Has(n, "border", "floor", "ground", "plane")) return Impenetrable;
        return 2f;
    }

    static bool Has(string text, params string[] words)
    {
        foreach (string w in words)
            if (text.Contains(w)) return true;
        return false;
    }
}
