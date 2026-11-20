using System.Collections.Generic;
using System.Globalization;

namespace SkySpaceAgency.Bridge
{
    // The text of the state log lines (pure).
    public static class BridgeFormat
    {
        public static string State(BridgeState s)
        {
            if (s == null || !s.Available) return "Redux's stage recovery is unavailable";
            var counts = new SortedDictionary<string, int>();
            foreach (var r in s.Records)
            {
                string k = r.Status.ToString();
                counts.TryGetValue(k, out int n);
                counts[k] = n + 1;
            }
            var parts = new List<string>();
            foreach (var kv in counts) parts.Add($"{kv.Key}={kv.Value}");
            return string.Format(CultureInfo.InvariantCulture,
                "phase {0}, capture pending {1}, transitioning {2}, replay active {3}, can retry {4}, recovery points on {5}, save delay {6}, docked {7}, active record {8}, {9} record(s) [{10}]",
                s.Phase, s.IsCapturePending, s.IsTransitioning, s.IsReplayActive, s.CanRetry, s.CreatePoints,
                float.IsNaN(s.SaveDelaySeconds) ? "unreadable" : s.SaveDelaySeconds.ToString("0.##", CultureInfo.InvariantCulture) + " s",
                s.ActiveReplayDocked, string.IsNullOrEmpty(s.ActiveRecordId) ? "-" : s.ActiveRecordId, s.Records.Count, string.Join(", ", parts));
        }

        public static IEnumerable<string> Records(BridgeState s)
        {
            if (s == null) yield break;
            foreach (var r in s.Records)
                yield return "record " + BridgeWatch.Describe(r);
        }
    }
}
