using System;
using System.Collections.Generic;
using System.IO;
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
            return table;
        }
    }
}
