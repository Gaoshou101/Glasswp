"""Controlled A/B of frontend settings in an isolated headless Chrome.

The live wallpaper, its properties and the bridge configuration are never changed. The bridge is
read once (setup session -> read token -> snapshot) to build an anonymised fixture; afterwards the
page only talks to an intercepted origin that replays that fixture with production-like timing:
hardware advances every 2 s, quota and weather every 10 min. Scenarios are applied through
window.wallpaperPropertyListener exactly like Wallpaper Engine does, and rounds rotate the order so
time drift is not attributed to one scenario. Chrome is not Wallpaper Engine's CEF: use the
results as relative costs of the frontend's own work, not as absolute desktop numbers.

Usage:
  python scripts/ab-frontend-resources.py [--rounds 3] [--measure-minutes 6] [--settle-minutes 1]
      [--warmup-minutes 5] [--scenarios A,S,C,H,P] [--fixture fixture.json] [--output-dir DIR]
      [--dist DIR]   # a candidate build directory (default: dist)
  python scripts/ab-frontend-resources.py --analyze DIR
"""
from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import subprocess
import sys
import time
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parents[1]
CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"
BRIDGE = "http://127.0.0.1:17632"
HARNESS = "http://127.0.0.1:17699"
SCENARIOS = {
    "A": ("production settings", {}, {}),
    "S": ("clock seconds off", {"seconds": False}, {}),
    "C": ("glass blur 0", {"glass_blur": 0}, {}),
    "H": ("hardware updates frozen", {}, {"freezeHardware": True}),
    # Placebo at idle: liquid mode hides the ambient timer and its rAF runs only on pointer input.
    "M": ("motion off (idle placebo)", {"motion": False}, {}),
    "P": ("paused", {}, {"paused": True}),
}
# Mirrors the live portrait wallpaper properties (2026-11-07) except the connection.
BASE_PROPERTIES = {
    "bridge_url": HARNESS + "/", "bridge_token": "ab-harness",
    "ignore_codex_base_weekly": True, "ignore_codex_image_quota": True, "seconds": True,
    "motion": True, "material_mode": "liquid",
    "glass_transparency": 83, "glass_blur": 26, "glass_highlight": 68,
}
ISO = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}")
JS_METRICS = ("TaskDuration", "ScriptDuration", "LayoutDuration", "RecalcStyleDuration", "LayoutCount", "RecalcStyleCount")


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


def post(path, headers):
    request = urllib.request.Request(BRIDGE + path, data=b"{}", method="POST", headers={"Content-Type": "application/json", **headers})
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.load(response)


def capture_fixture() -> dict:
    """Read the live snapshot once and keep only anonymised structure."""
    session = post("/v1/setup/session", {"Origin": BRIDGE, "X-Wallpaper-Setup": "1"})["session"]
    token = post("/v1/setup/config/read", {"Origin": BRIDGE, "X-Wallpaper-Admin": session})["readToken"]
    with urllib.request.urlopen(urllib.request.Request(BRIDGE + "/v1/snapshot", headers={"X-Wallpaper-Token": token}), timeout=5) as response:
        snapshot = json.load(response)
    for index, account in enumerate(snapshot["quota"]["accounts"], 1):
        account["id"], account["alias"] = f"acct-{index}", f"账号 {index}"
    if snapshot["weather"].get("city"):
        snapshot["weather"]["city"] = "示例城市"
    snapshot.pop("revisions", None)
    return {"capturedAt": utc_now().isoformat(), "snapshot": snapshot}


def shift(value, delta):
    if isinstance(value, dict):
        return {k: shift(v, delta) for k, v in value.items()}
    if isinstance(value, list):
        return [shift(v, delta) for v in value]
    if isinstance(value, str) and ISO.match(value):
        try:
            moment = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            return value
        return (moment + delta).astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
    return value


