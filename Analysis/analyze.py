import sys
import math
import pandas as pd


def stats(wins0, wins1):
    # p = share of decisive rounds won by team 0, draws are not counted
    n = wins0 + wins1
    if n == 0:
        return {"n": 0, "p": float("nan"), "lo": float("nan"), "hi": float("nan"), "z": float("nan")}
    p = wins0 / n
    se = math.sqrt(p * (1 - p) / n)
    z = (p - 0.5) / math.sqrt(0.25 / n)
    return {"n": n, "p": p, "lo": p - 1.96 * se, "hi": p + 1.96 * se, "z": z}


def counts(df):
    wins0 = int((df["winner"] == 0).sum())
    wins1 = int((df["winner"] == 1).sum())
    draws = int((df["winner"] == -1).sum())
    return wins0, wins1, draws


def print_stats(s, label="p(team0)"):
    if s["n"] == 0:
        print("no decisive rounds")
        return
    print(f"{label} = {s['p']:.3f}, 95% CI [{s['lo']:.3f}, {s['hi']:.3f}]")
    print(f"z = {s['z']:.2f}:", "no evidence of imbalance" if abs(s["z"]) < 1.96 else "IMBALANCED")


def report(df):
    wins0, wins1, draws = counts(df)
    print(f"rounds: {len(df)}, team0: {wins0}, team1: {wins1}, draws: {draws}")
    if {"alive0", "alive1"}.issubset(df.columns):
        tied = df[df["winner"] == -1]
        timeouts = ((tied["alive0"] > 0) & (tied["alive1"] > 0)).sum()
        mutual_eliminations = ((tied["alive0"] == 0) & (tied["alive1"] == 0)).sum()
        print(f"draws by cause: time limit {timeouts}, both teams eliminated {mutual_eliminations}")
    print_stats(stats(wins0, wins1))
    print(f"mean round length: {df['duration'].mean():.1f} s")
    print("team sizes:", sorted(int(x) for x in df["size0"].unique()), sorted(int(x) for x in df["size1"].unique()))
    if "stealth0" in df:
        print("stealth levels:", sorted(int(x) for x in df["stealth0"].unique()), sorted(int(x) for x in df["stealth1"].unique()))
    if "penalty" in df:
        print("penalty / radius / reveal:", df[["penalty", "radius", "reveal"]].iloc[0].tolist())
    if "weapon0" in df:
        print("weapons:", df["weapon0"].iloc[0], "vs", df["weapon1"].iloc[0])
    if "map" in df:
        print("map:", df["map"].iloc[0])
    if "bhop0" in df:
        print("bhop levels:", int(df["bhop0"].iloc[0]), "vs", int(df["bhop1"].iloc[0]))


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("usage: python analyze.py <file.csv>")
        sys.exit(1)
    report(pd.read_csv(sys.argv[1]))
