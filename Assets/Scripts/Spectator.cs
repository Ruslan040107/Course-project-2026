using System.Collections.Generic;
using UnityEngine;

public class Spectator : MonoBehaviour
{
    public Transform cam;
    public GameObject crosshair;

    Health self;
    Transform originalParent;
    Vector3 originalPos;
    Quaternion originalRot;
    bool spectating;
    Health watched;
    GUIStyle style;

    void Start()
    {
        self = GetComponent<Health>();
        if (cam == null && Camera.main != null) cam = Camera.main.transform;

        originalParent = cam.parent;
        originalPos = cam.localPosition;
        originalRot = cam.localRotation;
    }

    void Update()
    {
        if (self == null || cam == null) return;

        if (!self.IsDead)
        {
            if (spectating) StopSpectating();
            return;
        }

        List<Health> mates = AliveTeammates();
        if (mates.Count == 0)
        {
            if (spectating) StopSpectating();
            return;
        }

        if (!spectating || watched == null || watched.IsDead || !watched.gameObject.activeInHierarchy)
            Watch(mates[0]);

        // clicks on the pause menu buttons (and the click that takes the cursor back) do not switch the watched teammate
        if (MainMenu.IsOpen || Time.timeScale <= 0f || Cursor.lockState != CursorLockMode.Locked) return;
        if (Time.frameCount - MainMenu.LastClosedFrame <= 1) return;

        if (Input.GetMouseButtonDown(0)) Cycle(mates, 1);
        if (Input.GetMouseButtonDown(1)) Cycle(mates, -1);
    }

    List<Health> AliveTeammates()
    {
        List<Health> list = new List<Health>();
        foreach (Health h in Health.All)
            if (h != self && h.team == self.team && !h.IsDead && h.gameObject.activeInHierarchy)
                list.Add(h);
        return list;
    }

    void Cycle(List<Health> mates, int dir)
    {
        int i = mates.IndexOf(watched);
        if (i < 0) i = 0;
        i = (i + dir + mates.Count) % mates.Count;
        Watch(mates[i]);
    }

    void Watch(Health h)
    {
        watched = h;
        cam.SetParent(h.transform, false);
        cam.localPosition = new Vector3(0f, 0.6f, 0.2f);
        cam.localRotation = Quaternion.identity;
        spectating = true;

        if (crosshair != null) crosshair.SetActive(false);
    }

    void StopSpectating()
    {
        cam.SetParent(originalParent, false);
        cam.localPosition = originalPos;
        cam.localRotation = originalRot;
        spectating = false;
        watched = null;

        if (crosshair != null) crosshair.SetActive(true);
    }

    // bottom centre, between the health and ammo boxes of GameHud (they show the watched teammate)
    GUIStyle hintStyle;
    Health titleFor;
    string titleText = "";
    const string HintText = "ЛКМ / ПКМ — сменить игрока";

    void OnGUI()
    {
        if (!spectating || watched == null) return;
        if (Time.timeScale == 0f) return;   // a menu is open
        if (Event.current.type != EventType.Repaint) return;

        float k = GameHud.Scale;
        if (style == null)
        {
            style = GameHud.MakeStyle(FontStyle.Bold);
            hintStyle = GameHud.MakeStyle(FontStyle.Normal);
        }
        style.fontSize = Mathf.Max(6, Mathf.RoundToInt(26f * k));
        hintStyle.fontSize = Mathf.Max(6, Mathf.RoundToInt(17f * k));

        if (titleFor != watched)
        {
            titleFor = watched;
            titleText = "Наблюдение: " + watched.name;
        }

        Vector2 title = GameHud.Measure(style, titleText);
        Vector2 hint = GameHud.Measure(hintStyle, HintText);
        float titleH = title.y;
        float hintH = hint.y;
        float w = Mathf.Max(title.x, hint.x) + 48f * k;
        float h = titleH + hintH + 16f * k;
        Rect r = new Rect(Mathf.Round((Screen.width - w) * 0.5f), Mathf.Round(Screen.height - GameHud.Margin * k - h),
            Mathf.Round(w), Mathf.Round(h));

        GameHud.Fill(r, GameHud.PanelColor);
        Color team = watched.team == 0 ? new Color(1f, 0.35f, 0.35f) : new Color(0.4f, 0.6f, 1f);
        GameHud.Fill(new Rect(r.x, r.y, r.width, Mathf.Max(2f, Mathf.Round(3f * k))), team);

        GameHud.Text(new Rect(r.x, r.y + 8f * k, r.width, titleH), titleText, style, Color.white, TextAnchor.MiddleCenter);
        GameHud.Text(new Rect(r.x, r.y + 8f * k + titleH, r.width, hintH), HintText, hintStyle, GameHud.DimText, TextAnchor.MiddleCenter);
    }
}