class Replay:
    """Production-like snapshot timing from one captured fixture."""

    def __init__(self, fixture: dict):
        self.base = fixture["snapshot"]
        self.captured = datetime.fromisoformat(fixture["capturedAt"])
        self.start = utc_now()
        self.frozen_step = None
        self.requests = 0

    def snapshot(self) -> dict:
        self.requests += 1
        elapsed = (utc_now() - self.start).total_seconds()
        step = int(elapsed // 2) if self.frozen_step is None else self.frozen_step
        block = int(elapsed // 600)
        lag = self.start - self.captured
        hardware = shift(self.base["hardware"], lag + timedelta(seconds=2 * step))
        for name, amplitude in (("cpu", 6), ("gpu", 4)):
            device = hardware.get(name) or {}
            for metric, scale in (("load", amplitude), ("temperature", 2)):
                item = device.get(metric) or {}
                if isinstance(item.get("value"), (int, float)):
                    item["value"] = round(max(0, item["value"] + scale * math.sin(step / (5 + scale))), 1)
        for name in ("ram", "vram"):
            device = hardware.get(name) or {}
            if isinstance(device.get("usedBytes"), (int, float)):
                device["usedBytes"] = int(device["usedBytes"] * (1 + 0.002 * math.sin(step / 9)))
        result = shift({k: v for k, v in self.base.items() if k != "hardware"}, lag + timedelta(minutes=10 * block))
        result["hardware"] = hardware
        result["generatedAt"] = utc_now().isoformat().replace("+00:00", "Z")
        result["revisions"] = {"epoch": "ab-harness", "hardware": step, "quota": block, "weather": block}
        return result

    def freeze(self, on: bool):
        self.frozen_step = int((utc_now() - self.start).total_seconds() // 2) if on else None


def run(args):
    sys.path.insert(0, str(ROOT / ".tools/python"))
    from playwright.sync_api import sync_playwright

    out = args.output_dir or ROOT / "artifacts/analysis" / ("ab-frontend-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
    out.mkdir(parents=True, exist_ok=False)
    fixture = json.loads(args.fixture.read_text(encoding="utf-8")) if args.fixture else capture_fixture()
    (out / "fixture.json").write_text(json.dumps(fixture, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    replay = Replay(fixture)
    keys = args.scenarios.split(",")
    dist = (args.dist or ROOT / "dist").resolve()
    info = dist / "build-info.json"
    meta = {"started": utc_now().isoformat(), "chrome": CHROME, "dist": str(dist), "buildInfo": json.loads(info.read_text(encoding="utf-8-sig")) if info.exists() else None,
            "baseProperties": {k: v for k, v in BASE_PROPERTIES.items() if k != "bridge_token"}, "scenarios": {k: SCENARIOS[k][0] for k in keys},
            "rounds": args.rounds, "measureMinutes": args.measure_minutes, "settleMinutes": args.settle_minutes, "warmupMinutes": args.warmup_minutes, "intervalSeconds": args.interval}
    phases, page_errors = [], []
    stop_flag = out / "stop.flag"
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, executable_path=CHROME)
        context = browser.new_context(viewport={"width": 1600, "height": 2560}, device_scale_factor=1)
        context.add_init_script("window.__frames=0; const nativeRAF=window.requestAnimationFrame.bind(window); window.requestAnimationFrame=cb=>nativeRAF(t=>{window.__frames++;cb(t)});")
        page = context.new_page()
        page.on("pageerror", lambda e: page_errors.append(str(e)))

        def respond(route):
            path = urlsplit(route.request.url).path
            headers = {"Access-Control-Allow-Origin": "*", "Cache-Control": "no-store"}
            if path == "/v1/snapshot":
                route.fulfill(json=replay.snapshot(), headers=headers)
            elif path == "/v1/client":
                route.fulfill(json={}, headers=headers)
            elif path == "/v1/desktop-input":
                route.fulfill(json={"available": False, "allowed": False}, headers=headers)
            else:
                route.fulfill(json={"canRefresh": False, "refresh": {"state": {"status": "idle", "message": "", "lastFinishedAt": None}}}, headers=headers)

        page.route(HARNESS + "/**", respond)
        # The page must never reach the live bridge.
        page.route(BRIDGE + "/**", lambda route: route.fulfill(status=503, body=""))
        page.goto((dist / "index.html").as_uri(), wait_until="load")
        cdp = context.new_cdp_session(page)
        cdp.send("Performance.enable")
        root = next(x["id"] for x in browser.new_browser_cdp_session().send("SystemInfo.getProcessInfo")["processInfo"] if x["type"] == "browser")
        meta["browserProcessId"] = root

        def apply(key):
            _, properties, flags = SCENARIOS[key]
            replay.freeze(bool(flags.get("freezeHardware")))
            page.evaluate("""p => { const l = window.wallpaperPropertyListener; l.setPaused(false);
                l.applyUserProperties(Object.fromEntries(Object.entries(p).map(([k, v]) => [k, { value: v }])));
                l.applyGeneralProperties({ fps: 30 }); }""", {**BASE_PROPERTIES, **properties})
            if flags.get("paused"):
                page.evaluate("() => window.wallpaperPropertyListener.setPaused(true)")

        def js_metrics():
            values = {m["name"]: m["value"] for m in cdp.send("Performance.getMetrics")["metrics"]}
            values["frames"] = page.evaluate("window.__frames")
            values["at"] = utc_now().isoformat()
            return values

        def hold(minutes):
            end = time.monotonic() + minutes * 60
            while time.monotonic() < end:
                page.wait_for_timeout(min(1000, max(1, int((end - time.monotonic()) * 1000))))

        apply("A")
        page.wait_for_selector(".quota-card", timeout=30000)
        sampler = subprocess.Popen(["powershell", "-NoProfile", "-File", str(ROOT / "scripts/sample-process-tree.ps1"),
                                    "-RootProcessId", str(root), "-IntervalSeconds", str(args.interval), "-OutputPath", str(out / "samples.jsonl"), "-StopFile", str(stop_flag)],
                                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        try:
            hold(args.warmup_minutes)
            for round_index in range(args.rounds):
                order = keys[round_index % len(keys):] + keys[:round_index % len(keys)]
                if round_index % 2:
                    order.reverse()
                for key in order:
                    apply(key)
                    settle = utc_now()
                    hold(args.settle_minutes)
                    before = js_metrics(); start = utc_now()
                    hold(args.measure_minutes)
                    after = js_metrics(); end = utc_now()
                    phases.append({"round": round_index + 1, "scenario": key, "settleStart": settle.isoformat(), "start": start.isoformat(), "end": end.isoformat(),
                                   "js": {"before": before, "after": after}, "quotaCards": page.locator(".quota-card").count()})
                    (out / "phases.json").write_text(json.dumps(phases, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
                    print(f"round {round_index + 1} {key} done {end.astimezone().strftime('%H:%M:%S')}", flush=True)
        finally:
            stop_flag.write_text("stop", encoding="utf-8")
            try:
                sampler.wait(timeout=120)
            except subprocess.TimeoutExpired:
                pass  # the sampler also stops once the browser root process exits
            meta.update(ended=utc_now().isoformat(), pageErrors=page_errors, snapshotRequests=replay.requests)
            (out / "meta.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
            browser.close()
    analyze(out)


def summary(values):
    values = [v for v in values if v is not None and math.isfinite(v)]
    if not values:
        return None
    ordered = sorted(values)
    return {"n": len(values), "mean": round(statistics.fmean(values), 4), "median": round(statistics.median(values), 4),
            "p95": round(ordered[max(0, math.ceil(len(ordered) * 0.95) - 1)], 4)}


def analyze(out: Path):
    meta = json.loads((out / "meta.json").read_text(encoding="utf-8"))
    phases = json.loads((out / "phases.json").read_text(encoding="utf-8"))
    samples = [json.loads(line) for line in (out / "samples.jsonl").read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    for sample in samples:
        sample["time"] = datetime.fromisoformat(sample["at"])
    fields = ("cpuSystemPercent", "gpu3dPercent", "privateWorkingSetMiB", "privateCommitMiB", "gpuDedicatedMiB")

    def totals(sample, role=None):
        processes = [p for p in sample["processes"] if role is None or p["role"] == role]
        row = {}
        for field in fields:
            values = [p[field] for p in processes]
            row[field] = None if not values or any(v is None for v in values) else sum(values)
        return row

    roles = sorted({p["role"] for s in samples for p in s["processes"]})
    results = []
    for phase in phases:
        start, end = datetime.fromisoformat(phase["start"]), datetime.fromisoformat(phase["end"])
        window = [s for s in samples if start < s["time"] <= end]
        before, after = phase["js"]["before"], phase["js"]["after"]
        minutes = (end - start).total_seconds() / 60
        results.append({
            "round": phase["round"], "scenario": phase["scenario"], "samples": len(window), "quotaCards": phase.get("quotaCards"),
            "total": {f: summary([totals(s)[f] for s in window]) for f in fields},
            "roles": {r: {f: summary([totals(s, r)[f] for s in window]) for f in fields} for r in roles},
            # CDP durations are seconds; report milliseconds of renderer main-thread work per minute.
            "jsPerMinute": {m: round((after.get(m, 0) - before.get(m, 0)) / minutes * (1000 if m.endswith("Duration") else 1), 3) for m in JS_METRICS + ("frames",)},
            "jsHeapUsedMiB": round(after.get("JSHeapUsedSize", 0) / 2**20, 2), "domNodes": after.get("Nodes"),
        })

    def metric(entry, field, stat="mean", role=None):
        source = entry["total"] if role is None else entry["roles"].get(role, {})
        value = source.get(field)
        return value[stat] if value else None

    scenarios = {}
    for key in meta["scenarios"]:
        own = [e for e in results if e["scenario"] == key]
        block = {"description": meta["scenarios"][key], "rounds": len(own)}
        for field, stat in (("cpuSystemPercent", "mean"), ("gpu3dPercent", "mean"), ("privateWorkingSetMiB", "median"), ("privateCommitMiB", "median"), ("gpuDedicatedMiB", "median")):
            values, deltas = [], []
            for entry in own:
                value = metric(entry, field, stat)
                values.append(None if value is None else round(value, 4))
                reference = next((e for e in results if e["scenario"] == "A" and e["round"] == entry["round"]), None)
                base = metric(reference, field, stat) if reference else None
                if value is not None and base is not None:
                    deltas.append(round(value - base, 4))
            block[field] = {"stat": stat, "perRound": values, "deltaVsA": deltas, "deltaVsAMean": round(statistics.fmean(deltas), 4) if deltas else None}
        block["roleCpuMean"] = {r: [metric(e, "cpuSystemPercent", "mean", r) for e in own] for r in roles}
        block["roleGpu3dMean"] = {r: [metric(e, "gpu3dPercent", "mean", r) for e in own] for r in roles}
        block["rolePrivateWorkingSetMedian"] = {r: [metric(e, "privateWorkingSetMiB", "median", r) for e in own] for r in roles}
        block["jsPerMinute"] = {m: [e["jsPerMinute"][m] for e in own] for m in JS_METRICS + ("frames",)}
        block["jsHeapUsedMiB"] = [e["jsHeapUsedMiB"] for e in own]
        scenarios[key] = block
    report = {"meta": meta, "scenarios": scenarios, "phases": results}
    (out / "analysis.json").write_text(json.dumps(report, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"page errors: {len(meta.get('pageErrors', []))}, snapshot requests: {meta.get('snapshotRequests')}, samples: {len(samples)}")
    for key, block in scenarios.items():
        cpu, gpu, ws = block["cpuSystemPercent"], block["gpu3dPercent"], block["privateWorkingSetMiB"]
        print(f"{key} {block['description']:<24} cpu {cpu['perRound']} dA {cpu['deltaVsAMean']} | gpu3d {gpu['perRound']} dA {gpu['deltaVsAMean']} | privWS {ws['perRound']} dA {ws['deltaVsAMean']}")
        print(f"   main-thread task ms/min {block['jsPerMinute']['TaskDuration']} layouts/min {block['jsPerMinute']['LayoutCount']} styles/min {block['jsPerMinute']['RecalcStyleCount']} rAF/min {block['jsPerMinute']['frames']}")
    print("saved", out / "analysis.json")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rounds", type=int, default=3)
    parser.add_argument("--measure-minutes", type=float, default=6)
    parser.add_argument("--settle-minutes", type=float, default=1)
    parser.add_argument("--warmup-minutes", type=float, default=5)
    parser.add_argument("--interval", type=float, default=5)
    parser.add_argument("--scenarios", default="A,S,C,H,P")
    parser.add_argument("--fixture", type=Path)
    parser.add_argument("--output-dir", type=Path)
    parser.add_argument("--dist", type=Path, help="frontend build directory containing index.html (default: dist)")
    parser.add_argument("--analyze", type=Path, help="re-analyse an existing output directory")
    args = parser.parse_args()
    if args.analyze:
        analyze(args.analyze)
        return
    unknown = [k for k in args.scenarios.split(",") if k not in SCENARIOS]
    if unknown or "A" not in args.scenarios.split(","):
        parser.error("scenarios must be known keys and include the paired reference A")
    run(args)


if __name__ == "__main__":
    main()
