"""Summarize an Azure Load Testing JMeter results CSV (brief 12b): per-label samples, error %, p50/p95/max, status codes,
and the pass/fail criteria. Usage: python summarize.py <engine1_results.csv> [<more.csv> ...] [--json out.json]"""
import csv
import math
import json
import sys
from collections import Counter, defaultdict

CRITERIA = [("features-hit", 300), ("override", 1500), ("document", 1000)]


def pct(xs, q):
    """Nearest-rank percentile (the same definition as the browser harness)."""
    xs = sorted(xs)
    return xs[min(len(xs) - 1, max(0, math.ceil(q * len(xs)) - 1))] if xs else None


def main(paths, out):
    rows = [r for p in paths for r in csv.DictReader(open(p, encoding="utf-8"))]
    by = defaultdict(list)
    codes = defaultdict(Counter)
    for r in rows:
        by[r["label"]].append(r)
        codes[r["label"]][r["responseCode"]] += 1
    t0 = min(int(r["timeStamp"]) for r in rows)
    t1 = max(int(r["timeStamp"]) + int(r["elapsed"]) for r in rows)
    summary = {"samples": len(rows), "errors": sum(r["success"] != "true" for r in rows), "startMs": t0, "endMs": t1, "labels": {}}
    summary["errorPct"] = round(100 * summary["errors"] / len(rows), 3)
    for label, rs in sorted(by.items()):
        el = [int(r["elapsed"]) for r in rs]
        summary["labels"][label] = {
            "samples": len(rs), "errorPct": round(100 * sum(r["success"] != "true" for r in rs) / len(rs), 2),
            "p50": pct(el, 0.5), "p95": pct(el, 0.95), "max": max(el), "codes": dict(codes[label]),
        }
    summary["criteria"] = [{"label": l, "p95LimitMs": lim, "p95": summary["labels"].get(l, {}).get("p95"),
                            "pass": (summary["labels"].get(l, {}).get("p95") or 10**9) <= lim} for l, lim in CRITERIA]
    summary["criteria"].append({"label": "all", "errorPctLimit": 0.5, "errorPct": summary["errorPct"], "pass": summary["errorPct"] < 0.5})
    print(f"{summary['samples']} samples, {summary['errorPct']} % errors")
    for label, s in summary["labels"].items():
        print(f"  {label:16} n={s['samples']:5}  err={s['errorPct']:5}%  p50={s['p50']:6}  p95={s['p95']:6}  max={s['max']:6}  {s['codes']}")
    for c in summary["criteria"]:
        print("  criterion", c)
    if out:
        json.dump(summary, open(out, "w", encoding="utf-8"), indent=2)


if __name__ == "__main__":
    args = sys.argv[1:]
    out = args[args.index("--json") + 1] if "--json" in args else None
    main([a for a in args if a.endswith(".csv")], out)
