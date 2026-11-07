"""Summarize a read-only observe-stability.ps1 run (samples.jsonl) without touching the wallpaper.

Usage:
  python scripts/analyze-stability.py artifacts/analysis/stability-20261022/samples.jsonl \
      [--output report.json] [--warmup-minutes 30] [--logical-processors 16]

Process instances are separated by PID so a reinstall or restart never mixes its warm-up into a
steady-state trend. Memory classification is descriptive: a growing trend over a few hours is a
reason for a longer controlled run, not proof of a leak.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import statistics
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path

LOCAL = timezone(timedelta(hours=8))
METRICS = ("privateCommitMiB", "workingSetMiB")


def parse_time(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def local(value: datetime) -> str:
    return value.astimezone(LOCAL).isoformat(timespec="seconds")


def stats(values):
    values = [v for v in values if v is not None and math.isfinite(v)]
    if not values:
        return {"n": 0}
    ordered = sorted(values)
    return {
        "n": len(values),
        "mean": round(statistics.fmean(values), 3),
        "median": round(statistics.median(values), 3),
        "p95": round(ordered[max(0, math.ceil(len(ordered) * 0.95) - 1)], 3),
        "min": round(ordered[0], 3),
        "max": round(ordered[-1], 3),
    }


def slope_per_hour(points):
    """Least-squares slope in units per hour for (datetime, value) points."""
    if len(points) < 3:
        return None
    origin = points[0][0]
    xs = [(t - origin).total_seconds() / 3600 for t, _ in points]
    ys = [v for _, v in points]
    mx, my = statistics.fmean(xs), statistics.fmean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx == 0:
        return None
    return round(sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx, 3)


def window_median(points, start, end):
    values = [v for t, v in points if start <= t < end]
    return round(statistics.median(values), 2) if values else None


def trend(points, warmup):
    """Describe a steady-state series after the warm-up window."""
    if not points:
        return None
    begin = points[0][0] + warmup
    steady = [(t, v) for t, v in points if t >= begin]
    result = {
        "first": points[0][1], "last": points[-1][1],
        "steadyHours": round((steady[-1][0] - steady[0][0]).total_seconds() / 3600, 2) if len(steady) > 1 else 0,
    }
    if len(steady) < 10:
        return result | {"classification": "too-short"}
    end = steady[-1][0]
    half = steady[0][0] + (end - steady[0][0]) / 2
    first_hour = window_median(steady, steady[0][0], steady[0][0] + timedelta(hours=1))
    last_hour = window_median(steady, end - timedelta(hours=1), end + timedelta(seconds=1))
    late_slope = slope_per_hour([p for p in steady if p[0] >= half])
    median = statistics.median(v for _, v in steady)
    threshold = max(1.0, median * 0.01)
    if result["steadyHours"] < 2:
        classification = "too-short"
    elif late_slope is not None and late_slope > threshold and last_hour and first_hour and last_hour > first_hour * 1.05:
        classification = "growing"
    elif late_slope is not None and abs(late_slope) <= threshold:
        classification = "plateau"
    elif late_slope is not None and late_slope < -threshold:
        classification = "declining"
    else:
        classification = "mixed"
    return result | {
        "steady": stats([v for _, v in steady]),
        "slopePerHour": slope_per_hour(steady),
        "lateHalfSlopePerHour": late_slope,
        "firstSteadyHourMedian": first_hour,
        "lastHourMedian": last_hour,
        "classification": classification,
    }


def periods(rows, predicate):
    """Contiguous runs of samples matching predicate."""
    result, current = [], None
    for row in rows:
        if predicate(row):
            if current is None:
                current = {"start": row["time"], "end": row["time"], "samples": 0}
            current["end"] = row["time"]
            current["samples"] += 1
        elif current is not None:
            result.append(current)
            current = None
    if current is not None:
        result.append(current)
    return [{"start": local(p["start"]), "end": local(p["end"]),
             "minutes": round((p["end"] - p["start"]).total_seconds() / 60, 1), "samples": p["samples"]} for p in result]


def timeline(rows, key):
    changes, previous = [], object()
    for row in rows:
        value = key(row)
        if value != previous:
            changes.append({"at": local(row["time"]), "value": value})
            previous = value
    return changes


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("samples", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--warmup-minutes", type=float, default=30)
    parser.add_argument("--logical-processors", type=int, default=os.cpu_count() or 1)
    args = parser.parse_args()
    warmup = timedelta(minutes=args.warmup_minutes)

    raw, incomplete = [], 0
    for line in args.samples.read_text(encoding="utf-8-sig").splitlines():
        if line.strip():
            try:
                raw.append(json.loads(line))
            except json.JSONDecodeError:
                incomplete += 1
    if not raw:
        sys.exit("no samples")
    status_path = args.samples.with_name("status.json")
    status = json.loads(status_path.read_text(encoding="utf-8-sig")) if status_path.exists() else {}
    interval = status.get("intervalSeconds", 60)

    rows, bursts = [], 0
    for item in raw:
        item["time"] = parse_time(item["at"])
        # Samples < 1 s apart are an observer artifact (see observe-stability.ps1), not data.
        if rows and (item["time"] - rows[-1]["time"]).total_seconds() < 1:
            bursts += 1
            continue
        rows.append(item)

    data = lambda r: r.get("data") or {}
    diag = lambda r: r.get("diagnostics") or {}
    client = lambda r: diag(r).get("client") or {}
    gaps = [(rows[i]["time"] - rows[i - 1]["time"]).total_seconds() for i in range(1, len(rows))]
    long_gap_limit = interval * 2 + 10

    hardware_age = []
    for r in rows:
        observed = data(r).get("hardwareObservedAt")
        if observed:
            hardware_age.append((r["time"] - parse_time(observed)).total_seconds())
    client_age = []
    for r in rows:
        if client(r).get("at"):
            client_age.append((r["time"] - parse_time(client(r)["at"])).total_seconds())

    # Process instances: Web by its PID set (a frontend reinstall restarts every CEF process),
    # bridge by its primary PID (the one with the most CPU time; short helpers are listed separately).
    web_instances, bridge_instances, transient = [], [], []
    for r in rows:
        processes = r.get("processes") or []
        web = sorted((p for p in processes if p["name"].startswith("webwallpaper")), key=lambda p: p["id"])
        bridges = sorted((p for p in processes if p["name"] == "WallpaperBridge"), key=lambda p: -(p.get("cpuSeconds") or 0))
        if web:
            key = tuple(p["id"] for p in web)
            if not web_instances or web_instances[-1]["key"] != key:
                web_instances.append({"key": key, "rows": []})
            web_instances[-1]["rows"].append((r, web))
        if bridges:
            primary = bridges[0]
            if not bridge_instances or bridge_instances[-1]["key"] != primary["id"]:
                bridge_instances.append({"key": primary["id"], "rows": []})
            bridge_instances[-1]["rows"].append((r, [primary]))
            for extra in bridges[1:]:
                transient.append({"at": local(r["time"]), "pid": extra["id"], "workingSetMiB": extra["workingSetMiB"]})

    def describe(instance, label):
        samples = instance["rows"]
        first, last = samples[0][0], samples[-1][0]
        totals = {m: [(r["time"], sum(p[m] for p in ps)) for r, ps in samples] for m in METRICS}
        cpu = []
        for (r0, p0), (r1, p1) in zip(samples, samples[1:]):
            dt = (r1["time"] - r0["time"]).total_seconds()
            if 0 < dt < interval * 2 + 10 and all(p.get("cpuSeconds") is not None for p in p0 + p1):
                delta = sum(p["cpuSeconds"] for p in p1) - sum(p["cpuSeconds"] for p in p0)
                if delta >= 0:
                    cpu.append((r1["time"], delta / dt / args.logical_processors * 100))
        steady_from = first["time"] + warmup
        per_process = {}
        for pid in sorted({p["id"] for _, ps in samples for p in ps}):
            series = {m: [(r["time"], p[m]) for r, ps in samples for p in ps if p["id"] == pid] for m in METRICS}
            per_process[str(pid)] = {m: trend(series[m], warmup) for m in METRICS}
        return {
            "kind": label,
            "pids": list(instance["key"]) if isinstance(instance["key"], tuple) else [instance["key"]],
            "start": local(first["time"]), "end": local(last["time"]),
            "hours": round((last["time"] - first["time"]).total_seconds() / 3600, 2),
            "samples": len(samples),
            "bridgeBuilds": sorted({r.get("buildId") for r, _ in samples if r.get("buildId")}),
            "clientBuilds": sorted({client(r).get("buildId") for r, _ in samples if client(r).get("buildId")}),
            "total": {m: trend(totals[m], warmup) for m in METRICS},
            "cpuSystemPercentSteady": stats([v for t, v in cpu if t >= steady_from]),
            "processes": per_process,
        }

    longest = max(web_instances, key=lambda i: len(i["rows"]))
    hourly = defaultdict(list)
    for r, ps in longest["rows"]:
        hourly[r["time"].astimezone(LOCAL).strftime("%m-%d %H:00")].append(sum(p["privateCommitMiB"] for p in ps))

    report = {
        "analyzedAt": datetime.now(LOCAL).isoformat(timespec="seconds"),
        "source": str(args.samples),
        "scope": "Read-only observational run; not a controlled A/B. Builds changed during the run, so only per-instance trends are comparable.",
        "observerStatus": status,
        "logicalProcessors": args.logical_processors,
        "warmupMinutes": args.warmup_minutes,
        "samples": len(raw), "analyzedSamples": len(rows), "burstSamplesDropped": bursts, "incompleteLines": incomplete,
        "start": local(rows[0]["time"]), "end": local(rows[-1]["time"]),
        "hours": round((rows[-1]["time"] - rows[0]["time"]).total_seconds() / 3600, 2),
        "availability": {
            "healthUnavailable": sum(not r.get("healthAvailable") for r in rows),
            "dataErrors": sum(bool(r.get("error")) for r in rows),
            "unavailablePeriods": periods(rows, lambda r: not r.get("healthAvailable") or bool(r.get("error"))),
            "gapSeconds": stats(gaps), "longGaps": sum(g > long_gap_limit for g in gaps), "longGapLimitSeconds": long_gap_limit,
        },
        "data": {
            source: {"counts": dict(Counter(data(r).get(source) for r in rows)),
                     "nonOkPeriods": periods(rows, lambda r, s=source: data(r).get(s) not in ("ok", None))}
            for source in ("hardware", "weather", "quota")
        } | {"hardwareAgeSeconds": stats(hardware_age)},
        "diagnostics": {
            "refreshStatusCounts": dict(Counter(diag(r).get("refreshStatus") for r in rows)),
            "refreshNonOkPeriods": periods(rows, lambda r: diag(r).get("refreshStatus") not in ("ok", None)),
            "cacheFaultSamples": sum(bool(diag(r).get("cacheFaults")) for r in rows),
            "accountCount": timeline(rows, lambda r: diag(r).get("accountCount")),
        },
        "client": {
            "pausedCounts": dict(Counter(str(client(r).get("paused")) for r in rows)),
            "fpsCounts": dict(Counter(str(client(r).get("fps")) for r in rows)),
            "sizeCounts": dict(Counter(f"{client(r).get('width')}x{client(r).get('height')}" for r in rows)),
            "reportAgeSeconds": stats(client_age),
            "builds": timeline(rows, lambda r: client(r).get("buildId")),
        },
        "bridgeBuilds": timeline(rows, lambda r: r.get("buildId")),
        "webInstances": [describe(i, "web") for i in web_instances],
        "bridgeInstances": [describe(i, "bridge") for i in bridge_instances],
        "transientBridgeProcesses": transient,
        "longestWebInstanceHourlyPrivateCommitMedian": {h: round(statistics.median(v), 1) for h, v in hourly.items()},
    }
    output = args.output or args.samples.with_name("analysis.json")
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"{report['start']} -> {report['end']} ({report['hours']} h), {len(rows)} samples analysed, {bursts} burst samples dropped")
    a = report["availability"]
    print(f"availability: health unavailable {a['healthUnavailable']}, data errors {a['dataErrors']}, long gaps {a['longGaps']}, max gap {a['gapSeconds'].get('max')} s")
    for source in ("hardware", "weather", "quota"):
        d = report["data"][source]
        print(f"{source}: {d['counts']} non-ok periods {[(p['start'][11:16], p['end'][11:16], p['minutes']) for p in d['nonOkPeriods']][:6]}")
    print("refresh:", report["diagnostics"]["refreshStatusCounts"], "client:", report["client"]["pausedCounts"], report["client"]["fpsCounts"], report["client"]["sizeCounts"])
    for instance in report["webInstances"] + report["bridgeInstances"]:
        t = instance["total"]["privateCommitMiB"]
        w = instance["total"]["workingSetMiB"]
        print(f"{instance['kind']:6} {instance['start'][5:16]} -> {instance['end'][5:16]} {instance['hours']:5.2f} h "
              f"commit {t.get('firstSteadyHourMedian')} -> {t.get('lastHourMedian')} late {t.get('lateHalfSlopePerHour')}/h [{t.get('classification')}] "
              f"ws {w.get('firstSteadyHourMedian')} -> {w.get('lastHourMedian')} [{w.get('classification')}] "
              f"cpu {instance['cpuSystemPercentSteady'].get('mean')}% builds {instance['clientBuilds'] if instance['kind'] == 'web' else instance['bridgeBuilds']}")
    print("saved", output)


if __name__ == "__main__":
    main()
