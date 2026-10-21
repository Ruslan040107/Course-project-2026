using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

// Weapon models in the hands of bots (third person view).
// With a character model (CharacterBody) the gun hangs under the model's right fist (CharacterBody.WeaponMount,
// grip at the mount, barrel +Z). CharacterBody holds a gun with both hands: its arm IK moves the right wrist so the
// mount is where the gun should be (along the aim, stock at the shoulder or pistol forward) and puts the left hand
// on the fore-end; a knife stays in the fist of the animation.
// The player with a character model gets one too: his model and gun are hidden only from his own camera
// (CharacterBody.LocalBodyLayer), RefreshRenderers puts a new gun on that layer at once.
// Without a character model it hangs under the bot's head with an offset, as before; the head is turned
// to the enemy by BotController, so the gun points where the bot shoots.
// It changes when the weapon changes. CombatEffects adds this component (never in simulation).
// On a bot's reload the empty magazine (the gun's Mag part) falls out (CombatEffects.DropPiece: a copy lands on the
// floor and lies there a second) and the gun has no magazine until the new one is in; shotguns throw out their hulls
// (after the pump, at once for the XM1014, on reload for the Sawed-Off) and the R8 drops its cases on reload.
// The character the camera shows in first person (ViewModel.IsFirstPerson: the player, a watched teammate) drops them
// from the first person model instead.
public class BotWeaponModels : MonoBehaviour
{
    // the name of the object that holds a gun (CharacterBody does not hide it)
    public const string HolderName = "Weapon model";

    [Tooltip("from the head, in meters: right, down, forward (only without a character model)")]
    public Vector3 offset = new Vector3(0.28f, -0.35f, 0.42f);

    // parts of the reload: the magazine falls out, the new one is in (as GameAudio's mag_in), the R8 cases drop
    const float MagDropAt = 0.22f, MagInAt = 0.6f, CasesDropAt = 0.3f;
    // a pump shotgun throws the hull out when the pump goes back after the shot, seconds
    const float PumpHullDelay = 0.3f;
    const int RevolverCases = 4;

    class Entry
    {
        public GameObject holder;
        public WeaponData data;
        public Transform parent;
        public Transform muzzle;
        // parts of the gun model (null if it has none)
        public Transform mag, eject, cylinder;
        public Health owner;
        public Weapon weapon;
        public BotController bot;
        public NavMeshAgent agent;
        // a reload that is going on: its magazine fell out, its cases dropped; the hull after a pump, when (-1 none)
        public bool reloading, magDropped, casesDropped;
        public float hullAt = -1f;
    }

