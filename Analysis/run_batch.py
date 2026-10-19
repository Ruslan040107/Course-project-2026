"""
Runs the built game in the background (no window) and collects the round results.

One experiment from the command line:
    python run_batch.py --exe "../Builds/Sim/Course project.exe" --jobs 4 --rounds 1000 --name stealth_1_0 --stealth0 1 --radius 0.7 --swap

A series from a file (see experiments.example.json):
    python run_batch.py --exe "../Builds/Sim/Course project.exe" --jobs 4 --config experiments.example.json

The rounds are split between --jobs game processes. Each process writes its own CSV and log
into data/_parts/, then the parts are joined into data/<name>.csv and analysed.
--swap also runs the mirrored setup (team 0 and team 1 settings exchanged) as <name>_swap and
prints the result of "side A" (the settings of team 0) averaged over both sides.
"""

import argparse
import json
import os
import subprocess
import sys
import time

import pandas as pd

from analyze import counts, print_stats, report, stats

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
PARTS = os.path.join(DATA, "_parts")

# settings that are passed to the game as "-<key> <value>"
PARAMS = ["map", "bots", "roundtime", "timescale",
          "stealth0", "stealth1", "penalty", "radius", "reveal",
          "weapon0", "weapon1", "bhop0", "bhop1", "penetration", "ff", "sneak"]

# pairs that are exchanged for the mirrored run
SWAP = [("stealth0", "stealth1"), ("weapon0", "weapon1"), ("bhop0", "bhop1")]


def swapped(params):
    p = dict(params)
    for a, b in SWAP:
        va, vb = params.get(a), params.get(b)
        p.pop(a, None)
        p.pop(b, None)
        if vb is not None:
            p[a] = vb
        if va is not None:
            p[b] = va
    return p


def split(total, jobs):
    jobs = max(1, min(jobs, total))
    base, extra = divmod(total, jobs)
    return [base + (1 if i < extra else 0) for i in range(jobs)]


def rows_in(path):
    # rounds already written by a running process (header line is not counted)
    try:
        with open(path, "r", encoding="utf-8") as f:
            return max(0, sum(1 for _ in f) - 1)
    except OSError:
        return 0


def tail(path, lines=25):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return "".join(f.readlines()[-lines:])
    except OSError:
        return "(no log file)"


def command(exe, rounds, seed, csv, log, params):
    cmd = [exe, "-batchmode", "-nographics", "-sim",
           "-rounds", str(rounds), "-seed", str(seed),
           "-csv", csv, "-logFile", log]
    for key in PARAMS:
        if params.get(key) is not None:
            cmd += ["-" + key, str(params[key])]
    return cmd


def run(exe, name, rounds, params, jobs, seed, timeout, dry_run, force):
    out = os.path.join(DATA, name + ".csv")
    if os.path.exists(out) and not force and not dry_run:
        print(f"[{name}] {out} already exists, skipped (use --force to run it again)")
        return pd.read_csv(out)

    os.makedirs(PARTS, exist_ok=True)
    parts = []
    for i, r in enumerate(split(rounds, jobs)):
        csv = os.path.join(PARTS, f"{name}_{i}.csv")
        log = os.path.join(PARTS, f"{name}_{i}.log")
        parts.append((r, csv, log, command(exe, r, seed + i, csv, log, params)))

    print(f"\n=== {name}: {rounds} rounds in {len(parts)} process(es), settings {params}")
    if dry_run:
        for _, _, _, cmd in parts:
            print("  " + subprocess.list2cmdline(cmd))
        return None

    for _, csv, log, _ in parts:
        for path in (csv, log):
            if os.path.exists(path):
                os.remove(path)

    procs = [subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
             for _, _, _, cmd in parts]
    started = time.time()
    try:
        while any(p.poll() is None for p in procs):
            done = sum(rows_in(csv) for _, csv, _, _ in parts)
            elapsed = time.time() - started
            print(f"\r  {done}/{rounds} rounds, {elapsed:.0f} s", end="", flush=True)
            if elapsed > timeout:
                print(f"\n  timeout after {timeout} s, stopping the processes")
                for p in procs:
                    p.kill()
                break
            time.sleep(2)
    except KeyboardInterrupt:
        print("\n  stopped by Ctrl+C")
        for p in procs:
            p.kill()
        raise
    print(f"\r  {sum(rows_in(csv) for _, csv, _, _ in parts)}/{rounds} rounds, {time.time() - started:.0f} s")

    frames = []
    for i, ((r, csv, log, _), p) in enumerate(zip(parts, procs)):
        got = rows_in(csv)
        if got < r:
            print(f"  part {i}: only {got}/{r} rounds (exit code {p.returncode}). End of {log}:")
            print(tail(log))
        if got > 0:
            df = pd.read_csv(csv)
            df["part"] = i
            frames.append(df)

    if not frames:
        print(f"[{name}] no results")
        return None

    df = pd.concat(frames, ignore_index=True)
    df["round"] = range(1, len(df) + 1)
    df.to_csv(out, index=False)
    print(f"  saved {out}")
    report(df)
    return df


