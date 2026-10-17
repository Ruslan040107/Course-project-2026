using UnityEngine;

public enum BodyZone { Head, Body, Legs }

// Put this on a collider of a character model (head, torso, legs).
// If a collider has no HitZone, the weapon falls back to the height of the hit on the collider.
public class HitZone : MonoBehaviour
{
    public BodyZone zone = BodyZone.Body;
}
