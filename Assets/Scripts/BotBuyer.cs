using System.Collections.Generic;
using UnityEngine;

// The weapons and the buying of one bot in a normal 5v5 game (RoundManager adds it; never in simulations,
// where the bots keep the fixed -weapon0/-weapon1 weapons and there is no money).
// Like the player: a new or dead bot starts a round with the team's default pistol (and the knife),
// a survivor keeps his weapons, each with a full magazine and full reserve (like CS; EquipBest also ends a reload
// left from the last round). Then it buys at the round start (RoundManager calls Buy):
// - full buy (>= FullBuyMoney): a rifle of its team (T: AK-47, CT: M4A4 or M4A1-S), about awpChance of them an AWP;
// - force buy (>= ForceBuyMoney): an SMG, a shotgun or a cheap rifle (Galil AR / FAMAS);
// - eco: sometimes a better pistol (P250, Tec-9 / Five-SeveN, Desert Eagle), otherwise it saves.
// A bot holds one weapon (BotController uses one Weapon): the primary if it has one, else the pistol, else the knife;
// when the primary has no bullets left at all it takes the pistol, then the knife.
// Weapons on the floor (WeaponDrops, normal 5v5 games): a dead bot drops its primary, else its pistol (TakeDeathDrop);
// walking over a primary it takes it when it has none, an empty one or a cheaper one (WantsPickup / PickUp; in a fight
// only when its own primary is gone); a bought weapon that replaces another one drops the old one, like in CS.
public class BotBuyer : MonoBehaviour
{
    public const int FullBuyMoney = 4000;
    public const int ForceBuyMoney = 2000;
    // the force buy takes rifles up to this price (Galil AR $1800, FAMAS $2050)
    public const int CheapRiflePrice = 2100;

    [Tooltip("part of the rich bots that buy an AWP instead of a rifle")] [Range(0f, 1f)] public float awpChance = 0.15f;
    [Tooltip("chance that a poor bot buys a better pistol instead of saving")] [Range(0f, 1f)] public float ecoPistolChance = 0.5f;

    public WeaponData primary;
    public WeaponData pistol;

    Health health;
    Weapon weapon;
    // -1 = a full magazine / full reserve (Weapon.Equip)
    int primaryAmmo = -1, primaryReserve = -1;
    int pistolAmmo = -1, pistolReserve = -1;
    bool initialized;

    static readonly string[] EcoPistolsT = { "P250", "Tec-9", "Desert Eagle" };
    static readonly string[] EcoPistolsCT = { "P250", "Five-SeveN", "Desert Eagle" };
    static readonly List<WeaponData> candidates = new List<WeaponData>();
    static readonly List<float> weights = new List<float>();

    void Awake()
    {
        health = GetComponent<Health>();
        BotController bc = GetComponent<BotController>();
        weapon = bc != null && bc.weapon != null ? bc.weapon : GetComponentInChildren<Weapon>(true);
    }

    int Team => health != null ? health.team : 0;

    // keep = the bot survived the last round: its weapons stay with a full magazine and full reserve
    public void NewRound(bool keep)
    {
        if (weapon == null) return;

        if (!keep || !initialized)
        {
            primary = null;
            pistol = Economy.DefaultPistol(Team);
        }
        primaryAmmo = primaryReserve = -1;
        pistolAmmo = pistolReserve = -1;
        initialized = true;
    }

    // ---------------- buying ----------------