def experiment(exe, e, args):
    name = e["name"]
    rounds = int(e.get("rounds", args.rounds))
    params = {k: v for k, v in e.get("params", {}).items() if k in PARAMS}
    unknown = set(e.get("params", {})) - set(PARAMS)
    if unknown:
        print(f"[{name}] unknown settings ignored: {sorted(unknown)}")
    params.setdefault("timescale", args.timescale)

    a = run(exe, name, rounds, params, args.jobs, args.seed, args.timeout, args.dry_run, args.force)
    if not e.get("swap", False):
        return

    b = run(exe, name + "_swap", rounds, swapped(params), args.jobs, args.seed + 1000,
            args.timeout, args.dry_run, args.force)
    if a is None or b is None:
        return

    # side A = team 0 in the first run and team 1 in the mirrored run
    w0a, w1a, da = counts(a)
    w0b, w1b, db = counts(b)
    print(f"\n--- {name}, both sides together (side A = team 0 settings of the first run)")
    print(f"side A: {w0a + w1b}, side B: {w1a + w0b}, draws: {da + db}")
    print_stats(stats(w0a + w1b, w1a + w0b), "p(side A)")


def main():
    ap = argparse.ArgumentParser(description="Background bot-vs-bot experiments")
    ap.add_argument("--exe", required=True, help="path to the built game, e.g. ../Builds/Sim/Course project.exe")
    ap.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 2) // 2), help="game processes at the same time")
    ap.add_argument("--rounds", type=int, default=1000, help="rounds per experiment")
    ap.add_argument("--name", help="experiment name (file data/<name>.csv)")
    ap.add_argument("--config", help="JSON file with a list of experiments")
    ap.add_argument("--seed", type=int, default=1, help="first random seed, each process gets its own")
    ap.add_argument("--timescale", type=float, default=10, help="game speed inside each process")
    ap.add_argument("--timeout", type=float, default=3 * 3600, help="seconds before the processes are stopped")
    ap.add_argument("--swap", action="store_true", help="also run the mirrored setup and average both sides")
    ap.add_argument("--dry-run", action="store_true", help="only print the commands")
    ap.add_argument("--force", action="store_true", help="run again even if data/<name>.csv exists")
    for key in PARAMS:
        if key != "timescale":
            ap.add_argument("--" + key)
    args = ap.parse_args()

    exe = os.path.abspath(args.exe)
    if not os.path.exists(exe) and not args.dry_run:
        sys.exit(f"game not found: {exe}\nBuild it first: File > Build Profiles > Windows > Build into Builds/Sim")

    if args.config:
        with open(args.config, "r", encoding="utf-8") as f:
            cfg = json.load(f)
        items = cfg["experiments"] if isinstance(cfg, dict) else cfg
        defaults = cfg.get("defaults", {}) if isinstance(cfg, dict) else {}
        for e in items:
            merged = dict(e)
            merged["params"] = {**defaults, **e.get("params", {})}
            experiment(exe, merged, args)
    else:
        if not args.name:
            sys.exit("give --name or --config")
        params = {k: getattr(args, k) for k in PARAMS if k != "timescale" and getattr(args, k) is not None}
        experiment(exe, {"name": args.name, "rounds": args.rounds, "params": params, "swap": args.swap}, args)


if __name__ == "__main__":
    main()
