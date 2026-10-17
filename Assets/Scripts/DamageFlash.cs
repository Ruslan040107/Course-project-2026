using UnityEngine;
using UnityEngine.UI;

public class DamageFlash : MonoBehaviour
{
    [Header("Flash")]
    public Color color = new Color(0.85f, 0.05f, 0.05f);
    [Range(0f, 1f)] public float maxAlpha = 0.35f;
    public float fadeTime = 0.6f;

    Health health;
    CanvasGroup group;
    float intensity;

    void Awake()
    {
        health = GetComponent<Health>();
        if (health != null) health.Damaged += OnDamaged;

        BuildOverlay();
    }

    void OnDestroy()
    {
        if (health != null) health.Damaged -= OnDamaged;
    }

    void OnDamaged(Health attacker)
    {
        if (attacker == null || attacker == health) return;
        if (attacker.team == health.team) return;

        intensity = 1f;
    }

    void Update()
    {
        intensity = Mathf.MoveTowards(intensity, 0f, Time.deltaTime / fadeTime);
        group.alpha = intensity * maxAlpha;
    }

    void BuildOverlay()
    {
        GameObject root = new GameObject("DamageFlash");
        root.transform.SetParent(transform, false);

        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50;

        group = root.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;

        GameObject imageObject = new GameObject("Vignette");
        imageObject.transform.SetParent(root.transform, false);

        RectTransform rect = imageObject.AddComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        Image image = imageObject.AddComponent<Image>();
        image.raycastTarget = false;
        image.sprite = MakeVignette();
        image.color = Color.white;
    }

    Sprite MakeVignette()
    {
        const int size = 256;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        Vector2 center = new Vector2(size - 1, size - 1) * 0.5f;
        float maxDist = center.magnitude;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), center) / maxDist;
                float edge = Mathf.SmoothStep(0.3f, 1f, d);
                float a = Mathf.Lerp(0.12f, 1f, edge);
                tex.SetPixel(x, y, new Color(color.r, color.g, color.b, a));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}