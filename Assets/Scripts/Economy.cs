using System.Collections.Generic;
using UnityEngine;

// Money like in CS 2 competitive: RoundManager pays the rewards and opens the buy time; the buy menu, the bots and the HUD read it.
// - start $800, at most $16000;
// - a kill: the killer weapon's killReward (knife $1500, SMG $600, AWP $100 ...), a teammate killed: -$300;
// - round win $3250, round loss $1400 + $500 for every loss in a row before it (at most $3400); a draw is a loss for both teams;
// - buying: the first BuyTimeLength seconds of a round, counted from the start of the freeze time (BuyMenu, BotBuyer),
//   and only inside the buy zone of the own side (RoundManager.BuyZones: a circle around each spawn; normal games only).
// Off (Enabled = false) in simulations and in the solo mode, then nothing costs money.
public static class Economy
{
    public const int StartMoney = 800;
    public const int MaxMoney = 16000;

    public const int RoundWinReward = 3250;
    public const int LossBonusBase = 1400;
    public const int LossBonusStep = 500;
    public const int LossBonusMax = 3400;
    public const int TeamKillPenalty = 300;
    // used when the killer weapon is not known
    public const int DefaultKillReward = 300;

    // the reasons sent with MoneyChanged (ReasonText gives the Russian text for the HUD)
    public const string ReasonKill = "kill";
    public const string ReasonTeamKill = "team kill";
    public const string ReasonRoundWin = "round win";
    public const string ReasonRoundLoss = "round loss";
    public const string ReasonBuy = "buy";

    // the default weapons every new or dead player gets at the start of a round
    public const string KnifeName = "Knife";
    public const string DefaultPistolT = "Glock-18";
    public const string DefaultPistolCT = "USP-S";

    public static bool Enabled;
    // seconds of buying at the start of a round
    public static float BuyTimeLength = 20f;
    // when the buy time of the current round ends (Time.time)
    public static float BuyTimeEnd;

    public static bool InBuyTime => Time.time < BuyTimeEnd;
    public static float BuyTimeLeft => Mathf.Max(0f, BuyTimeEnd - Time.time);

    // player, change (+ reward / - price), reason ("kill", "round win", "buy" ...) — for the HUD
    public static event System.Action<Health, int, string> MoneyChanged;

    static readonly Dictionary<Health, int> money = new Dictionary<Health, int>();
    static WeaponData[] weapons;

    // play mode without a domain reload keeps the statics: start clean
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        money.Clear();
        weapons = null;
        Enabled = false;
        BuyTimeEnd = 0f;
        MoneyChanged = null;
    }

    public static int MoneyOf(Health h)
    {
        if (h == null) return 0;
        if (!money.TryGetValue(h, out int m)) m = StartMoney;
        return m;
    }

    public static void Set(Health h, int amount)
    {
        if (h == null) return;
        money[h] = Mathf.Clamp(amount, 0, MaxMoney);
    }

    public static void Add(Health h, int amount, string reason)
    {
        if (h == null || amount == 0) return;
        int before = MoneyOf(h);
        Set(h, before + amount);
        int change = MoneyOf(h) - before;
        if (change != 0) MoneyChanged?.Invoke(h, change, reason);
    }

    // true and the money is taken if the player has enough (always true when the economy is off)
    public static bool TrySpend(Health h, int amount, string reason = "buy")
    {
        if (!Enabled) return true;
        if (h == null || MoneyOf(h) < amount) return false;
        if (amount <= 0) return true;
        Set(h, MoneyOf(h) - amount);
        MoneyChanged?.Invoke(h, -amount, reason);
        return true;
    }

    public static void ResetAll()
    {
        money.Clear();
    }

    // the money for a lost round; lossesInRow counts this loss too (1 = the first loss after a win or the match start)
    public static int LossBonus(int lossesInRow)
    {
        int extra = Mathf.Max(0, lossesInRow - 1) * LossBonusStep;
        return Mathf.Min(LossBonusBase + extra, LossBonusMax);
    }

    // the money for a kill with this weapon
    public static int KillReward(WeaponData weapon)
    {
        return weapon != null ? weapon.killReward : DefaultKillReward;
    }

    // ---------------- weapons ----------------

    // every weapon in Resources/Weapons, cheapest first
    public static WeaponData[] Weapons
    {
        get
        {
            if (weapons == null)
            {
                weapons = Resources.LoadAll<WeaponData>("Weapons");
                System.Array.Sort(weapons, (a, b) =>
                {
                    int c = a.price.CompareTo(b.price);
                    return c != 0 ? c : string.CompareOrdinal(a.weaponName, b.weaponName);
                });
            }
            return weapons;
        }
    }

    // a weapon by its weaponName (or asset name), null if there is none
    public static WeaponData Find(string weaponName)
    {
        if (string.IsNullOrEmpty(weaponName)) return null;
        foreach (WeaponData w in Weapons)
            if (w != null && string.Equals(w.weaponName, weaponName, System.StringComparison.OrdinalIgnoreCase)) return w;
        foreach (WeaponData w in Weapons)
            if (w != null && string.Equals(w.name, weaponName, System.StringComparison.OrdinalIgnoreCase)) return w;
        return null;
    }

    // inside one of the buy zones of his team (true when there are no zones, e.g. the solo mode)
    public static bool InBuyZone(Health h)
    {
        if (h == null) return false;
        bool any = false;
        foreach (RoundManager.BuyZone z in RoundManager.BuyZones)
        {
            if (z.team != h.team) continue;
            any = true;
            Vector3 d = h.transform.position - z.center;
            d.y = 0f;
            if (d.sqrMagnitude <= z.radius * z.radius) return true;
        }
        return !any;
    }

    // the player may buy now: always when the economy is off (solo), else alive, in the buy time and in his buy zone
    public static bool CanBuy(Health h)
    {
        if (h == null || h.IsDead) return false;
        if (!Enabled) return true;
        return InBuyTime && InBuyZone(h);
    }

    // the knife is never bought, everything else can be
    public static bool Buyable(WeaponData d)
    {
        return d != null && d.category != WeaponCategory.Melee;
    }

    // -1 = both teams, 0 = only T, 1 = only CT
    public static bool TeamCanBuy(WeaponData d, int team)
    {
        return d != null && (d.team < 0 || d.team == team);
    }

    public static WeaponData Knife()
    {
        WeaponData k = Find(KnifeName);
        if (k != null) return k;
        foreach (WeaponData w in Weapons)
            if (w != null && w.category == WeaponCategory.Melee) return w;
        return null;
    }

    // T: Glock-18, CT: USP-S; without them the cheapest pistol of the team
    public static WeaponData DefaultPistol(int team)
    {
        WeaponData p = Find(team == 1 ? DefaultPistolCT : DefaultPistolT);
        if (p != null) return p;
        foreach (WeaponData w in Weapons)
            if (w != null && w.category == WeaponCategory.Pistol && TeamCanBuy(w, team)) return w;
        return null;
    }

    // the Russian text of a MoneyChanged reason, for the HUD
    public static string ReasonText(string reason)
    {
        switch (reason)
        {
            case ReasonKill: return "Убийство противника";
            case ReasonTeamKill: return "Убийство союзника";
            case ReasonRoundWin: return "Победа в раунде";
            case ReasonRoundLoss: return "Поражение в раунде";
            case ReasonBuy: return "Покупка";
            default: return reason ?? "";
        }
    }
}
