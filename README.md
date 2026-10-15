# 🎯 Unity Shooter — Course Project

A course project for the **2nd year** of the **Bachelor's Programme in Applied Mathematics and Informatics (AMI)** at the **Faculty of Computer Science, HSE University**.

The goal of this project is to build a small shooter game in **Unity** featuring autonomous bots, a symmetric arena, and a rigorous calibration procedure to verify the fairness of the simulation.

---

## 📌 Overview

This project is a minimal shooter in which two automated teams of bots fight each other on a symmetric map. The main focus is on:

- **Fair calibration** — verifying that two identical teams win with probability exactly 50%, using a z-test.
- **Symmetric map** — every wall has a mirrored counterpart under a 180° rotation about the arena center, `(x, z) → (-x, -z)`. This guarantees equal conditions for both teams.
- **Bot navigation** via Unity's `NavMesh` — bots path around obstacles and cannot pass through walls.
- **Visibility mechanics** *(in development)* — cover objects and line-of-sight rules are planned so that combat is not decided immediately at spawn.

---

## 🛠️ Tech Stack

- **Unity 6** (`6000.6.5f1`)
- **C#**
- **AI Navigation** package (`com.unity.ai.navigation`)
- **Git / GitHub**
- **Python** (for calibration analysis, `Analysis/analyze.py`)

---

## 📁 Project Structure

```
Course project/
├── Assets/
│   ├── Scenes/
│   │   └── SampleScene/
│   │       ├── SampleScene.unity
│   │       └── NavMesh-Level.asset       # Baked NavMesh
│   ├── Scripts/
│   │   ├── BotController.cs              # Bot behaviour
│   │   └── ...
│   ├── Materials/
│   ├── Prefabs/
│   └── Settings/
├── Analysis/
│   ├── analyze.py                        # CSV-based round analysis
│   └── data/                             # Raw round data (CSV)
├── Packages/
├── ProjectSettings/
└── README.md
```

---

## 🗺️ Map

The map is **symmetric** about the origin `(0, 0, 0)`: each wall has a counterpart rotated 180° about the center, `(x, z) → (-x, -z)`.

| Object | Position | Scale |
|---|---|---|
| Central wall | `0, 1.5, 0` | `10, 3, 1` |
| Side wall 1 | `-10, 1.5, 6` | `1, 3, 8` |
| Side wall 2 | `10, 1.5, -6` | `1, 3, 8` |
| Block 1 | `6, 1.5, 11` | `6, 3, 1` |
| Block 2 | `-6, 1.5, -11` | `6, 3, 1` |
| Low cover 1 | `-5, 0.75, -8` | `3, 1.5, 3` |
| Low cover 2 | `5, 0.75, 8` | `3, 1.5, 3` |

All walls and the floor are children of the `Level` object, which carries a `NavMesh Surface` component configured with `Collect Objects = Current Object Hierarchy`.

---

## 🧪 Calibration

The first experiment verifies the fairness of the simulation. We run **2400 rounds** between two **identical** teams and measure the win rate of the first team.

**Result:**

$$\hat p = 0.500, \qquad \text{CI } [0.480;\ 0.521], \qquad z = 0.04$$

- Wins: **1160** and **1158** out of **2318** decisive rounds.
- Draws: **82** (≈ **3.4%**).
- z-statistic:

$$z = \frac{\hat p - 0.5}{\sqrt{0.25 / n}}$$

where `n` is the number of decisive rounds.

**History:** the simulation initially showed a bias of `p̂ ≈ 0.58` in favor of one side. After fixing navigation, map symmetry, and the round-handling order, the result converged to **0.50**, confirming the calibration is sound.

**Acceptance criterion:** `p̂ ≈ 0.5` and `|z| < 1.96` on the new map. If satisfied, the map and navigation are symmetric.

---

## 🚀 Getting Started

1. Clone the repository:
   ```bash
   git clone https://github.com/Ruslan040107/Course-project-2026-10-14_22-30-42.git
   ```
2. Open the project in **Unity 6** via Unity Hub.
3. Open `Assets/Scenes/SampleScene/SampleScene.unity`.
4. Make sure the NavMesh is baked: select `Level` → `NavMesh Surface` → **Bake**.
5. Press **Play**.

---

## 📊 Analysis

The script `Analysis/analyze.py` reads CSV files with per-round results and computes:

- Win rate of the first team `p̂`.
- 95% confidence interval.
- z-statistic for the null hypothesis `p = 0.5`.

Run:

```bash
python Analysis/analyze.py
```

---

## 🗓️ Roadmap

- [x] Base scene, bots, shooting
- [x] Symmetric map
- [x] Calibration over 2400 rounds (`p̂ = 0.50`, `z = 0.04`)
- [ ] Invisibility mechanic and bot vision rules
- [ ] Cover objects that actually influence combat
- [ ] Longer average round duration (currently ≈ 4.8 s)
- [ ] Final report and defense

---

## 👤 Author

**Ruslan**  
2nd-year student, Bachelor's Programme in Applied Mathematics and Informatics  
Faculty of Computer Science, HSE University  
GitHub: [@Ruslan040107](https://github.com/Ruslan040107)

---

## 📄 License

Educational project. Free to use for academic purposes.
