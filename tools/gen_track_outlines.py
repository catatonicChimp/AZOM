#!/usr/bin/env python3
"""Generate Data/TrackOutlines.json from PitHouse's bundled track outlines.

PitHouse ships the wheel's track-map outlines as
    <PitHouse>/bin/GameConfigs/TrackData/<game>/<name>.mztrack
(JSON, XOR 0x5A obfuscated). The folder is the patch/TrackName game prefix and the
file name the track key, so "<game>/<name>" is the map the wheel draws.

Only the names and bounding boxes are kept, as
    {"<game>": {"<name>": [minX, maxX, minZ, maxZ, minY, maxY] | []}}
in world metres (mztrack x/y are the ground plane = world X/Z, mztrack z is
elevation). [] marks a normalised outline (iRacing: no world bounds, 0..1).

Usage:
    gen_track_outlines.py "<PitHouse>/bin/GameConfigs/TrackData" [out.json]
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from mztrack_compare import load_mztrack  # noqa: E402


def main():
    root = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else \
        Path(__file__).resolve().parent.parent / "Data" / "TrackOutlines.json"
    games = {}
    for gdir in sorted(p for p in root.iterdir() if p.is_dir()):
        tracks = {}
        for f in sorted(gdir.glob("*.mztrack")):
            d = load_mztrack(f)
            name = f.stem
            if d.get("name") and d["name"] != name:
                print(f"note: {gdir.name}/{name} has inner name {d['name']!r}; using file name")
            normalised = d["x_min"] == 0 and d["y_min"] == 0 and 1 in (d["x_max"], d["y_max"]) \
                and d["z_min"] == d["z_max"] == 0
            tracks[name] = [] if normalised else [round(float(d[k]), 2) for k in
                                                  ("x_min", "x_max", "y_min", "y_max", "z_min", "z_max")]
        # The plugin matches on lowercase letters/digits only (TrackOutlines.Key).
        seen = {}
        for name in tracks:
            k = "".join(c for c in name.lower() if c.isalnum())
            if k in seen:
                print(f"warning: {gdir.name}: {seen[k]!r} and {name!r} share key {k!r}")
            seen.setdefault(k, name)
        games[gdir.name] = tracks
        print(f"{gdir.name}: {len(tracks)} tracks")
    text = "{\n" + ",\n".join(
        f'  {json.dumps(g)}: {{\n' + ",\n".join(
            f"    {json.dumps(n, ensure_ascii=False)}: {json.dumps(b)}" for n, b in t.items()) + "\n  }"
        for g, t in games.items()) + "\n}\n"
    out.write_text(text, encoding="utf-8")
    print("wrote", out)


if __name__ == "__main__":
    main()
