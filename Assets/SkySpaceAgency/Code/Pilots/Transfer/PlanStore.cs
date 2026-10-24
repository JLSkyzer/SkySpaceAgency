using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.Transfer
{
    public enum TransferPhase { Planned, WaitWindow, PlaneChange, OrbitChange, Injection, Escape, Correction, Coast, Capture, Done, Failed }

    // A plan saved for one vessel, so that it survives a save/reload, a scene change and a vessel
    // switch. The game glue (TransferPilot) writes it in <mod folder>/plans/.
    public class SavedPlan
    {
        public int Version = 1;
        public string VesselId;
        public string Kind = KindTransfer; // later sub-projects add their own kinds
        public TransferPlan Plan;          // null for a rendezvous (it always re-plans on resume)
        public TransferPhase Phase;
        public int StepIndex;            // the next step to fly; the steps before it are done
        public double SavedUT;
        public bool PrepareOnly;
        // The burn of step StepIndex has ignited: it was cut if the plan is resumed before that step
        // is done. Kept through a failure (Phase is then Failed). Optional in the file (false).
        public bool BurnStarted;
        // A direct descent's entry correction (a step index) is solved without the stale planned
        // arrival. -1: none. Optional in the file (-1).
        public int FreeArrivalStep = -1;

        // Bodies are kept by name: a body index may change between sessions (a planet pack added).
        public string FromBodyName, ToBodyName;
        // The name of each body index the plan uses, as it was when the plan was saved.
        public List<string> BodyNames = new List<string>();

        // Kind "rendezvous" (sub-project 4.3) only; null for a transfer. Optional in the file.
        public SavedRendezvous Rendezvous;

        public const string KindTransfer = "transfer", KindRendezvous = "rendezvous";
    }

    // What a rendezvous needs to start again: its target, what was asked, and how far it got. The
    // steps are not kept: a resume always plans again from the actual states.
    public class SavedRendezvous
    {
        public string TargetId;       // the target vessel's GUID
        public string TargetName;
        public string Mode;           // RvMode's name
        public string Phase;          // RvPhase's name when saved
        public string OurPortId;      // the docking pair the player picked; null = automatic
        public string TargetPortId;
    }

    public enum ResumeVerdict { Resume, Replan, Mismatch }

    public class ResumeCheck
    {
        public ResumeVerdict Verdict;
        public string Reason; // for the player; MismatchMessage on a mismatch
        public string Detail; // why, for the log

        public static ResumeCheck Resume() => new ResumeCheck { Verdict = ResumeVerdict.Resume, Reason = "", Detail = "" };
        public static ResumeCheck Replan(string why) => new ResumeCheck { Verdict = ResumeVerdict.Replan, Reason = why, Detail = why };
        public static ResumeCheck Mismatch(string why) =>
            new ResumeCheck { Verdict = ResumeVerdict.Mismatch, Reason = PlanStore.MismatchMessage, Detail = why };
    }

    // Pure: the JSON form of a SavedPlan, and the check made before resuming it. No file access.
    public static class PlanStore
    {
        public const int CurrentVersion = 1;
        public const string MismatchMessage = "plan no longer matches the vessel: re-plan";
        public const double EnergyTolerance = 0.05;
        public const double MissedBurnSeconds = 60;

        public static string FileName(string vesselId) => $"plan_{vesselId}.json";

        // One file per vessel and kind: a rendezvous never overwrites a transfer's plan.
        public static string FileName(string vesselId, string kind) =>
            kind == null || kind == SavedPlan.KindTransfer ? FileName(vesselId) : $"{kind}_{vesselId}.json";

        // ---------- JSON ----------

        public static string ToJson(SavedPlan p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            var o = new JObject
            {
                ["version"] = p.Version,
                ["vesselId"] = p.VesselId,
                ["kind"] = p.Kind,
                ["phase"] = p.Phase.ToString(),
                ["stepIndex"] = p.StepIndex,
                ["savedUT"] = D(p.SavedUT),
                ["prepareOnly"] = p.PrepareOnly,
                ["burnStarted"] = p.BurnStarted,
                ["freeArrivalStep"] = p.FreeArrivalStep,
                ["fromBody"] = p.FromBodyName,
                ["toBody"] = p.ToBodyName,
                ["bodyNames"] = new JArray((p.BodyNames ?? new List<string>()).Cast<object>().ToArray()),
                ["plan"] = p.Plan == null ? JValue.CreateNull() : PlanJson(p.Plan),
            };
            if (p.Rendezvous != null)
                o["rendezvous"] = new JObject
                {
                    ["targetId"] = p.Rendezvous.TargetId,
                    ["targetName"] = p.Rendezvous.TargetName,
                    ["mode"] = p.Rendezvous.Mode,
                    ["phase"] = p.Rendezvous.Phase,
                    ["ourPortId"] = p.Rendezvous.OurPortId,
                    ["targetPortId"] = p.Rendezvous.TargetPortId,
                };
            return o.ToString(Formatting.Indented);
        }

        static JObject PlanJson(TransferPlan plan) => new JObject
        {
            ["ok"] = plan.Ok,
            ["refusal"] = plan.Refusal,
            ["departureUT"] = D(plan.DepartureUT),
            ["arrivalUT"] = D(plan.ArrivalUT),
            ["totalDeltaV"] = D(plan.TotalDeltaV),
            ["reserveDeltaV"] = D(plan.ReserveDeltaV),
            ["caseName"] = plan.CaseName,
            ["warnings"] = new JArray((plan.Warnings ?? new List<string>()).Cast<object>().ToArray()),
            ["excessBody"] = plan.ExcessBody,
            ["requiredExcess"] = V(plan.RequiredExcess),
            ["request"] = plan.Request == null ? JValue.CreateNull() : RequestJson(plan.Request),
            ["steps"] = new JArray((plan.Steps ?? new List<PlanStep>()).Select(StepJson).Cast<object>().ToArray()),
        };

        static JObject RequestJson(TransferRequest q) => new JObject
        {
            ["fromBody"] = q.FromBody,
            ["toBody"] = q.ToBody,
            ["r0"] = V(q.R0),
            ["v0"] = V(q.V0),
            ["ut0"] = D(q.UT0),
            ["arrival"] = q.Arrival.ToString(),
            ["arrivalPeriapsisRadius"] = D(q.ArrivalPeriapsisRadius),
            ["arrivalApoapsisRadius"] = D(q.ArrivalApoapsisRadius),
            ["arrivalInclinationDeg"] = D(q.ArrivalInclinationDeg),
            ["maxTripSeconds"] = D(q.MaxTripSeconds),
        };

        static JObject StepJson(PlanStep s) => new JObject
        {
            ["kind"] = s.Kind.ToString(),
            ["body"] = s.Body,
            ["ut"] = D(s.UT),
            ["deltaV"] = V(s.DeltaV),
            ["deltaVMagnitude"] = D(s.DeltaVMagnitude),
            ["explanation"] = s.Explanation,
            ["targetBody"] = s.TargetBody,
            ["targetPeriapsisRadius"] = D(s.TargetPeriapsisRadius),
            ["targetApoapsisRadius"] = D(s.TargetApoapsisRadius),
            ["targetInclinationDeg"] = D(s.TargetInclinationDeg),
            ["legIndex"] = s.LegIndex,
            ["replanAtLegStart"] = s.ReplanAtLegStart,
        };

        // Doubles are written with "G17", which always reads back to the same bits. The values JSON
        // numbers cannot hold (NaN, the infinities, and -0, which a number parser may read as +0)
        // are written as strings.
        static JToken D(double d)
        {
            if (double.IsNaN(d)) return "NaN";
            if (double.IsPositiveInfinity(d)) return "Infinity";
            if (double.IsNegativeInfinity(d)) return "-Infinity";
            if (d == 0 && BitConverter.DoubleToInt64Bits(d) < 0) return "-0";
            return new JRaw(d.ToString("G17", CultureInfo.InvariantCulture));
        }

        static JArray V(Vector3d v) => new JArray(D(v.x), D(v.y), D(v.z));

        // Refuses an unknown version, a missing field or a wrong type, and never throws.
        public static bool TryFromJson(string json, out SavedPlan p, out string error)
        {
            p = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the plan file is empty";
                return false;
            }
            try
            {
                JToken root;
                using (var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                })
                {
                    root = JToken.ReadFrom(reader);
                    if (reader.Read() && reader.TokenType != JsonToken.Comment)
                        throw new FormatException("text after the plan");
                }
                if (!(root is JObject o))
                    throw new FormatException("the plan is not a JSON object");

                int version = Int(o, "version");
                if (version != CurrentVersion)
                    throw new FormatException($"unknown plan version {version.ToString(CultureInfo.InvariantCulture)}");

                var saved = new SavedPlan
                {
                    Version = version,
                    VesselId = Str(o, "vesselId", allowNull: false),
                    Kind = Str(o, "kind", allowNull: false),
                    Phase = Enum<TransferPhase>(o, "phase"),
                    StepIndex = Int(o, "stepIndex"),
                    SavedUT = Dbl(o, "savedUT"),
                    PrepareOnly = Bool(o, "prepareOnly"),
                    BurnStarted = o.ContainsKey("burnStarted") && Bool(o, "burnStarted"),
                    FreeArrivalStep = o.ContainsKey("freeArrivalStep") ? Int(o, "freeArrivalStep") : -1,
                    FromBodyName = Str(o, "fromBody", allowNull: false),
                    ToBodyName = Str(o, "toBody", allowNull: false),
                    BodyNames = Arr(o, "bodyNames").Select((t, i) => StrValue(t, $"bodyNames[{i}]")).ToList(),
                };
                if (o.ContainsKey("rendezvous"))
                {
                    var r = Obj(Field(o, "rendezvous"), "rendezvous");
                    saved.Rendezvous = new SavedRendezvous
                    {
                        TargetId = Str(r, "targetId", allowNull: false),
                        TargetName = Str(r, "targetName", allowNull: true),
                        Mode = Str(r, "mode", allowNull: false),
                        Phase = Str(r, "phase", allowNull: false),
                        OurPortId = Str(r, "ourPortId", allowNull: true),
                        TargetPortId = Str(r, "targetPortId", allowNull: true),
                    };
                }
                bool rendezvous = saved.Kind == SavedPlan.KindRendezvous;
                if (rendezvous && saved.Rendezvous == null)
                    throw new FormatException("missing field 'rendezvous'");
                var planToken = Field(o, "plan");
                if (planToken.Type == JTokenType.Null)
                {
                    if (!rendezvous)
                        throw new FormatException("the field 'plan' is null");
                }
                else
                    saved.Plan = ReadPlan(Obj(planToken, "plan"));
                if (saved.Plan != null && (saved.StepIndex < 0 || saved.StepIndex > saved.Plan.Steps.Count))
                    throw new FormatException($"the field 'stepIndex' ({saved.StepIndex}) is outside the plan's {saved.Plan.Steps.Count} step(s)");
                p = saved;
                return true;
            }
            catch (Exception e)
            {
                p = null;
                error = e is FormatException || e is JsonException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        static TransferPlan ReadPlan(JObject o)
        {
            var plan = new TransferPlan
            {
                Ok = Bool(o, "ok"),
                Refusal = Str(o, "refusal", allowNull: true),
                DepartureUT = Dbl(o, "departureUT"),
                ArrivalUT = Dbl(o, "arrivalUT"),
                TotalDeltaV = Dbl(o, "totalDeltaV"),
                ReserveDeltaV = Dbl(o, "reserveDeltaV"),
                CaseName = Str(o, "caseName", allowNull: false),
                Warnings = Arr(o, "warnings").Select((t, i) => StrValue(t, $"warnings[{i}]")).ToList(),
                ExcessBody = Int(o, "excessBody"),
                RequiredExcess = Vec(o, "requiredExcess"),
                Steps = Arr(o, "steps").Select((t, i) => ReadStep(Obj(t, $"steps[{i}]"), i)).ToList(),
            };
            var q = Field(o, "request");
            plan.Request = q.Type == JTokenType.Null ? null : ReadRequest(Obj(q, "request"));
            return plan;
        }

        static TransferRequest ReadRequest(JObject o) => new TransferRequest
        {
            FromBody = Int(o, "fromBody"),
            ToBody = Int(o, "toBody"),
            R0 = Vec(o, "r0"),
            V0 = Vec(o, "v0"),
            UT0 = Dbl(o, "ut0"),
            Arrival = Enum<ArrivalKind>(o, "arrival"),
            ArrivalPeriapsisRadius = Dbl(o, "arrivalPeriapsisRadius"),
            ArrivalApoapsisRadius = Dbl(o, "arrivalApoapsisRadius"),
            ArrivalInclinationDeg = Dbl(o, "arrivalInclinationDeg"),
            MaxTripSeconds = Dbl(o, "maxTripSeconds"),
        };

        static PlanStep ReadStep(JObject o, int i)
        {
            try
            {
                return new PlanStep
                {
                    Kind = Enum<StepKind>(o, "kind"),
                    Body = Int(o, "body"),
                    UT = Dbl(o, "ut"),
                    DeltaV = Vec(o, "deltaV"),
                    DeltaVMagnitude = Dbl(o, "deltaVMagnitude"),
                    Explanation = Str(o, "explanation", allowNull: false),
                    TargetBody = Int(o, "targetBody"),
                    TargetPeriapsisRadius = Dbl(o, "targetPeriapsisRadius"),
                    TargetApoapsisRadius = Dbl(o, "targetApoapsisRadius"),
                    TargetInclinationDeg = Dbl(o, "targetInclinationDeg"),
                    LegIndex = Int(o, "legIndex"),
                    ReplanAtLegStart = Bool(o, "replanAtLegStart"),
                };
            }
            catch (FormatException e)
            {
                throw new FormatException($"step {i}: {e.Message}");
            }
        }

        static JToken Field(JObject o, string name)
        {
            if (!o.TryGetValue(name, StringComparison.Ordinal, out var t))
                throw new FormatException($"missing field '{name}'");
            return t;
        }

        static JObject Obj(JToken t, string name) =>
            t as JObject ?? throw new FormatException($"the field '{name}' is not an object");

        static IEnumerable<JToken> Arr(JObject o, string name) =>
            Field(o, name) as JArray ?? throw new FormatException($"the field '{name}' is not an array");

        static int Int(JObject o, string name)
        {
            var t = Field(o, name);
            if (t.Type != JTokenType.Integer)
                throw new FormatException($"the field '{name}' is not an integer");
            long l = t.Value<long>();
            if (l < int.MinValue || l > int.MaxValue)
                throw new FormatException($"the field '{name}' is out of range");
            return (int)l;
        }

        static bool Bool(JObject o, string name)
        {
            var t = Field(o, name);
            if (t.Type != JTokenType.Boolean)
                throw new FormatException($"the field '{name}' is not true or false");
            return t.Value<bool>();
        }

        static string Str(JObject o, string name, bool allowNull)
        {
            var t = Field(o, name);
            if (t.Type == JTokenType.Null && allowNull)
                return null;
            return StrValue(t, name);
        }

        static string StrValue(JToken t, string name)
        {
            if (t.Type != JTokenType.String)
                throw new FormatException($"the field '{name}' is not a string");
            return t.Value<string>();
        }

        static T Enum<T>(JObject o, string name) where T : struct
        {
            string s = Str(o, name, allowNull: false);
            if (!System.Enum.TryParse(s, false, out T value) || !System.Enum.IsDefined(typeof(T), value) || int.TryParse(s, out _))
                throw new FormatException($"the field '{name}' has an unknown value '{s}'");
            return value;
        }

        static double Dbl(JObject o, string name) => DblValue(Field(o, name), name);

        static double DblValue(JToken t, string name)
        {
            switch (t.Type)
            {
                case JTokenType.Float:
                case JTokenType.Integer:
                    return t.Value<double>();
                case JTokenType.String:
                    switch (t.Value<string>())
                    {
                        case "NaN": return double.NaN;
                        case "Infinity": return double.PositiveInfinity;
                        case "-Infinity": return double.NegativeInfinity;
                        case "-0": return -0.0;
                    }
                    break;
            }
            throw new FormatException($"the field '{name}' is not a number");
        }

        static Vector3d Vec(JObject o, string name)
        {
            var a = Field(o, name) as JArray;
            if (a == null || a.Count != 3)
                throw new FormatException($"the field '{name}' is not a vector of 3 numbers");
            return new Vector3d(DblValue(a[0], name), DblValue(a[1], name), DblValue(a[2], name));
        }

        // ---------- Resume check ----------

        // Before a saved plan resumes. The pilot always re-plans from the actual state; this says
        // whether the plan's sequence still applies (Resume), whether the search must run again
        // (Replan), or whether the plan cannot be followed any more (Mismatch).
        // currentBody, r and v: the vessel's body in s and its state relative to it, at ut.
        public static ResumeCheck Check(SavedPlan p, BodySystem s, int currentBody, Vector3d r, Vector3d v, double ut)
        {
            if (p?.Plan == null || s == null)
                return ResumeCheck.Mismatch("no plan or no body system");
            var plan = p.Plan;

            // Bodies, by name.
            var byName = new Dictionary<string, int>();
            for (int i = 0; i < s.Count; i++)
                byName[s[i].Name] = i;
            foreach (string name in new[] { p.FromBodyName, p.ToBodyName })
                if (name == null || !byName.ContainsKey(name))
                    return ResumeCheck.Mismatch($"the body {name ?? "(none)"} no longer exists");

            int Map(int savedIndex, out string missing)
            {
                missing = null;
                if (savedIndex < 0 || p.BodyNames == null || savedIndex >= p.BodyNames.Count)
                {
                    missing = $"body #{savedIndex} (not in the saved names)";
                    return -1;
                }
                string name = p.BodyNames[savedIndex];
                if (name == null || !byName.TryGetValue(name, out int now))
                {
                    missing = name ?? $"body #{savedIndex}";
                    return -1;
                }
                return now;
            }

            var passes = new HashSet<int> { byName[p.FromBodyName], byName[p.ToBodyName] };
            var stepBody = new int[plan.Steps.Count];
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                stepBody[i] = Map(st.Body, out string missing);
                if (stepBody[i] < 0)
                    return ResumeCheck.Mismatch($"the body {missing} of step {i} no longer exists");
                passes.Add(stepBody[i]);
                if (st.TargetBody >= 0)
                {
                    int target = Map(st.TargetBody, out missing);
                    if (target < 0)
                        return ResumeCheck.Mismatch($"the target body {missing} of step {i} no longer exists");
                    passes.Add(target);
                }
            }

            int fromNow = -1;
            if (plan.Request != null)
            {
                fromNow = Map(plan.Request.FromBody, out string missing);
                int toNow = Map(plan.Request.ToBody, out string missingTo);
                if (fromNow != byName[p.FromBodyName] || toNow != byName[p.ToBodyName])
                    return ResumeCheck.Mismatch(
                        $"the request goes from {missing ?? s[fromNow].Name} to {missingTo ?? s[toNow].Name}, the plan from {p.FromBodyName} to {p.ToBodyName}");
            }

            if (currentBody < 0 || currentBody >= s.Count)
                return ResumeCheck.Mismatch($"the vessel's body #{currentBody} is unknown");
            if (!passes.Contains(currentBody))
                return ResumeCheck.Mismatch($"the vessel orbits {s[currentBody].Name}, which the plan to {p.ToBodyName} does not pass through");
            if (p.Phase == TransferPhase.Done)
                return ResumeCheck.Mismatch("the plan is already done");

            if (ut < p.SavedUT - 1)
                return ResumeCheck.Replan(
                    $"the game is {Seconds(p.SavedUT - ut)} earlier than when the plan was saved (an older save was loaded)");

            bool stepPending = false;
            for (int i = p.StepIndex; i < plan.Steps.Count; i++)
                if (plan.Steps[i].Kind != StepKind.Escape)
                    stepPending = true;

            // The orbit the plan expects now, as an energy, where it is known.
            if (ExpectedEnergy(p, s, stepBody, fromNow, out int expectedBody, out double expected) && expectedBody == currentBody)
            {
                double mu = s[currentBody].Mu;
                double actual = v.sqrMagnitude / 2 - mu / r.magnitude;
                // Relative to the expected energy, with an absolute floor of mu/r: near a parabolic
                // orbit the expected energy is close to 0 and any change would count as huge.
                double scale = Math.Max(Math.Abs(expected), mu / r.magnitude);
                double off = Math.Abs(actual - expected) / Math.Max(scale, 1e-9);
                if (off > EnergyTolerance)
                {
                    string why = $"the orbit around {s[currentBody].Name} differs from the plan by {off * 100:F1} % in energy";
                    return stepPending
                        ? ResumeCheck.Replan(why + " (burned by hand?)")
                        : ResumeCheck.Mismatch(why + ", with no step left to fix it");
                }
            }

            // A burn whose time has passed.
            for (int i = p.StepIndex; i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                if (st.Kind == StepKind.Escape || st.Kind == StepKind.Correction)
                    continue; // a milestone; corrections are re-timed in flight
                if (st.UT < ut - MissedBurnSeconds)
                    return ResumeCheck.Replan($"the {st.Kind} burn was due {Seconds(ut - st.UT)} ago");
                break;
            }

            return ResumeCheck.Resume();
        }

        // Walks the steps already flown from the request's starting orbit. Burns with a known vector
        // inside one body are applied to the propagated state; a capture sets its target orbit; an
        // SOI change or a correction (solved in flight) leaves the orbit unknown.
        static bool ExpectedEnergy(SavedPlan p, BodySystem s, int[] stepBody, int fromNow, out int body, out double energy)
        {
            body = -1;
            energy = double.NaN;
            var plan = p.Plan;
            bool haveState = false, haveEnergy = false;
            Vector3d r = Vector3d.zero, v = Vector3d.zero;
            double ut = 0;
            if (plan.Request != null && fromNow >= 0)
            {
                body = fromNow;
                r = plan.Request.R0;
                v = plan.Request.V0;
                ut = plan.Request.UT0;
                haveState = true;
            }

            for (int i = 0; i < p.StepIndex && i < plan.Steps.Count; i++)
            {
                var st = plan.Steps[i];
                int b = stepBody[i];
                switch (st.Kind)
                {
                    case StepKind.Injection:
                    case StepKind.PlaneChange:
                    case StepKind.OrbitChange:
                        if (haveState && b == body && st.DeltaV.sqrMagnitude > 0)
                        {
                            KeplerPropagator.Propagate(r, v, s[body].Mu, st.UT - ut, out r, out v);
                            v += st.DeltaV;
                            ut = st.UT;
                        }
                        else
                            haveState = haveEnergy = false;
                        break;
                    case StepKind.Capture:
                        haveState = false;
                        haveEnergy = !double.IsNaN(st.TargetPeriapsisRadius);
                        if (haveEnergy)
                        {
                            body = b;
                            double pe = st.TargetPeriapsisRadius;
                            double ap = double.IsNaN(st.TargetApoapsisRadius) ? pe : st.TargetApoapsisRadius;
                            energy = -s[b].Mu / (pe + ap);
                        }
                        break;
                    default: // Escape, Correction
                        haveState = haveEnergy = false;
                        break;
                }
            }

            if (haveState)
                energy = v.sqrMagnitude / 2 - s[body].Mu / r.magnitude;
            return (haveState || haveEnergy) && !double.IsNaN(energy) && !double.IsInfinity(energy);
        }

        static string Seconds(double s) => s < 120
            ? $"{s.ToString("F0", CultureInfo.InvariantCulture)} s"
            : $"{(s / 60).ToString("F0", CultureInfo.InvariantCulture)} min";
    }
}
