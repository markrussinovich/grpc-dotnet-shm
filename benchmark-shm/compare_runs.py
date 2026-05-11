#!/usr/bin/env python3
"""Compare master/phase1/phase2 SHM benchmark JSONs."""
import json
import sys

def load(p):
    with open(p) as f:
        return json.load(f)

def fmt(b):
    if b == 0:
        return "0B"
    if b < 1024:
        return f"{b}B"
    if b < 1048576:
        return f"{b//1024}KB"
    return f"{b//1048576}MB"

def by(j, mode):
    d = {}
    for r in j[mode]:
        if r["transport"] == "shm":
            d[r["size_bytes"]] = r["throughput_mb_per_s"]
    return d

if len(sys.argv) < 3:
    print("usage: compare_runs.py <master.json> <phase1.json> [<phase2.json> ...]")
    sys.exit(1)

paths = sys.argv[1:]
runs = [load(p) for p in paths]
labels = [f"r{i}" for i in range(len(paths))]
labels[0] = "master"
if len(paths) >= 2:
    labels[1] = "phase1"
if len(paths) >= 3:
    labels[2] = "phase2"

for mode in ("unary", "streaming"):
    print(f"=== {mode.upper()} SHM ===")
    headerLabels = "  ".join(f"{lab:>10}" for lab in labels)
    deltaCols = "  ".join(f"{lab+'Δ%':>8}" for lab in labels[1:])
    print(f"{'Size':<7}  {headerLabels}  {deltaCols}")
    maps = [by(r, mode) for r in runs]
    sizes = sorted(set().union(*maps))
    for s in sizes:
        if s == 0:
            continue
        vals = [m.get(s, 0) for m in maps]
        ref = vals[0]
        deltas = [(v - ref) / ref * 100 if ref else 0 for v in vals[1:]]
        valStr = "  ".join(f"{v:>10.1f}" for v in vals)
        deltaStr = "  ".join(f"{d:>+7.1f}%" for d in deltas)
        print(f"{fmt(s):<7}  {valStr}  {deltaStr}")
    print()
