using System;
using System.Collections.Generic;
using System.Globalization;
using K2D2.Transfer;

namespace K2D2.Rendezvous
{
    // The Rendezvous tab's lists and texts (RendezvousUI is the game glue). Pure, tested in EditMode.
    public static class RendezvousReadout
    {
        // One dropdown entry. A header (a body's name) is shown but cannot be picked: Id is null.
        public struct Choice
        {
            public string Id;      // vessel or part GUID; null for a header or "Auto"
            public string Label;   // unique in its list
            public bool Header;
        }

        public struct PortEntry
        {
            public string Id;
            public string Name;
            public bool Free;      // Data_DockingNode.CurrentState == Ready
        }

        public struct StepRow
        {
            public string Kind;        // "Intercept burn"
            public string When;        // "in 12m 30s", "now", "done"
            public string DeltaV;
            public string Explanation;
            public bool Done;
        }

        public const string AutoLabel = "Auto";
        public const string NoTargetLabel = "Choose a vessel";
        // No-break spaces, as BodyChoices.Indent: a dropdown keeps them.
        public const string Indent = BodyChoices.Indent;

        // ------------------------------------------------------------ targets

        // Each body as a header, then its vessels indented, in the groups' order (ours first).
        // Two vessels with the same name are told apart by a number, so every label is unique.
        public static List<Choice> Targets(IReadOnlyList<(string body, List<VesselEntry> vessels)> groups)
        {
            var result = new List<Choice>();
            var used = new HashSet<string>();
            if (groups == null) return result;
            foreach (var (body, vessels) in groups)
            {
                if (vessels == null || vessels.Count == 0) continue;
                string header = string.IsNullOrEmpty(body) ? "Unknown body" : body;
                result.Add(new Choice { Label = Unique(header, used), Header = true });
                foreach (var v in vessels)
                    result.Add(new Choice { Id = v.Id, Label = Unique(Indent + (string.IsNullOrEmpty(v.Name) ? "Unnamed vessel" : v.Name), used) });
            }
            return result;
        }

        // The game's target when it is in the list, else the one picked before, else -1.
        public static int PreSelect(IReadOnlyList<Choice> choices, string gameTargetId, string rememberedId)
        {
            int i = FindId(choices, gameTargetId);
            return i >= 0 ? i : FindId(choices, rememberedId);
        }

        // ------------------------------------------------------------ ports

        // "Auto" first, then each port numbered in the vessel's order, "(in use)" when not free.
        public static List<Choice> Ports(IEnumerable<PortEntry> ports)
        {
            var result = new List<Choice> { new Choice { Label = AutoLabel } };
            if (ports == null) return result;
            int n = 0;
            foreach (var p in ports)
            {
                n++;
                string name = string.IsNullOrEmpty(p.Name) ? "port" : p.Name;
                result.Add(new Choice { Id = p.Id, Label = $"{n}. {name}" + (p.Free ? "" : " (in use)") });
            }
            return result;
        }

        // ------------------------------------------------------------ lists

        public static List<string> Labels(IReadOnlyList<Choice> choices)
        {
            var labels = new List<string>(choices?.Count ?? 0);
            if (choices != null)
                foreach (var c in choices) labels.Add(c.Label);
            return labels;
        }

        public static int FindLabel(IReadOnlyList<Choice> choices, string label)
        {
            if (choices == null) return -1;
            for (int i = 0; i < choices.Count; i++)
                if (choices[i].Label == label) return i;
            return -1;
        }

        // Never a header or "Auto": their Id is null.
        public static int FindId(IReadOnlyList<Choice> choices, string id)
        {
            if (choices == null || string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < choices.Count; i++)
                if (choices[i].Id == id) return i;
            return -1;
        }

        static string Unique(string label, HashSet<string> used)
        {
            string l = label;
            for (int k = 2; !used.Add(l); k++) l = $"{label} ({k})";
            return l;
        }

        // ------------------------------------------------------------ live readouts

        // Dock only needs the target within RendezvousFlight.DockOnlyRange.
        public static bool DockOnlyAllowed(double distance) =>
            !double.IsNaN(distance) && distance <= RendezvousFlight.DockOnlyRange;

        public static string Distance(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "-";
            return metres < 10000 ? Inv($"{metres:F0} m") : Inv($"{metres / 1000:F1} km");
        }

        public static string Relative(double distance, double speed, double inclinationDeg) =>
            "Distance " + Distance(distance) +
            (double.IsNaN(speed) ? "" : Inv($", relative speed {speed:F1} m/s")) +
            (double.IsNaN(inclinationDeg) ? "" : Inv($", relative inclination {inclinationDeg:F2}°"));