    public void Buy()
    {
        if (weapon == null) return;
        if (!initialized) NewRound(false);

        int team = Team;
        int money = Economy.MoneyOf(health);

        // a bot that lost its pistol gets the default one again (it is free)
        if (pistol == null)
        {
            pistol = Economy.DefaultPistol(team);
            pistolAmmo = pistolReserve = -1;
        }

        // a primary only if there is none, or a rich bot with an SMG / shotgun / machine gun gets a rifle
        bool weakPrimary = primary != null && primary.category != WeaponCategory.Rifle && primary.category != WeaponCategory.Sniper;
        if (primary == null || (weakPrimary && money >= FullBuyMoney))
        {
            WeaponData pick = null;
            if (money >= FullBuyMoney) pick = FullBuy(team, money);
            else if (money >= ForceBuyMoney && primary == null) pick = ForceBuy(team, money);

            if (pick != null && Economy.TrySpend(health, pick.price, Economy.ReasonBuy))
            {
                // the old SMG / shotgun falls to the floor
                if (primary != null) WeaponDrops.Drop(health, new GunState { data = primary, ammo = primaryAmmo, reserve = primaryReserve });
                primary = pick;
                primaryAmmo = primaryReserve = -1;
                money = Economy.MoneyOf(health);
            }
        }

        // eco round: a better pistol or nothing
        if (primary == null && money < ForceBuyMoney && IsDefaultPistol(pistol) && Random.value < ecoPistolChance)
        {
            WeaponData p = EcoPistol(team, money);
            if (p != null && Economy.TrySpend(health, p.price, Economy.ReasonBuy))
            {
                if (pistol != null) WeaponDrops.Drop(health, new GunState { data = pistol, ammo = pistolAmmo, reserve = pistolReserve });
                pistol = p;
                pistolAmmo = pistolReserve = -1;
            }
        }
    }

    WeaponData FullBuy(int team, int money)
    {
        WeaponData awp = Economy.Find("AWP");
        if (Affordable(awp, team, money) && Random.value < awpChance) return awp;

        WeaponData rifle;
        if (team == 1)
        {
            bool m4a1s = Random.value < 0.5f;
            rifle = Economy.Find(m4a1s ? "M4A1-S" : "M4A4");
            if (!Affordable(rifle, team, money)) rifle = Economy.Find(m4a1s ? "M4A4" : "M4A1-S");
        }
        else
        {
            rifle = Economy.Find("AK-47");
        }
        if (Affordable(rifle, team, money)) return rifle;

        // the named rifles are missing: the most expensive rifle of the team the bot can pay for
        WeaponData best = null;
        foreach (WeaponData w in Economy.Weapons)
            if (w.category == WeaponCategory.Rifle && Affordable(w, team, money) && (best == null || w.price > best.price))
                best = w;
        return best;
    }

    // a random SMG, shotgun or cheap rifle; the rifles are liked most, the shotguns least
    WeaponData ForceBuy(int team, int money)
    {
        candidates.Clear();
        weights.Clear();
        foreach (WeaponData w in Economy.Weapons)
        {
            if (!Affordable(w, team, money)) continue;
            float weight = 0f;
            if (w.category == WeaponCategory.Rifle && w.price <= CheapRiflePrice) weight = 3f;
            else if (w.category == WeaponCategory.SMG) weight = 2f;
            else if (w.category == WeaponCategory.Shotgun) weight = 1f;
            if (weight <= 0f) continue;
            candidates.Add(w);
            weights.Add(weight);
        }
        return Pick();
    }

    WeaponData EcoPistol(int team, int money)
    {
        candidates.Clear();
        weights.Clear();
        foreach (string n in team == 1 ? EcoPistolsCT : EcoPistolsT)
        {
            WeaponData w = Economy.Find(n);
            if (!Affordable(w, team, money)) continue;
            candidates.Add(w);
            weights.Add(1f);
        }
        return Pick();
    }

    static WeaponData Pick()
    {
        float total = 0f;
        foreach (float w in weights) total += w;
        if (candidates.Count == 0 || total <= 0f) return null;

        float r = Random.value * total;
        for (int i = 0; i < candidates.Count; i++)
        {
            r -= weights[i];
            if (r <= 0f) return candidates[i];
        }
        return candidates[candidates.Count - 1];
    }

    static bool Affordable(WeaponData w, int team, int money)
    {
        return w != null && Economy.Buyable(w) && Economy.TeamCanBuy(w, team) && (!Economy.Enabled || w.price <= money);
    }

    bool IsDefaultPistol(WeaponData p)
    {
        return p == null || p == Economy.DefaultPistol(Team);
    }

    // ---------------- the weapon in the hands ----------------

