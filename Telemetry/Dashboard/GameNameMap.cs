using System;
using System.Collections.Generic;

namespace MozaPlugin.Telemetry.Dashboard
{
    /// <summary>
    /// Maps SimHub's game code (<c>PluginManager.GameName</c>, e.g.
    /// "AssettoCorsaCompetizione") to the short string MOZA PitHouse publishes
    /// on the <c>patch/GameName</c> channel ("ACC"). Wheel dashboards branch
    /// on the PitHouse strings — verified against the FSR2 "LD - Marco" /
    /// "Lovely Dashboard FSR2" dashes, whose layout logic compares
    /// <c>patch/GameName</c> against "AC", "ACC", "Automobilista 2" and
    /// "rFactor 2".
    ///
    /// Only those four games are remapped — they are the ones with ground
    /// truth from real dashboards. Any other game passes its SimHub code
    /// through unchanged: that is informative and clearly the plugin's own
    /// value, never a fabricated PitHouse short-code we have no capture for.
    /// </summary>
    internal static class GameNameMap
    {
        private static readonly Dictionary<string, string> ToMoza =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["AssettoCorsa"]             = "AC",
                ["AssettoCorsaCompetizione"] = "ACC",
                ["Automobilista2"]           = "Automobilista 2",
                ["rFactor2"]                 = "rFactor 2",
            };

        /// <summary>
        /// MOZA <c>patch/GameName</c> string for a SimHub game code, or the
        /// code itself when unmapped. Empty string when no game is running.
        /// </summary>
        internal static string Resolve(string? simHubGameName)
        {
            if (string.IsNullOrEmpty(simHubGameName)) return "";
            return ToMoza.TryGetValue(simHubGameName!, out var moza) ? moza : simHubGameName!;
        }

        // MOZA track-map identifier "<game>/<track>" for patch/TrackName — the
        // wheel loads /home/moza/resource/maps/<key>.mztrack (AC captures:
        // "ac/imola"; the wheel logs "not exist" for an unknown key). A known
        // outline gives the exact key; otherwise the lowercased game prefix +
        // SimHub's track name, which is right only when the names coincide.
        internal static string TrackPath(string? simHubGameName, string? trackId, string? trackConfig, string? trackName)
        {
            var outline = Frames.TrackOutlines.Find(simHubGameName, trackId, trackConfig, trackName);
            if (outline != null) return outline.Path;
            if (string.IsNullOrEmpty(trackName)) return "";
            string prefix = Frames.TrackOutlines.GameFolder(simHubGameName) ?? Resolve(simHubGameName).ToLowerInvariant();
            return prefix.Length == 0 ? trackName! : prefix + "/" + trackName;
        }
    }
}
