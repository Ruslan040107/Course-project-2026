using UnityEngine;

public class PlayerHud : MonoBehaviour
{
    public Weapon weapon;

    Health health;
    GUIStyle big;
    GUIStyle small;

    void Start()
    {
        health = GetComponent<Health>();
    }

    void OnGUI()
    {
        if (weapon == null) return;
        if (health != null && health.IsDead) return;

        if (big == null)
        {
            big = new GUIStyle(GUI.skin.label);
            big.fontSize = 40;
            big.fontStyle = FontStyle.Bold;
            big.alignment = TextAnchor.LowerRight;
            big.normal.textColor = Color.white;

            small = new GUIStyle(GUI.skin.label);
            small.fontSize = 22;
            small.alignment = TextAnchor.LowerRight;
            small.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
        }

        float w = 320f;
        float x = Screen.width - w - 24f;
        float y = Screen.height - 120f;

        GUI.Label(new Rect(x, y, w, 36f), weapon.weaponName, small);
        GUI.Label(new Rect(x, y + 28f, w, 56f), weapon.Ammo + " / " + weapon.Reserve, big);

        if (weapon.IsReloading)
        {
            GUI.Label(new Rect(x, y - 30f, w, 30f), "Reloading...", small);

            Rect bar = new Rect(x + w - 200f, y + 88f, 200f, 6f);
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(bar, Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * weapon.ReloadProgress, bar.height), Texture2D.whiteTexture);
        }
    }
}