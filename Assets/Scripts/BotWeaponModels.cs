using System.Collections.Generic;
using UnityEngine;

// Weapon models in the hands of bots (third person view).
// With a character model (CharacterBody) the gun is in the model's right fist (CharacterBody.WeaponMount):
// it follows the animations, and while the bot aims CharacterBody turns it exactly along the aim.
// The player with a character model gets one too (his model only casts a shadow while he is alive).
// Without a character model it hangs under the bot's head with an offset, as before; the head is turned
// to the enemy by BotController, so the gun points where the bot shoots.
// It changes when the weapon changes. CombatEffects adds this component (never in simulation).
public class BotWeaponModels : MonoBehaviour
{
    // the name of the object that holds a gun (CharacterBody does not hide it)
    public const string HolderName = "Weapon model";

    [Tooltip("from the head, in meters: right, down, forward (only without a character model)")]
    public Vector3 offset = new Vector3(0.28f, -0.35f, 0.42f);

    class Entry
    {
        public GameObject holder;
        public WeaponData data;
        public Transform parent;
        public Transform muzzle;
    }

    static BotWeaponModels instance;
    readonly Dictionary<Health, Entry> models = new Dictionary<Health, Entry>();
    float nextCheck;

    void OnEnable()
    {
        instance = this;
    }

    void OnDisable()
    {
        if (instance == this) instance = null;
    }

    // the muzzle of the gun model in this bot's hands, for tracers; false when there is none or it is hidden
    public static bool TryGetMuzzle(Health shooter, out Vector3 position)
    {
        position = Vector3.zero;
        if (instance == null || shooter == null || !instance.models.TryGetValue(shooter, out Entry e)) return false;
        if (e.holder == null || e.muzzle == null || !e.holder.activeInHierarchy) return false;
        position = e.muzzle.position;
        return true;
    }

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.5f;

        foreach (Health h in Health.All)
        {
            if (h == null) continue;
            BotController bot = h.GetComponent<BotController>();
            CharacterBody body = h.GetComponent<CharacterBody>();
            Transform mount = body != null ? body.WeaponMount : null;

            // a bot: in the model's fist or under the head; the player: only with a character model
            Weapon weapon = null;
            Transform parent = mount;
            if (bot != null)
            {
                weapon = bot.weapon;
                if (parent == null) parent = bot.head;
            }
            else if (mount != null && h.GetComponent<PlayerController>() != null)
            {
                PlayerShoot shoot = h.GetComponent<PlayerShoot>();
                weapon = shoot != null ? shoot.weapon : h.GetComponentInChildren<Weapon>();
            }
            if (weapon == null || parent == null) continue;

            WeaponData d = weapon.data;
            models.TryGetValue(h, out Entry e);
            if (e != null && e.data == d && e.parent == parent && e.holder != null) continue;

            if (e != null && e.holder != null) Destroy(e.holder);
            e = new Entry { data = d, parent = parent };

            GameObject prefab = ViewModel.ModelFor(d);
            if (prefab != null)
            {
                Transform holder = new GameObject(HolderName).transform;
                holder.SetParent(parent, false);

                // real size whatever the parent's scale is
                Vector3 s = parent.lossyScale;
                Vector3 inv = new Vector3(1f / Mathf.Max(s.x, 1e-4f), 1f / Mathf.Max(s.y, 1e-4f), 1f / Mathf.Max(s.z, 1e-4f));
                holder.localScale = inv;
                // the mount is already in the fist, the head needs the offset to the hands
                holder.localPosition = parent == mount ? Vector3.zero : Vector3.Scale(offset, inv);

                ViewModel.SpawnModel(prefab, holder, true);
                e.holder = holder.gameObject;
                e.muzzle = ViewModel.FindChild(holder, "Muzzle");

                // the right shadow mode at once (the player's own gun must not show in his view)
                if (body != null && parent == mount) body.RefreshRenderers();
            }
            models[h] = e;
        }
    }
}
