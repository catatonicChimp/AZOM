#!/usr/bin/env python3
"""Check an embedded outline line runs the same way as the car in a replay.

Integrates the player's path like SimHub's CoordinatesCreator (no full lap needed),
maps it to the outline frame (u = x, v = -z), and compares each step's direction of
travel with the outline line's direction at the same lap fraction
(TrackOutline.AtLapFraction). Mean cos ≈ +1: same direction and orientation;
≈ -1: the line runs backwards; ≈ 0: mirrored/rotated.

Usage:
    outline_direction_check.py <replay stem> <outline name>
"""
from __future__ import annotations

import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from outline_line_check import at_fraction, load_lines  # noqa: E402
from simhub_replay import iter_frames  # noqa: E402


def main():
    stem, name = sys.argv[1], sys.argv[2].lower()
    line = load_lines()[("iracing", name)]
    cum = [0.0]
    for i in range(len(line)):
        a, b = line[i], line[(i + 1) % len(line)]
        cum.append(cum[-1] + math.dist(a, b))

    x = z = 0.0
    prev = None
    samples = []      # (pct, x, z)
    for i, fr in enumerate(iter_frames(stem)):
        t = fr.data.get("Telemetry")
        if i % 3 or not isinstance(t, dict):
            continue
        pct, dist = t["LapDistPct"], t["LapDist"]
        yaw = t["YawNorth"] - math.pi / 2
        if prev is not None:
            d = dist - prev[0]
            if 0 < d < 50:
                x += math.cos(prev[1]) * d
                z += math.sin(prev[1]) * d
                samples.append((pct, x, z))
        prev = (dist, yaw)

    cos_sum, n = 0.0, 0
    step = 20
    for k in range(0, len(samples) - step, step):
        p0, x0, z0 = samples[k]
        p1, x1, z1 = samples[k + step]
        if not (0.0 <= p0 < p1 <= 1.0) or p1 - p0 > 0.05:
            continue
        mu, mv = x1 - x0, -(z1 - z0)
        u0, v0 = at_fraction(line, cum, p0)
        u1, v1 = at_fraction(line, cum, p1)
        lu, lv = u1 - u0, v1 - v0
        nm, nl = math.hypot(mu, mv), math.hypot(lu, lv)
        if nm < 1 or nl == 0:
            continue
        cos_sum += (mu * lu + mv * lv) / (nm * nl)
        n += 1
    print(f"{name}: {n} steps, mean cos(travel, outline) = {cos_sum / max(n, 1):+.3f}")


if __name__ == "__main__":
    main()