    static BotWeaponModels instance;
    readonly Dictionary<Health, Entry> models = new Dictionary<Health, Entry>();
    // entries with a reload or a hull to show
    readonly List<Entry> busy = new List<Entry>();
    float nextCheck;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        instance = null;
    }

    void OnEnable()
    {
        instance = this;
        Weapon.AnyReloadStarted += OnReload;
        Weapon.AnyShot += OnShot;
        RoundManager.RoundStarted += CheckSoon;
        RoundManager.Halftime += CheckSoon;
    }

    void OnDisable()
    {
        if (instance == this) instance = null;
        Weapon.AnyReloadStarted -= OnReload;
        Weapon.AnyShot -= OnShot;
        RoundManager.RoundStarted -= CheckSoon;
        RoundManager.Halftime -= CheckSoon;
        foreach (Entry e in busy) ShowMag(e);
        busy.Clear();
    }

    // a new round (new weapons) or the halftime (new character models with a new mount): the guns are hung at once,
    // not up to half a second later
    void CheckSoon()
    {
        nextCheck = 0f;
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
        UpdateBusy();

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

            if (e != null)
            {
                busy.Remove(e);
                if (e.holder != null) Destroy(e.holder);
            }
            e = new Entry { data = d, parent = parent, owner = h, weapon = weapon, bot = bot, agent = h.GetComponent<NavMeshAgent>() };

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
                e.mag = ViewModel.FindChild(holder, "Mag");
                e.eject = ViewModel.FindChild(holder, "Eject");
                e.cylinder = ViewModel.FindChild(holder, "Cylinder");

                // the right layer at once (the player's own gun must not show in his view), and the hold
                // measures the new gun (stock, fore-end) on its next frame
                if (body != null && parent == mount) body.RefreshRenderers();
            }
            models[h] = e;
        }
    }

    // ---------------- magazines and shells ----------------

    // the gun model of this character, if its weapon is still the one the model shows and nobody sees it in first person
    Entry EntryOf(Health h, WeaponData d)
    {
        if (h == null || !models.TryGetValue(h, out Entry e) || e.holder == null || e.data != d) return null;
        return ViewModel.IsFirstPerson(h) ? null : e;
    }

    void OnReload(Weapon w)
    {
        if (w == null) return;
        Entry e = EntryOf(w.Owner, w.data);
        if (e == null) return;

        if (ViewModel.IsBreakAction(w.data))
        {
            // the Sawed-Off opens: its two empty hulls drop out of the breech
            DropShell(e, false);
            DropShell(e, false);
            return;
        }
        if (w.IsShellReload || (e.mag == null && e.cylinder == null)) return;

        e.reloading = true;
        e.magDropped = false;
        e.casesDropped = false;
        if (!busy.Contains(e)) busy.Add(e);
    }

    void OnShot(ShotInfo s)
    {
        // one hull for the whole shell (the first pellet), none for the knife and the Zeus
        if (s.pellet || s.melee || s.taser || s.weapon == null || !s.weapon.IsShotgun) return;
        if (ViewModel.IsBreakAction(s.weapon)) return;
        Entry e = EntryOf(s.shooter, s.weapon);
        if (e == null) return;

        if (ViewModel.IsPumpAction(s.weapon))
        {
            e.hullAt = Time.time + PumpHullDelay;
            if (!busy.Contains(e)) busy.Add(e);
        }
        else
        {
            // semi-automatic (XM1014): the hull flies out with the shot
            DropShell(e, false);
        }
    }

    // the timed parts: the magazine out and in again, the R8 cases, the hull after the pump
    void UpdateBusy()
    {
        for (int i = busy.Count - 1; i >= 0; i--)
        {
            Entry e = busy[i];
            bool valid = e.holder != null && e.owner != null && !e.owner.IsDead && e.weapon != null && e.weapon.data == e.data;
            // the camera went into this character: its first person model shows the rest
            bool firstPerson = valid && ViewModel.IsFirstPerson(e.owner);

            if (e.hullAt >= 0f && (!valid || Time.time >= e.hullAt))
            {
                if (valid && !firstPerson) DropShell(e, false);
                e.hullAt = -1f;
            }

            if (e.reloading)
            {
                if (!valid || !e.weapon.IsReloading)
                {
                    e.reloading = false;
                }
                else
                {
                    float p = e.weapon.ReloadProgress;
                    if (!e.magDropped && p >= MagDropAt && p < MagInAt)
                    {
                        e.magDropped = true;
                        if (!firstPerson) DropMag(e);
                    }
                    if (e.cylinder != null && !e.casesDropped && p >= CasesDropAt)
                    {
                        e.casesDropped = true;
                        if (!firstPerson)
                            for (int c = 0; c < RevolverCases; c++) DropShell(e, true);
                    }
                    // the new magazine is in
                    if (p >= MagInAt)
                    {
                        ShowMag(e);
                        if (e.casesDropped || e.cylinder == null) e.reloading = false;
                    }
                }
            }

            if (!e.reloading && e.hullAt < 0f)
            {
                ShowMag(e);
                busy.RemoveAt(i);
            }
        }
    }

    // the empty magazine leaves the gun: a real-size copy falls, the gun shows no magazine until the new one is in
    void DropMag(Entry e)
    {
        if (e.mag == null) return;
        Transform h = e.holder.transform;
        Vector3 velocity = -h.up * 1f + Velocity(e) + Random.insideUnitSphere * 0.15f;
        ViewModel.WorldPose(e.mag, out Quaternion rot, out Vector3 scale);
        if (CombatEffects.DropPiece(e.mag.gameObject, "mag:" + e.data.weaponName, e.mag.position, rot, scale,
                                    velocity, Random.insideUnitSphere * 150f, Eyes(e)))
            e.mag.gameObject.SetActive(false);
    }

    void ShowMag(Entry e)
    {
        if (e.mag != null && !e.mag.gameObject.activeSelf) e.mag.gameObject.SetActive(true);
    }

    // an empty shell: out to the right of the gun (the Eject point of the models is on their left side, it is mirrored),
    // or down out of the R8 cylinder
    void DropShell(Entry e, bool fromCylinder)
    {
        Transform h = e.holder.transform;
        Vector3 pos;
        Quaternion rot;
        Vector3 velocity;
        if (fromCylinder && e.cylinder != null)
        {
            pos = e.cylinder.position + Random.insideUnitSphere * 0.012f;
            rot = Quaternion.LookRotation(h.up, h.forward);
            velocity = -h.up * Random.Range(0.3f, 0.8f) + h.right * Random.Range(-0.3f, 0.3f);
        }
        else
        {
            Vector3 local = e.eject != null ? h.InverseTransformPoint(e.eject.position) : new Vector3(0.03f, 0.05f, 0.1f);
            local.x = Mathf.Abs(local.x);
            pos = h.TransformPoint(local);
            rot = Quaternion.LookRotation(h.forward, h.right);
            velocity = h.right * Random.Range(1.2f, 2f) + h.up * Random.Range(0.9f, 1.5f) - h.forward * Random.Range(0f, 0.3f);
        }
        CombatEffects.DropShell(e.data, pos, rot, velocity + Velocity(e), Random.insideUnitSphere * 720f, Eyes(e));
    }

    static Vector3 Velocity(Entry e)
    {
        return e.agent != null && e.agent.enabled ? e.agent.velocity : Vector3.zero;
    }

    // a point the gun surely is not behind a wall from: the bot's eyes
    static Vector3 Eyes(Entry e)
    {
        if (e.bot != null && e.bot.head != null) return e.bot.head.position;
        return e.owner.transform.position + Vector3.up * 0.6f;
    }
}
