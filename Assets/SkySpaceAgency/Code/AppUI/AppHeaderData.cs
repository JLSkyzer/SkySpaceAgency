using System;
using System.Globalization;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The header chips of the new window, from the active vessel (mockups: vessel with a status dot,
    /// "body · situation", Δv, TWR). Refreshed twice a second; without a vessel (editor, KSC) they
    /// show dashes.
    /// </summary>
    public class AppHeaderData
    {
        readonly AppChip _vessel, _where, _dv, _twr;
        float _next;

        public AppHeaderData(AppHeader header)
        {
            _vessel = header.AddChip("", "—", StatusKind.Neutral);
            _where = header.AddChip("", "—");
            _dv = header.AddChip("Δv", "—");
            _twr = header.AddChip("TWR", "—");
        }

        /// <summary>"4 812 m/s", the mockups' thousands separator.</summary>
        public static string MetresPerSecond(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + " m/s";

        /// <summary>"Orbit", "Sub orbital", "Pre launch": an enum name split into words.</summary>
        public static string Words(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_') { sb.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) && name[i - 1] != '_') { sb.Append(' '); c = char.ToLowerInvariant(c); }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public void Set(string vessel, bool running, string body, string situation, double dv, double twr)
        {
            _vessel.SetValue(string.IsNullOrEmpty(vessel) ? "—" : vessel);
            _vessel.SetDot(string.IsNullOrEmpty(vessel) ? StatusKind.Neutral : running ? StatusKind.Ok : StatusKind.Info);
            _where.SetValue(string.IsNullOrEmpty(body) ? "—" : body + " · " + situation);
            _dv.SetValue(MetresPerSecond(dv));
            _twr.SetValue(double.IsNaN(twr) || double.IsInfinity(twr) ? "—" : twr.ToString("0.00", CultureInfo.InvariantCulture));
        }

        /// <summary>Reads the active vessel (twice a second). Any game error leaves the chips as they were.</summary>
        public void Tick(AppTabContext context, bool anyRunning)
        {
            if (!context.InGame || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            try
            {
                var vc = context.Plugin.current_vessel?.VesselComponent;
                if (vc == null) { Set(null, false, null, null, double.NaN, double.NaN); return; }
                var dv = vc.VesselDeltaV;
                double twr = double.NaN;
                var stages = dv?.StageInfo;
                if (stages != null && stages.Count > 0 && stages[0] != null) twr = stages[0].TWRActual;
                Set(vc.DisplayName, anyRunning, vc.mainBody?.Name, Words(vc.Situation.ToString()),
                    dv != null ? dv.TotalDeltaVActual : double.NaN, twr);
            }
            catch (Exception) { }
        }
    }
}
