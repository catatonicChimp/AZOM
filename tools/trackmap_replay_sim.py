#!/usr/bin/env python3
"""Offline check of the track-map patch/Location* encoding against an iRacing SimHub replay.

iRacing exposes no world positions: SimHub reports every car (player included) as
[LapDistPct, 0, 0] and resolves it to map space through the recorded track map
(PersistantTracker DataRecord, built by ICarsReader.CoordinatesCreator from
YawNorth/LapDist/Pitch integration). This tool rebuilds that map from the player's
own laps in the replay, resolves every car per frame exactly like
DataRecordBase.ToAbsoluteCoordinates, packs location_t with a given transform, and
reports clamping + plots the field-space positions.

Usage:
    trackmap_replay_sim.py <stem> [--every N] [--png out.png]
    (<stem> = path without .telemetry.json, e.g. IRacing/20261003_011016)
"""
from __future__ import annotations

import argparse
import bisect
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from simhub_replay import iter_frames, read_metadata  # noqa: E402

U24 = 1 << 24
U20 = 1 << 20


def values(v):
    return v.get("$values") if isinstance(v, dict) else v


class MapRecord:
    """SimHub map (lap-percent -> map metres), one recorded lap."""

    def __init__(self, pts):
        # pts: [(pct, [x, y, z])], pct ascending
        last = pts[-1][1]
        # SaveMap re-zeroes relative maps to the last point.
        self.pct = [p for p, _ in pts]
        self.val = [[v[0] - last[0], v[1] - last[1], v[2] - last[2]] for _, v in pts]

    def lookup(self, pct):
        """DataRecordBase.GetRelativeCoordinates: [0,0,0] when out of range."""
        if pct < self.pct[0] or pct > self.pct[-1]:
            return None
        i = bisect.bisect_right(self.pct, pct) - 1
        i = min(max(i, 0), len(self.pct) - 2)
        a, b = self.pct[i], self.pct[i + 1]
        r = (pct - a) / (b - a) if b != a else 0.0
        va, vb = self.val[i], self.val[i + 1]
        return [va[k] + (vb[k] - va[k]) * r for k in range(3)]


def build_map(frames):
    """CoordinatesCreator.AddPoint integration, split at the pct wrap (like
    PersistantTracker's map recorder); return the longest full lap."""
    laps, cur = [], []
    for t in frames:
        pct, yaw, dist, pitch = t["LapDistPct"], t["YawNorth"] - math.pi / 2, t["LapDist"], t["Pitch"]
        if cur and cur[-1]["pct"] > 0.9 and pct < 0.1:
            laps.append(cur)
            cur = []
        if cur and pct - cur[-1]["pct"] <= 0:
            continue
        v = [0.001, 0.001, 0.001]
        if cur:
            p = cur[-1]
            d = dist - p["dist"]
            v = [p["v"][0] + math.cos(p["yaw"]) * d,
                 p["v"][1] - math.tan(p["pitch"]) * d,
                 p["v"][2] + math.sin(p["yaw"]) * d]
        cur.append({"pct": pct, "yaw": yaw, "dist": dist, "pitch": pitch, "v": v})
    if cur:
        laps.append(cur)
    full = [l for l in laps if len(l) > 10 and l[0]["pct"] < 0.05 and l[-1]["pct"] > 0.95]
    if not full:
        return None
    best = max(full, key=len)
    return MapRecord([(p["pct"], p["v"]) for p in best])


def bounds(m, trim_pct=0.0):
    """GameDataSnapshot.ComputeMapBounds: lap-relative maps are not trimmed."""
    def pc(s, p):
        return s[min(max(round(p / 100.0 * (len(s) - 1)), 0), len(s) - 1)]
    xs = sorted(v[0] for v in m.val)
    zs = sorted(v[2] for v in m.val)
    return pc(xs, trim_pct), pc(xs, 100 - trim_pct), pc(zs, trim_pct), pc(zs, 100 - trim_pct)


def transform_fallback():
    # TrackMapTransform.Fallback(): SCALE_FACTOR=1.2, no dims.
    sx = 669.0 / 1.2
    return dict(sx=sx, sz=16 * sx, cx=4719000, cz=8300000)


def transform_bounds(b):
    min_x, max_x, min_z, max_z = b
    ext = max(max_x - min_x, max_z - min_z)
    s = 1e6 / ext  # TrackMapTransform.TrackCounts
    mx, mz = (min_x + max_x) / 2, (min_z + max_z) / 2
    # Lap-relative maps run Z opposite to the wheel outline (invertZ).
    return dict(sx=s, sz=-16 * s, cx=0x480000 - s * mx, cz=0x800000 + 16 * s * mz)


def pack(t, x, z):
    fx = round(t["cx"] + t["sx"] * x)
    fz = round(t["cz"] + t["sz"] * z)
    clamped = not (0 <= fx < U24 and 0 <= fz < U24)
    return min(max(fx, 0), U24 - 1), min(max(fz, 0), U24 - 1), clamped


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("stem")
    ap.add_argument("--every", type=int, default=6, help="use every Nth frame")
    ap.add_argument("--png")
    a = ap.parse_args()

    meta = read_metadata(a.stem)
    tel = []
    for i, fr in enumerate(iter_frames(a.stem)):
        if i % a.every:
            continue
        t = fr.data.get("Telemetry")
        if isinstance(t, dict):
            tel.append(t)
    print(f"{meta.track} / {meta.car}: {len(tel)} frames")

    m = build_map(tel)
    if m is None:
        print("no full lap in replay -> SimHub has no map; cars resolve to [pct,0,0]")
        return
    b = bounds(m)
    print(f"map: {len(m.pct)} pts, X {b[0]:.0f}..{b[1]:.0f}  Z {b[2]:.0f}..{b[3]:.0f} m")

    tfs = {"current-fallback": transform_fallback(), "bounds": transform_bounds(b)}
    pts = {k: [] for k in tfs}
    nclamp = {k: 0 for k in tfs}
    ncar = 0
    for t in tel:
        for pct in values(t.get("CarIdxLapDistPct")) or []:
            if pct is None or pct < 0:
                continue
            w = m.lookup(pct)
            if w is None:
                continue
            ncar += 1
            for k, tf in tfs.items():
                fx, fz, c = pack(tf, w[0], w[2])
                nclamp[k] += c
                pts[k].append((fx, fz))
    for k, tf in tfs.items():
        fx = [p[0] for p in pts[k]]
        fz = [p[1] for p in pts[k]]
        print(f"{k:17s} sx={tf['sx']:.1f} X field {min(fx)}..{max(fx)}  Z field {min(fz)}..{max(fz)}  "
              f"clamped {nclamp[k]}/{ncar} ({100.0 * nclamp[k] / max(ncar, 1):.1f}%)")

    if a.png:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        fig, axs = plt.subplots(1, len(tfs), figsize=(6 * len(tfs), 6))
        for ax, (k, p) in zip(axs, pts.items()):
            # Normalised the way the fields relate: X carries 1/16 of Z's resolution.
            ax.scatter([(q[0] - 0x400000) / U20 for q in p], [q[1] / U24 for q in p], s=0.3)
            ax.set_xlim(0, 1); ax.set_ylim(0, 1); ax.set_aspect("equal"); ax.set_title(k)
        fig.savefig(a.png, dpi=90)
        print("wrote", a.png)


if __name__ == "__main__":
    main()
