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

    void OnGUI()
    {
        if (!spectating || watched == null) return;

        if (style == null)
        {
            style = new GUIStyle(GUI.skin.label);
            style.fontSize = 26;
            style.alignment = TextAnchor.MiddleCenter;
            style.normal.textColor = Color.white;
        }

        GUI.Label(new Rect(0, Screen.height - 100, Screen.width, 36), "Spectating: " + watched.name, style);
        GUI.Label(new Rect(0, Screen.height - 66, Screen.width, 36), "LMB / RMB: switch teammate", style);
    }
}