    // the primary, else the pistol, else the knife (with the ammo it has left)
    public void EquipBest()
    {
        if (weapon == null) return;
        if (primary != null && (primaryAmmo != 0 || primaryReserve != 0)) weapon.Equip(primary, primaryAmmo, primaryReserve);
        else if (pistol != null && (pistolAmmo != 0 || pistolReserve != 0)) weapon.Equip(pistol, pistolAmmo, pistolReserve);
        else EquipKnife();
    }

    void EquipKnife()
    {
        WeaponData knife = Economy.Knife();
        if (knife != null) weapon.Equip(knife);
        else if (primary != null) weapon.Equip(primary);
        else if (pistol != null) weapon.Equip(pistol);
    }

    // ---------------- weapons on the floor (WeaponDrops) ----------------

    // the weapon a dead bot leaves on the floor: the primary, else the pistol (never the knife), with its ammo;
    // the bot loses it (the next round gives it the default pistol anyway)
    public GunState TakeDeathDrop()
    {
        GunState gun = default;
        if (weapon == null || !initialized) return gun;
        SaveCurrent();
        if (primary != null)
        {
            gun = new GunState { data = primary, ammo = primaryAmmo, reserve = primaryReserve };
            primary = null;
            primaryAmmo = primaryReserve = -1;
        }
        else if (pistol != null)
        {
            gun = new GunState { data = pistol, ammo = pistolAmmo, reserve = pistolReserve };
            pistol = null;
            pistolAmmo = pistolReserve = -1;
        }
        return gun;
    }

    // the bot walks over this weapon: does it take it? Only a primary with bullets in it, when the bot has no primary,
    // an empty one or (not in a fight) a cheaper one
    public bool WantsPickup(GunState gun, bool inFight)
    {
        if (weapon == null || health == null || health.IsDead || !gun.Valid || gun.Empty) return false;
        if (gun.data.Slot != WeaponSlot.Primary) return false;
        if (primary == null || PrimaryEmpty()) return true;
        return !inFight && primary.price < gun.data.price;
    }

    // takes a primary from the floor into the hands; the old primary comes back (not Valid if there was none)
    // so WeaponDrops drops it
    public GunState PickUp(GunState gun)
    {
        GunState old = default;
        if (weapon == null || !gun.Valid) return old;
        if (!initialized) NewRound(false);

        SaveCurrent();
        if (primary != null) old = new GunState { data = primary, ammo = primaryAmmo, reserve = primaryReserve };
        primary = gun.data;
        primaryAmmo = gun.ammo;
        primaryReserve = gun.reserve;
        EquipBest();
        return old;
    }

    // the primary has no bullets left at all (in the hands or put away)
    bool PrimaryEmpty()
    {
        if (primary == null) return true;
        if (weapon != null && weapon.data == primary) return weapon.Ammo == 0 && weapon.Reserve == 0;
        return primaryAmmo == 0 && primaryReserve == 0;
    }

    // the ammo left in the weapon in the hands goes back to its slot
    void SaveCurrent()
    {
        if (weapon == null || weapon.data == null) return;
        if (weapon.data == primary)
        {
            primaryAmmo = weapon.Ammo;
            primaryReserve = weapon.Reserve;
        }
        else if (weapon.data == pistol)
        {
            pistolAmmo = weapon.Ammo;
            pistolReserve = weapon.Reserve;
        }
    }

    void Update()
    {
        if (weapon == null || weapon.data == null || Weapon.InfiniteAmmo) return;
        if (health != null && health.IsDead) return;
        if (weapon.IsMelee || weapon.IsReloading || weapon.Ammo > 0 || weapon.Reserve > 0) return;

        // no bullets left at all: the next weapon
        if (weapon.data == primary)
        {
            primaryAmmo = primaryReserve = 0;
            if (pistol != null && (pistolAmmo != 0 || pistolReserve != 0)) weapon.Equip(pistol, pistolAmmo, pistolReserve);
            else EquipKnife();
        }
        else if (weapon.data == pistol)
        {
            pistolAmmo = pistolReserve = 0;
            EquipKnife();
        }
    }
}
