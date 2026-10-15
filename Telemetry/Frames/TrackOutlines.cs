using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using MozaPlugin.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MozaPlugin.Telemetry.Frames
{
    /// <summary>One MOZA track-map outline: the wheel draws
    /// <c>/home/moza/resource/maps/&lt;Path&gt;.mztrack</c> for <c>patch/TrackName</c>.</summary>
    public sealed class TrackOutline
    {
        public string Game = "";   // TrackData folder, the patch/TrackName prefix
        public string Name = "";   // file name, the wheel's map key
        // World-metre extent of the outline; absent for normalised (iRacing) outlines.
        public bool HasBounds;
        public double MinX, MaxX, MinY, MaxY, MinZ, MaxZ;

        // patch/TrackName key, lowercased: the firmware logs "not exist" for
        // "combinedShortB" but loads lowercase keys (bundle PBGRBYY4).
        public string Path = "";

        // The drawn line of a normalised (iRacing) outline: x,y pairs 0..10000 per
        // axis, spanning XSpan × YSpan (larger = 1), point 0 on the S/F line and
        // near-evenly spaced by distance — so lap fraction ≈ distance fraction.
        internal ushort[]? Line;
        internal float XSpan, YSpan;
        private double[]? _cumLength;   // closed-loop cumulative length, outline units

        public bool HasLine => Line != null && Line.Length >= 8;

        /// <summary>Line length in outline units (larger axis = 1).</summary>
        public double Perimeter => CumLength()[Line!.Length / 2];

        /// <summary>Point at lap fraction <paramref name="f"/> (wrapped to 0..1),
        /// interpolated by distance along the closed line, in outline units.</summary>
        public (double U, double V) AtLapFraction(double f)
        {
            var cum = CumLength();
            int n = Line!.Length / 2;
            f -= Math.Floor(f);
            double target = f * cum[n];
            int i = Array.BinarySearch(cum, target);
            if (i < 0) i = ~i - 1;
            if (i < 0) i = 0; else if (i >= n) i = n - 1;
            double seg = cum[i + 1] - cum[i];
            double r = seg > 0.0 ? (target - cum[i]) / seg : 0.0;
            var (ax, ay) = Pt(i);
            var (bx, by) = Pt((i + 1) % n);
            return (ax + (bx - ax) * r, ay + (by - ay) * r);
        }

        private (double, double) Pt(int i) =>
            (Line![2 * i] * 0.0001 * XSpan, Line[2 * i + 1] * 0.0001 * YSpan);

        private double[] CumLength()
        {
            var cum = _cumLength;
            if (cum != null) return cum;
            int n = Line!.Length / 2;
            cum = new double[n + 1];
            for (int i = 0; i < n; i++)
            {
                var (ax, ay) = Pt(i);
                var (bx, by) = Pt((i + 1) % n);
                cum[i + 1] = cum[i] + Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            }
            return _cumLength = cum;
        }
    }

    /// <summary>
    /// The wheel's track-map outline set (names + world bounds of PitHouse's
    /// GameConfigs/TrackData, embedded as Data/TrackOutlines.json by
    /// tools/gen_track_outlines.py). Matched exactly on letters/digits against
    /// SimHub's track id/config/name — never fuzzily: a wrong outline is worse
    /// than none.
    /// </summary>
    internal static class TrackOutlines
    {
        private static readonly Lazy<Dictionary<string, Dictionary<string, TrackOutline>>> s_table =
            new Lazy<Dictionary<string, Dictionary<string, TrackOutline>>>(Load, isThreadSafe: true);

        private sealed class LastLookup
        {
            public string? Game, Id, Config, Name;
            public TrackOutline? Result;
        }
        private static volatile LastLookup? s_last;

        /// <summary>TrackData folder for a SimHub game code, or null when MOZA has no
        /// outlines for that game.</summary>
        internal static string? GameFolder(string? simHubGame)
        {
            if (string.IsNullOrEmpty(simHubGame)) return null;
            switch (simHubGame)
            {
                case "AssettoCorsa": return "ac";
                case "AssettoCorsaCompetizione": return "acc";
                case "IRacing": return "iracing";
                case "LMU": return "lemans";
            }
            // F1 2020, F1 2021, … are one TrackData folder.
            if (simHubGame!.Length > 2 && simHubGame.StartsWith("F1", StringComparison.Ordinal)
                && char.IsDigit(simHubGame[2]))
                return "f1";
            return null;
        }

        /// <summary>The outline for the current track, or null. Candidates in order:
        /// TrackId+TrackConfig (AC: "ks_nordschleife"+"nordschleife"), TrackId (ACC
        /// "nurburgring_24h", iRacing "daytona 2011 road"), then the display name.</summary>
        internal static TrackOutline? Find(string? simHubGame, string? trackId, string? trackConfig, string? trackName)
        {
            var last = s_last;
            if (last != null && last.Game == simHubGame && last.Id == trackId
                && last.Config == trackConfig && last.Name == trackName)
                return last.Result;

            TrackOutline? result = null;
            string? folder = GameFolder(simHubGame);
            if (folder != null && s_table.Value.TryGetValue(folder, out var tracks))
            {
                foreach (var cand in new[] { trackId + trackConfig, trackId, trackName + trackConfig, trackName })
                {
                    string k = Key(cand);
                    if (k.Length > 0 && tracks.TryGetValue(k, out result)) break;
                }
            }
            s_last = new LastLookup { Game = simHubGame, Id = trackId, Config = trackConfig, Name = trackName, Result = result };
            return result;
        }

        // Lowercase letters/digits only: "ks_nordschleife-nordschleife" and
        // "ks_nordschleifenordschleife" are the same key.
        internal static string Key(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s!.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static Dictionary<string, Dictionary<string, TrackOutline>> Load()
        {
            var table = new Dictionary<string, Dictionary<string, TrackOutline>>(StringComparer.Ordinal);
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("MozaPlugin.Data.TrackOutlines.json");
                if (stream == null)
                {
                    MozaLog.Warn("[AZOM] track outlines: embedded resource not found");
                    return table;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8);
                foreach (var game in JObject.Parse(reader.ReadToEnd()).Properties())
                {
                    var tracks = new Dictionary<string, TrackOutline>(StringComparer.Ordinal);
                    foreach (var t in ((JObject)game.Value).Properties())
                    {
                        var o = new TrackOutline { Game = game.Name, Name = t.Name,
                            Path = (game.Name + "/" + t.Name).ToLowerInvariant() };
                        if (t.Value is JArray b && b.Count == 6)
                        {
                            o.HasBounds = true;
                            o.MinX = (double)b[0]; o.MaxX = (double)b[1];
                            o.MinZ = (double)b[2]; o.MaxZ = (double)b[3];
                            o.MinY = (double)b[4]; o.MaxY = (double)b[5];
                        }
                        string k = Key(t.Name);
                        if (k.Length > 0 && !tracks.ContainsKey(k)) tracks[k] = o;
                    }
                    table[game.Name] = tracks;
                }
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] track outlines: load failed: {ex.Message}");
            }
            LoadLines(table);
            return table;
        }

        private static uint ReadVarint(BinaryReader r)
        {
            uint u = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = r.ReadByte();
                u |= (uint)(b & 0x7F) << shift;
                if (b < 0x80) return u;
            }
        }

        // Data/TrackOutlinePoints.gz (format: tools/gen_track_outlines.py).
        private static void LoadLines(Dictionary<string, Dictionary<string, TrackOutline>> table)
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("MozaPlugin.Data.TrackOutlinePoints.gz");
                if (stream == null)
                {
                    MozaLog.Warn("[AZOM] track outline lines: embedded resource not found");
                    return;
                }
                using var gz = new GZipStream(stream, CompressionMode.Decompress);
                using var r = new BinaryReader(gz, Encoding.UTF8);
                if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "MZOL" || r.ReadUInt16() != 2)
                {
                    MozaLog.Warn("[AZOM] track outline lines: bad header");
                    return;
                }
                int count = r.ReadUInt16(), loaded = 0;
                for (int t = 0; t < count; t++)
                {
                    string game = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
                    string name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
                    float xSpan = r.ReadSingle(), ySpan = r.ReadSingle();
                    int n = r.ReadInt32();
                    var line = new ushort[2 * n];
                    // Per axis: zigzag-varint second differences.
                    for (int axis = 0; axis < 2; axis++)
                    {
                        int v = 0, d = 0;
                        for (int i = 0; i < n; i++)
                        {
                            uint z = ReadVarint(r);
                            d += (int)(z >> 1) ^ -(int)(z & 1);
                            v += d;
                            line[2 * i + axis] = (ushort)v;
                        }
                    }
                    if (table.TryGetValue(game, out var tracks) && tracks.TryGetValue(Key(name), out var o))
                    {
                        o.Line = line; o.XSpan = xSpan; o.YSpan = ySpan;
                        loaded++;
                    }
                }
                MozaLog.Info($"[AZOM] track outline lines: {loaded}/{count} loaded");
            }
            catch (Exception ex)
            {
                MozaLog.Warn($"[AZOM] track outline lines: load failed: {ex.Message}");
            }
        }
    }
}
