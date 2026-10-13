#!/usr/bin/env python3
"""Check the embedded outline lines (Data/TrackOutlinePoints.bin.gz) against a replay.

Places the player's lap fraction on the outline line exactly like
TrackOutline.AtLapFraction (distance-interpolated, closed loop), scales it to metres
by the replay's lap length (GameDataSnapshot.LineMetresPerUnit), and compares with
the replay's own SimHub-style map (trackmap_replay_sim.build_map) after aligning the
two frames (Z = -outline y, both shifted to their min corner).

Usage:
    outline_line_check.py <replay stem> <outline name, e.g. "daytona 2011 road">
"""
from __future__ import annotations

import bisect
import gzip
import math
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from simhub_replay import iter_frames  # noqa: E402
from trackmap_replay_sim import build_map  # noqa: E402

DATA = Path(__file__).resolve().parent.parent / "Data" / "TrackOutlinePoints.bin.gz"


def load_raw(path=DATA):
    """{(game, name): (xSpan, ySpan, [(x, y) 0..10000])} — mirrors TrackOutlines.LoadLines."""
    b = gzip.decompress(Path(path).read_bytes())
    version, count = struct.unpack_from("<HH", b, 4)
    assert b[:4] == b"MZOL" and version == 2
    p, out = 8, {}
    for _ in range(count):
        gl = b[p]; p += 1
        game = b[p:p + gl].decode(); p += gl
        nl, = struct.unpack_from("<H", b, p); p += 2
        name = b[p:p + nl].decode(); p += nl
        xs, ys, n = struct.unpack_from("<ffI", b, p); p += 12
        axes = []
        for _axis in (0, 1):
            vals, v, d = [], 0, 0
            for _i in range(n):
                u, shift = 0, 0
                while True:
                    c = b[p]; p += 1
                    u |= (c & 0x7F) << shift
                    shift += 7
                    if c < 0x80:
                        break
                d += (u >> 1) ^ -(u & 1)
                v += d
                vals.append(v)
            axes.append(vals)
        out[(game, name)] = (xs, ys, list(zip(axes[0], axes[1])))
    return out


def load_lines():
    return {(g, n.lower()): [(x * 1e-4 * xs, y * 1e-4 * ys) for x, y in pts]
            for (g, n), (xs, ys, pts) in load_raw().items()}


def at_fraction(line, cum, f):
    f -= math.floor(f)
    t = f * cum[-1]
    i = min(max(bisect.bisect_right(cum, t) - 1, 0), len(line) - 1)
    seg = cum[i + 1] - cum[i]
    r = (t - cum[i]) / seg if seg > 0 else 0.0
    a, b = line[i], line[(i + 1) % len(line)]
    return a[0] + (b[0] - a[0]) * r, a[1] + (b[1] - a[1]) * r


def main():
    stem, name = sys.argv[1], sys.argv[2].lower()
    line = load_lines()[("iracing", name)]
    cum = [0.0]
    for i in range(len(line)):
        a, b = line[i], line[(i + 1) % len(line)]
        cum.append(cum[-1] + math.dist(a, b))
    tel = [fr.data["Telemetry"] for i, fr in enumerate(iter_frames(stem))
           if i % 3 == 0 and isinstance(fr.data.get("Telemetry"), dict)]
    m = build_map(tel)
    lap_len = max(t["LapDist"] for t in tel if t["LapDistPct"] < 0.999)
    scale = lap_len / cum[-1]
    mx0 = min(v[0] for v in m.val)
    mz1 = max(v[2] for v in m.val)
    err = []
    for k in range(500):
        f = m.pct[0] + k / 500 * (m.pct[-1] - m.pct[0])
        w = m.lookup(f)
        u, v = at_fraction(line, cum, f)
        # Outline min corner = (min x, max z) of the SimHub map (Z = -outline y).
        err.append(math.hypot(u * scale - (w[0] - mx0), -v * scale - (w[2] - mz1)))
    err.sort()
    print(f"{name}: {len(line)} pts, line {cum[-1]:.3f} units, lap {lap_len:.0f} m -> {scale:.1f} m/unit")
    print(f"  position error vs SimHub map: median {err[250]:.1f} m, p95 {err[475]:.1f} m, max {err[-1]:.1f} m")


if __name__ == "__main__":
    main()
