using System.Collections.Generic;
using UnityEngine;

// Weapon models in the bots' hands (third person view).
// The model hangs under the bot's head, which BotController turns to the enemy, so the gun points where the bot shoots.
// It changes when the bot's weapon changes. CombatEffects adds this component (never in simulation).
public class BotWeaponModels : MonoBehaviour
{
    [Tooltip("from the head, in meters: right, down, forward")]
    public Vector3 offset = new Vector3(0.28f, -0.35f, 0.42f);

    class Entry
    {
        public GameObject holder;
        public WeaponData data;
    }

    readonly Dictionary<BotController, Entry> models = new Dictionary<BotController, Entry>();
    float nextCheck;

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;

        foreach (Health h in Health.All)
        {
            BotController bot = h.GetComponent<BotController>();
            if (bot == null || bot.weapon == null || bot.head == null) continue;

            WeaponData d = bot.weapon.data;
            models.TryGetValue(bot, out Entry e);
            if (e != null && e.data == d && e.holder != null) continue;

            if (e != null && e.holder != null) Destroy(e.holder);
            e = new Entry { data = d };

            GameObject prefab = ViewModel.ModelFor(d);
            if (prefab != null)
            {
                Transform holder = new GameObject("Weapon model").transform;
                holder.SetParent(bot.head, false);

                // real size whatever the head's scale is
                Vector3 s = bot.head.lossyScale;
                Vector3 inv = new Vector3(1f / Mathf.Max(s.x, 1e-4f), 1f / Mathf.Max(s.y, 1e-4f), 1f / Mathf.Max(s.z, 1e-4f));
                holder.localScale = inv;
                holder.localPosition = Vector3.Scale(offset, inv);

                ViewModel.SpawnModel(prefab, holder, true);
                e.holder = holder.gameObject;
            }
            models[bot] = e;
        }
    }
}
