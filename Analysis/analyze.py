import sys
import math
import pandas as pd

path = sys.argv[1]
df = pd.read_csv(path)

wins0 = int((df["winner"] == 0).sum())
wins1 = int((df["winner"] == 1).sum())
draws = int((df["winner"] == -1).sum())
n = wins0 + wins1

p = wins0 / n
se = math.sqrt(p * (1 - p) / n)
z = (p - 0.5) / math.sqrt(0.25 / n)

print(f"rounds: {len(df)}, team0: {wins0}, team1: {wins1}, draws: {draws}")
print(f"p(team0) = {p:.3f}, 95% CI [{p - 1.96 * se:.3f}, {p + 1.96 * se:.3f}]")
print(f"z = {z:.2f}:", "no evidence of imbalance" if abs(z) < 1.96 else "IMBALANCED")
print(f"mean round length: {df['duration'].mean():.1f} s")
print("team sizes:", sorted(df["size0"].unique()), sorted(df["size1"].unique()))