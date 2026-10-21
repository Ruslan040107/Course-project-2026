using UnityEngine;

// Body = chest and arms. Stomach is added at the end so the saved values keep their meaning.
public enum BodyZone { Head, Body, Legs, Stomach }

// Put this on a collider of a character model (head, chest/arms, stomach, legs).
// If a collider has no HitZone, the weapon falls back to the height of the hit on the collider.
public class HitZone : MonoBehaviour
{
    public BodyZone zone = BodyZone.Body;
}