        // ------------------------------------------------------------ plan

        // One row per step; steps before doneBefore are marked flown.
        public static List<StepRow> Steps(RvPlan plan, double now, int doneBefore)
        {
            var rows = new List<StepRow>();
            if (plan?.Steps == null) return rows;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var s = plan.Steps[i];
                bool done = i < doneBefore;
                double dt = s.UT - now;
                rows.Add(new StepRow
                {
                    Kind = Capitalize(RendezvousFlight.StepName(s.Kind)),
                    When = done ? "done" : dt > 0 ? "in " + KerbinCalendar.Duration(dt) : "now",
                    DeltaV = Inv($"{s.DeltaVMagnitude:F1} m/s"),
                    Explanation = s.Explanation ?? "",
                    Done = done,
                });
            }
            return rows;
        }

        public static string Totals(RvPlan plan, BudgetResult budget)
        {
            if (plan == null) return "";
            string text = Inv($"Total {plan.TotalDeltaV:F1} m/s");
            if (plan.Revolutions > 0) text += $", {plan.Revolutions} full revolution(s) before the arrival";
            if (budget != null)
            {
                double margin = budget.Available - budget.Needed;
                text += Inv($"; the vessel has {budget.Available:F0} m/s, ") +
                    (margin >= 0 ? Inv($"{margin:F0} m/s left") : Inv($"short by {-margin:F0} m/s"));
            }
            return text;
        }

        // The stages in firing order with the steps each pays for. budget comes from
        // RendezvousFlight.Budget(plan, from, …): its step indices start at 'from'.
        public static List<string> Stages(RvPlan plan, int from, IReadOnlyList<StageDv> stages, BudgetResult budget)
        {
            var rows = new List<string>();
            if (stages == null) return rows;
            var pays = new Dictionary<int, List<string>>();
            bool unpaid = false;
            if (budget != null && plan?.Steps != null)
                foreach (var (step, stage) in budget.Allocation)
                {
                    int i = from + step;
                    if (stage < 0) { unpaid = true; continue; }
                    if (i < 0 || i >= plan.Steps.Count) continue;
                    if (!pays.TryGetValue(stage, out var names)) pays[stage] = names = new List<string>();
                    string n = RendezvousFlight.StepName(plan.Steps[i].Kind);
                    if (!names.Contains(n)) names.Add(n);
                }
            foreach (var st in stages)
                rows.Add(Inv($"Stage {st.Stage}: {st.DeltaVVac:F0} m/s") +
                    (pays.TryGetValue(st.Stage, out var names) ? ", pays the " + string.Join(", ", names) : ""));
            if (unpaid) rows.Add("No stage is left for the last step(s).");
            return rows;
        }

        // ------------------------------------------------------------ docking

        public static string TranslateLine(TranslateMode mode, string note)
        {
            string head;
            switch (mode)
            {
                case TranslateMode.Rcs: head = "Translation: RCS"; break;
                case TranslateMode.RcsWithEngine: head = "Translation: RCS and main engine"; break;
                case TranslateMode.MainEngine: head = "Translation: main engine"; break;
                default: head = "Translation: refused"; break;
            }
            return string.IsNullOrEmpty(note) ? head : head + ". " + note;
        }

        public static string PhaseName(DockPhase phase)
        {
            switch (phase)
            {
                case DockPhase.Align: return "aligning on the port axis";
                case DockPhase.Hold50: return "holding at 50 m";
                case DockPhase.Approach10: return "approaching to 10 m";
                case DockPhase.Hold10: return "holding at 10 m";
                case DockPhase.Final: return "final approach";
                case DockPhase.Backoff: return "backing off to retry";
                case DockPhase.Docked: return "contact";
                default: return "aborted";
            }
        }

        public static string DockLine(DockPhase phase, double distance, double lateral, double closing)
        {
            string text = "Docking: " + PhaseName(phase);
            if (!double.IsNaN(distance))
                text += Inv($", {distance:F1} m from the port");
            if (!double.IsNaN(lateral))
                text += Inv($", lateral {lateral:F2} m");
            if (!double.IsNaN(closing))
                text += Inv($", closing {closing:F2} m/s");
            return text;
        }

        public static string PortsLine(string ours, string theirs) =>
            $"Ports: {ours ?? "?"} on our vessel, {theirs ?? "?"} on the target";

        public static string AbortLine(int aborts, string reason) =>
            aborts <= 0 ? "" : $"Aborts: {aborts}" + (string.IsNullOrEmpty(reason) ? "" : $" (last: {reason})");

        static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        static string Inv(FormattableString f) => f.ToString(CultureInfo.InvariantCulture);
    }
}
