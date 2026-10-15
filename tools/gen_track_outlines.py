#!/usr/bin/env python3
"""Generate the plugin's embedded track-outline tables from PitHouse's TrackData.

PitHouse ships the wheel's track-map outlines as
    <PitHouse>/bin/GameConfigs/TrackData/<game>/<name>.mztrack
(JSON, XOR 0x5A obfuscated). The folder is the patch/TrackName game prefix and the
file name the track key, so "<game>/<name>" is the map the wheel draws.

Writes two files into <out dir> (default: the repo's Data/):

TrackOutlines.json — names and bounding boxes,
    {"<game>": {"<name>": [minX, maxX, minZ, maxZ, minY, maxY] | []}}
  in world metres (mztrack x/y are the ground plane = world X/Z, mztrack z is
  elevation). [] marks a normalised outline (iRacing: no world bounds, 0..1).

TrackOutlinePoints.gz — the line of every normalised outline, which the plugin
  walks by lap fraction (iRacing has no world positions). gzip of, little-endian:
    "MZOL" u16 version=2 u16 count
    count × { u8 len, game utf8; u16 len, name utf8; f32 xMax; f32 yMax; u32 n;
              n × x, then n × y }               x, y in 0..10000 per axis
  Each axis is stored as second differences (d_i − d_{i−1}, d_i = v_i − v_{i−1},
  v_{−1} = d_{−1} = 0), zigzag LEB128 varints: the line is smooth, so most are
  0/±1 — 6× smaller under gzip than raw u16 (239 KB vs 1.5 MB).

Usage:
    gen_track_outlines.py "<PitHouse>/bin/GameConfigs/TrackData" [out dir]
"""
from __future__ import annotations

import gzip
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from mztrack_compare import load_mztrack  # noqa: E402


def zigzag(v):
    return (v << 1) ^ (v >> 63)


def write_varint(buf, u):
    while u >= 0x80:
        buf.append((u & 0x7F) | 0x80)
        u >>= 7
    buf.append(u)


def main():
    root = Path(sys.argv[1])
    out = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "Data"
    games = {}
    lines = []
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
            if normalised:
                pts = [p for s in d["sectors"] for p in s["data"]]
                lines.append((gdir.name, name, float(d["x_max"]), float(d["y_max"]), pts))
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
    (out / "TrackOutlines.json").write_text(text, encoding="utf-8")

    buf = bytearray(b"MZOL" + struct.pack("<HH", 2, len(lines)))
    for game, name, xmax, ymax, pts in lines:
        g, n = game.encode("utf-8"), name.encode("utf-8")
        buf += struct.pack("<B", len(g)) + g + struct.pack("<H", len(n)) + n
        buf += struct.pack("<ffI", xmax, ymax, len(pts))
        for axis in (0, 1):
            prev = prev_d = 0
            for p in pts:
                d = int(p[axis]) - prev
                write_varint(buf, zigzag(d - prev_d))
                prev, prev_d = int(p[axis]), d
    # mtime=0 keeps the output byte-identical across runs.
    (out / "TrackOutlinePoints.gz").write_bytes(gzip.compress(bytes(buf), mtime=0))
    print(f"wrote {out}/TrackOutlines.json, TrackOutlinePoints.gz "
          f"({len(lines)} lines, {sum(len(l[4]) for l in lines)} points)")


if __name__ == "__main__":
    main()
