using System.Collections.Generic;
using System.Globalization;

namespace SkySpaceAgency.Bridge
{
    // Turns successive reads of Redux's stage recovery into log lines for what changed (pure).
    // Silent when nothing changed, so a 5 Hz poll costs nothing in the log.
    public sealed class BridgeWatch
    {
        BridgeState _last;
        readonly Dictionary<string, BridgeRecord> _records = new Dictionary<string, BridgeRecord>();

        public IReadOnlyList<string> Observe(BridgeState state)
        {
            state = state ?? BridgeState.Unavailable;
            var lines = new List<string>();

            if (_last == null)
            {
                lines.Add(state.Available
                    ? $"first read: phase {state.Phase}, {state.Records.Count} recovery point(s), capture pending {state.IsCapturePending}"
                    : "first read: Redux's stage recovery is unavailable");
            }
            else
            {
                if (_last.Available != state.Available)
                    lines.Add(state.Available ? "Redux's stage recovery became available" : "Redux's stage recovery became unavailable");
                if (_last.Phase != state.Phase)
                    lines.Add($"phase {_last.Phase} -> {state.Phase}");
                if (_last.IsCapturePending != state.IsCapturePending)
                    lines.Add("capture pending " + (state.IsCapturePending ? "on" : "off"));
                if (_last.ActiveRecordId != state.ActiveRecordId)
                    lines.Add($"active record {_last.ActiveRecordId ?? "-"} -> {state.ActiveRecordId ?? "-"}");
                if (_last.IsTransitioning != state.IsTransitioning)
                    lines.Add("transitioning " + (state.IsTransitioning ? "on" : "off"));
            }

            var now = new Dictionary<string, BridgeRecord>();
            foreach (var r in state.Records)
                if (r != null && r.Id != null) now[r.Id] = r;

            // Records are reported only between two reads of an available manager: after the first read, or when
            // Redux comes back, the list is a fresh baseline (a loaded save brings its records, they were not captured now).
            if (_last != null && _last.Available && state.Available)
            {
                // "saved" only when the capture itself was seen (pending on at the previous read or this one).
                bool capture = _last.IsCapturePending || state.IsCapturePending;
                foreach (var kv in now)
                {
                    if (!_records.TryGetValue(kv.Key, out var old))
                        lines.Add((capture ? "recovery point saved: " : "recovery point appeared (no capture seen, loaded or restored): ") + Describe(kv.Value));
                    else if (old.Status != kv.Value.Status)
                        lines.Add($"recovery point {kv.Key} '{kv.Value.VesselName}' status {old.Status} -> {kv.Value.Status}");
                }
                foreach (var kv in _records)
                    if (!now.ContainsKey(kv.Key))
                        lines.Add($"recovery point removed: {kv.Key} '{kv.Value.VesselName}'");
            }

            _records.Clear();
            foreach (var kv in now)
                _records[kv.Key] = Copy(kv.Value);
            _last = Copy(state);
            return lines;
        }

        public static string Describe(BridgeRecord r)
            => string.Format(CultureInfo.InvariantCulture,
                "{0} '{1}' guid {2} parent {3} UT {4:0.##} MET {5} status {6}",
                r.Id, r.VesselName, r.VesselGuid, r.ParentVesselGuid, r.CapturedUT,
                r.CapturedMET.HasValue ? r.CapturedMET.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-", r.Status);

        static BridgeRecord Copy(BridgeRecord r) => new BridgeRecord
        {
            Id = r.Id, VesselGuid = r.VesselGuid, ParentVesselGuid = r.ParentVesselGuid, VesselName = r.VesselName,
            Origin = r.Origin, LastError = r.LastError, CapturedUT = r.CapturedUT, CapturedMET = r.CapturedMET,
            CompletedUT = r.CompletedUT, Status = r.Status,
        };

        // Only the fields compared next time; the records are tracked separately.
        static BridgeState Copy(BridgeState s) => new BridgeState
        {
            Available = s.Available, Phase = s.Phase, IsCapturePending = s.IsCapturePending,
            IsTransitioning = s.IsTransitioning, ActiveRecordId = s.ActiveRecordId,
        };
    }
}
