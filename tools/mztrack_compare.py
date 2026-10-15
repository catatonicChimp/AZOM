#!/usr/bin/env python3
"""Compare a SimHub iRacing replay's track map against PitHouse's .mztrack outline.

PitHouse ships one outline per track under GameConfigs/TrackData/<game>/<name>.mztrack
(JSON, XOR 0x5A obfuscated). iRacing outlines are normalised: points are
[x, y, 5000] in 0..10000 per axis, scaled to the drawn shape by x_max/y_max (the
larger dimension = 1), sampled along the lap from the S/F line.

This rebuilds the map from the replay (trackmap_replay_sim.build_map), normalises it
the same way, and reports the per-point error for each axis orientation, so the
world->outline mapping (axis flips, aspect) can be checked offline.

Usage:
    mztrack_compare.py <replay stem> <file.mztrack>
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from simhub_replay import iter_frames  # noqa: E402
from trackmap_replay_sim import build_map  # noqa: E402


def load_mztrack(path):
    b = Path(path).read_bytes()
    if b[:1] != b"{":
        b = bytes(c ^ 0x5A for c in b)
    return json.loads(b.decode("utf-8"))


def main():
    stem, mzpath = sys.argv[1], sys.argv[2]
    tel = [fr.data["Telemetry"] for i, fr in enumerate(iter_frames(stem))
           if i % 3 == 0 and isinstance(fr.data.get("Telemetry"), dict)]
    m = build_map(tel)
    if m is None:
        print("no full lap in replay")
        return
    mz = load_mztrack(mzpath)
    pts = [p for s in mz["sectors"] for p in s["data"]]
    ax, ay = mz["x_max"] - mz["x_min"], mz["y_max"] - mz["y_min"]
    outline = [(p[0] / 10000.0 * ax, p[1] / 10000.0 * ay) for p in pts]

    xs = [v[0] for v in m.val]
    zs = [v[2] for v in m.val]
    ext = max(max(xs) - min(xs), max(zs) - min(zs))
    print(f"replay map: {max(xs) - min(xs):.0f} x {max(zs) - min(zs):.0f} m, "
          f"aspect {(max(xs) - min(xs)) / ext:.3f}:{(max(zs) - min(zs)) / ext:.3f}; "
          f"mztrack '{mz.get('name')}' aspect {ax:.3f}:{ay:.3f}, {len(pts)} pts")

    # Sample both along the lap by fraction (pct for the map, index for the outline).
    n = 400
    res = {}
    for fx in (False, True):
        for fz in (False, True):
            err = []
            for k in range(n):
                f = k / n
                w = m.lookup(m.pct[0] + f * (m.pct[-1] - m.pct[0]))
                u = (w[0] - min(xs)) / ext
                v = (w[2] - min(zs)) / ext
                if fx:
                    u = (max(xs) - min(xs)) / ext - u
                if fz:
                    v = (max(zs) - min(zs)) / ext - v
                o = outline[min(int(f * len(outline)), len(outline) - 1)]
                err.append(math.hypot(u - o[0], v - o[1]))
            err.sort()
            res[(fx, fz)] = (sum(err) / n, err[int(0.95 * n)])
    for (fx, fz), (mean, p95) in sorted(res.items(), key=lambda kv: kv[1][0]):
        print(f"  outline.x={'-' if fx else '+'}x  outline.y={'-' if fz else '+'}z  "
              f"mean err {mean:.4f}  p95 {p95:.4f}  (track widths)")


if __name__ == "__main__":
    main()
