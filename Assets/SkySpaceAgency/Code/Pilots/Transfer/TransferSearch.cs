using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Transfer
{
    // The cheapest transfer from the vessel's orbit to any body (patched conics). The case comes from
    // the body hierarchy only, through the common ancestor A of the two bodies:
    //  - target = A (child → parent or ancestor): an escape whose orbit around A has its periapsis at
    //    the arrival radius;
    //  - vessel's body = A (parent → child or descendant): a Lambert search inside A's SOI, from the
    //    vessel's orbit to the child's ephemeris;
    //  - otherwise (siblings, different branches): a porkchop of Lambert transfers at A between the two
    //    children of A, then the climb out of the vessel's branch and the descent into the target's.
    // Climbing out of a moon, each level is either straight through or through a low periapsis of that
    // level (Oberth); descending, either straight at the moon or a capture into a low orbit, then a
    // parent → child leg. The cheaper wins and the plan shows each stage.
    //
    // Multi-level plans are flown leg by leg (see PlanStep.LegIndex). The grids use circular, coplanar
    // estimates. Only refined candidates are costed from the actual state: DeparturePlanner for the
    // first burn, then a check flight through the PatchedPropagator in which each later climb level is
    // re-planned from the state actually reached, as the pilot will, and checked (no impact, periapsis
    // above the atmosphere or terrain, excess within 5 % of the required one).
    // Pure code: no game API, safe on a worker thread. Honours the token (throws
    // OperationCanceledException) and reports progress in [0, 1].
    public static partial class TransferSearch
    {
        public const string SameBody = "The target is the body you are orbiting.";
        public const string NoWindow = "No window within the maximum trip duration.";
        public const string NotClosed = "Not in a closed orbit.";
        public static string LowCapture(string body) => $"Capture orbit below the atmosphere or terrain of {body}.";
        public static string LowOrbit(string body) => $"Your orbit passes below the atmosphere or terrain of {body}: raise its periapsis first.";

        public const string ChildToParent = "Child to parent";
        public const string ChildToAncestor = "Child to ancestor";
        public const string ParentToChild = "Parent to child";
        public const string ParentToDescendant = "Parent to descendant";
        public const string Siblings = "Siblings";
        public const string DifferentBranches = "Different branches";

        // Optional diagnostics: each candidate built and why it was rejected (tests, logs). Called on
        // the search's thread.
        public static Action<string> Trace;

        public static TransferPlan Find(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress = null)
        {
            if (s == null)
                throw new ArgumentNullException(nameof(s));
            if (q == null)
                throw new ArgumentNullException(nameof(q));
            if (q.FromBody < 0 || q.FromBody >= s.Count || q.ToBody < 0 || q.ToBody >= s.Count)
                throw new ArgumentException("Unknown body index.");
            ct.ThrowIfCancellationRequested();
            progress?.Report(0);
            var plan = new Search(s, q, ct, progress).Run();
            plan.Request = q;
            progress?.Report(1);
            return plan;
        }

        // The orbit a leg aims at.
        struct Target
        {
            public int Body;
            public double Pe, Ap, Inc;

            public Target(int body, double pe, double ap, double inc)
            {
                Body = body;
                Pe = pe;
                Ap = ap;
                Inc = inc;
            }

            public void ApplyTo(PlanStep step)
            {
                step.TargetBody = Body;
                step.TargetPeriapsisRadius = Pe;
                step.TargetApoapsisRadius = Ap;
                step.TargetInclinationDeg = Inc;
            }
        }

        // How one climb level above the vessel's body is left: with the excess W, straight through or
        // through a low periapsis.
        sealed class LevelPlan
        {
            public int Level;
            public Vector3d W;
            public bool Staged;
            public string Explanation;
        }

        sealed class Leg
        {
            public bool Ok = true;
            public string Refusal;
            public readonly List<PlanStep> Steps = new List<PlanStep>();     // the vessel's own level (leg 0)
            public readonly List<Impulse> Impulses = new List<Impulse>();    // its burns
            public readonly List<LevelPlan> Levels = new List<LevelPlan>();  // levels 1…k, in order
            public double Cost;            // leg 0 from the actual state, the levels above estimated
            public double FirstBurnUT = double.NaN;
            public double ExitUT = double.NaN; // SOI exit of the vessel's body
            public Vector3d TopW;          // excess required at the top level
        }

        // The check flight of a climb, each level above the first re-planned from the state reached.
        sealed class Flight
        {
            public bool Ok = true;
            public string Reason;
            public readonly List<PlanStep> Steps = new List<PlanStep>(); // levels 1…k, flown values
            public double Cost;
            public double TopExitUT;
            public Vector3d AfterR, AfterV; // the state entering the top level's parent (A)
            public double AfterUT;
            public int Unplanned = -1;
            public double UnplannedUT;
        }

        sealed class Arrival
        {
            public readonly List<PlanStep> Steps = new List<PlanStep>();
            public readonly List<string> Notes = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public double Cost;
            public double ArrivalUT;
            public Target FirstTarget;
            public bool DirectDescent;       // straight at the moon from the first descent level
            public PlanStep EntryCorrection; // that leg's correction on entering the intermediate SOI
            public PlanStep FinalCapture;    // the capture at the target, when there is one
        }

        sealed class Built
        {
            public TransferPlan Plan;
            public bool FlightFailed; // the check flight failed: shift the departure or refuse
            public int Unplanned = -1; // a body entered on the departure that the plan does not pass through
            public double UnplannedUT;
            public double Cost => Plan.Ok ? Plan.TotalDeltaV : double.PositiveInfinity;
        }

        sealed partial class Search
        {
            const int SiblingDepartureSamples = 120, SiblingTofSamples = 50;
            const int ChildTofSamples = 16, MaxChildDepartureSamples = 1200, ChildSamplesPerOrbit = 24;
            const int RefinedCandidates = 4;
            const int PhaseSamples = 72;
            const double SafetyMargin = 10_000; // LowOrbitRadius minus this: the top of the atmosphere or terrain
            const int MaxShifts = 12;
            const double ShiftTolerance = 1.03; // a shifted departure may cost 3 % more
            const double PlaneTurnExplainedDeg = 10;
            const double ExcessTolerance = 0.05;
            const int MaxExitIterations = 5;
            const int PhaseShifts = 8; // arrival phases tried over one moon period, direct descents
            const double PhaseShiftMinCorrection = 20; // m/s: below it the moon's phase is already fine
            const double ExitTolerance = 1; // m/s, velocity at the SOI exit against Lambert
            const string Solved = "solved in flight, skipped under 1 m/s";

            readonly BodySystem S;
            readonly TransferRequest Q;
            readonly CancellationToken Ct;
            readonly IProgress<float> Progress;
            readonly int From, To;
            readonly double[] Sma;
            int Ancestor;
            readonly List<int> Up = new List<int>();   // [From, …, child of A]; empty when From = A
            readonly List<int> Down = new List<int>(); // [child of A, …, To]; empty when To = A
            readonly HashSet<int> Planned = new HashSet<int>();
            double ArrRp, ArrRa, ArrInc, MaxTrip;
            OrbitElements VesselEl;
            double VesselPeriod;
            string CaseName = "";
            float _progress;

            public Search(BodySystem s, TransferRequest q, CancellationToken ct, IProgress<float> progress)
            {
                S = s;
                Q = q;
                Ct = ct;
                Progress = progress;
                From = q.FromBody;
                To = q.ToBody;
                Sma = new double[s.Count];
                for (int i = 0; i < s.Count; i++)
                {
                    var n = s[i];
                    if (n.ParentIndex >= 0)
                        Sma[i] = OrbitMath.Elements(n.EpochPosition, n.EpochVelocity, s[n.ParentIndex].Mu).SemiMajorAxis;
                }
            }

            public TransferPlan Run()
            {
                if (From == To)
                    return Refuse(SameBody);
                var target = S[To];
                ArrRp = double.IsNaN(Q.ArrivalPeriapsisRadius) ? target.LowOrbitRadius : Q.ArrivalPeriapsisRadius;
                ArrRa = double.IsNaN(Q.ArrivalApoapsisRadius) ? ArrRp : Q.ArrivalApoapsisRadius;
                if (ArrRa < ArrRp)
                    (ArrRp, ArrRa) = (ArrRa, ArrRp);
                ArrInc = Q.ArrivalInclinationDeg;
                if (!(ArrRp >= Floor(To)))
                    return Refuse(LowCapture(target.Name));
                MaxTrip = double.IsNaN(Q.MaxTripSeconds) ? double.PositiveInfinity : Q.MaxTripSeconds;

                Ancestor = S.CommonAncestor(From, To);
                for (int b = From; b != Ancestor; b = S[b].ParentIndex)
                    Up.Add(b);
                for (int b = To; b != Ancestor; b = S[b].ParentIndex)
                    Down.Add(b);
                Down.Reverse();
                Planned.Add(Ancestor);
                Planned.UnionWith(Up);
                Planned.UnionWith(Down);

                if (To == Ancestor)
                    CaseName = S[From].ParentIndex == To ? ChildToParent : ChildToAncestor;
                else if (From == Ancestor)
                    CaseName = S[To].ParentIndex == From ? ParentToChild : ParentToDescendant;
                else
                    CaseName = S[From].ParentIndex == S[To].ParentIndex ? Siblings : DifferentBranches;

                if (!Hyperbola.IsFinite(Q.R0) || !Hyperbola.IsFinite(Q.V0) || !(Q.R0.magnitude > 0))
                    return Refuse("Invalid vessel state.");
                VesselEl = OrbitMath.Elements(Q.R0, Q.V0, S[From].Mu);
                bool closed = VesselEl.IsClosed && VesselEl.ApoapsisRadius < S[From].Soi;
                if (!closed)
                    return To == Ancestor ? AlreadyEscaping() : Refuse(NotClosed);
                if (VesselEl.PeriapsisRadius < Floor(From))
                    return Refuse(LowOrbit(S[From].Name));
                VesselPeriod = VesselEl.Period;

                if (To == Ancestor)
                    return ToAncestor();
                if (From == Ancestor)
                    return ToDescendant();
                return Porkchop();
            }

            // ---------------------------------------------------------------- shared

            double Floor(int body) => S[body].LowOrbitRadius - SafetyMargin;

            Target FinalTarget() => new Target(To, ArrRp, ArrRa, ArrInc);

            // Three placeholders on the given leg, solved in flight: after the SOI exit, at mid-course,
            // on the approach. They aim at the leg's first arrival body.
            void AddCorrections(List<PlanStep> steps, int body, int leg, Target aim, double exitUT, double departureUT,
                double arrivalUT, double latestUT, List<string> notes)
            {
                double trip = arrivalUT - departureUT;
                double end = Math.Min(arrivalUT, latestUT);
                if (!(trip > 0) || !(end > exitUT))
                    return;
                double c1 = exitUT + 0.01 * trip, c2 = departureUT + 0.5 * trip, c3 = Math.Min(departureUT + 0.9 * trip, end - 0.01 * trip);
                if (!(exitUT < c1 && c1 < c2 && c2 < c3 && c3 < end))
                {
                    double span = end - exitUT;
                    c1 = exitUT + 0.05 * span;
                    c2 = exitUT + 0.5 * span;
                    c3 = exitUT + 0.9 * span;
                }
                string what = Describe(aim);
                string approach = $"Approach correction: sets {what}, the arrival plane included ({Solved})";
                if (notes != null && notes.Count > 0)
                    approach += ". " + string.Join(". ", notes);
                var list = new[]
                {
                    Step(StepKind.Correction, body, c1, 0, $"Correction after leaving {S[From].Name}: aims {what} ({Solved})", leg, aim),
                    Step(StepKind.Correction, body, c2, 0, $"Mid-course correction: aims {what} ({Solved})", leg, aim),
                    Step(StepKind.Correction, body, c3, 0, approach, leg, aim),
                };
                steps.AddRange(list);
            }

            string Describe(Target aim)
            {
                string name = S[aim.Body].Name;
                string text = double.IsNaN(aim.Pe)
                    ? $"the {name} encounter"
                    : $"the {name} periapsis at {Km(aim.Body, aim.Pe)} km";
                if (!double.IsNaN(aim.Inc))
                    text += Inv($" and an inclination of {aim.Inc:F1}°");
                return text;
            }

            void NoteCrossing(Built built, IEnumerable<Patch> patches)
            {
                foreach (var p in patches)
                    if (p.End == PatchEnd.Encounter && !Planned.Contains(p.NextBody))
                    {
                        built.Unplanned = p.NextBody;
                        built.UnplannedUT = p.EndUT;
                        return;
                    }
            }

            // A departure whose check flight failed, or that crosses an unplanned SOI: depart a few orbits
            // later (at most 3 % dearer than a good original). A failed flight with no good shift is
            // refused; a crossing with no clean shift is kept, with a warning.
            Built ResolveDeparture(Built built, Func<int, Built> shifted)
            {
                bool failed = built.FlightFailed;
                if (!failed && (!built.Plan.Ok || built.Unplanned < 0))
                    return built;
                for (int k = 1; k <= MaxShifts; k++)
                {
                    var b = shifted(k);
                    if (b.Plan.Ok && b.Unplanned < 0 && (failed || b.Cost <= ShiftTolerance * built.Cost))
                    {
                        string why = failed
                            ? $"the departure {k} orbit(s) earlier could not be flown ({built.Plan.Refusal})"
                            : Inv($"avoids the {S[built.Unplanned].Name} SOI, +{b.Cost - built.Cost:F0} m/s");
                        var first = b.Plan.Steps.FirstOrDefault(st => st.DeltaVMagnitude > 0);
                        if (first != null)
                            first.Explanation += $". Departure moved {k} orbit(s) later: {why}";
                        return b;
                    }
                }
                if (failed)
                    return built;
                string warning = $"The departure crosses the {S[built.Unplanned].Name} SOI, which the plan does not include: expect a large correction, or plan again later.";
                built.Plan.Warnings.Add(warning);
                var burn = built.Plan.Steps.FirstOrDefault(st => st.DeltaVMagnitude > 0);
                if (burn != null)
                    burn.Explanation += ". Warning: " + warning;
                return built;
            }

            double ShiftUnit() => Up.Count > 1 ? S.OrbitalPeriod(Up[0]) : VesselPeriod;

            // Totals, order, leg flags and the leg targets of steps that have none yet.
            Built Finish(Built built, TransferPlan plan, double departureUT, double arrivalUT, Dictionary<int, Target> legTargets)
            {
                plan.Steps = plan.Steps.OrderBy(st => st.UT).ThenBy(st => st.LegIndex).ToList(); // stable
                foreach (var st in plan.Steps)
                {
                    st.ReplanAtLegStart = st.LegIndex > 0;
                    if (st.TargetBody < 0 && legTargets != null && legTargets.TryGetValue(st.LegIndex, out var t))
                        t.ApplyTo(st);
                }
                double sum = plan.Steps.Sum(st => st.DeltaVMagnitude);
                plan.ReserveDeltaV = Math.Max(10, 0.02 * sum);
                plan.TotalDeltaV = sum + plan.ReserveDeltaV;
                plan.DepartureUT = departureUT;
                plan.ArrivalUT = arrivalUT;
                plan.Ok = true;
                if (plan.TripSeconds > MaxTrip)
                {
                    plan.Ok = false;
                    plan.Refusal = NoWindow;
                }
                built.Plan = plan;
                return built;
            }

            void VesselAt(double ut, out Vector3d r, out Vector3d v)
            {
                if (ut == Q.UT0)
                {
                    r = Q.R0;
                    v = Q.V0;
                    return;
                }
                KeplerPropagator.Propagate(Q.R0, Q.V0, S[From].Mu, ut - Q.UT0, out r, out v);
            }

            static void Coast(ref Vector3d r, ref Vector3d v, double mu, ref double ut, double toUT)
            {
                if (toUT != ut)
                    KeplerPropagator.Propagate(r, v, mu, toUT - ut, out r, out v);
                ut = toUT;
            }

            // Lambert's direction about +z, from the orbit's angular momentum; both when near-polar.
            static bool[] Directions(Vector3d h)
            {
                var hHat = h.normalized;
                return Math.Abs(hHat.z) >= 0.5 ? new[] { hHat.z >= 0 } : new[] { true, false };
            }

            TransferPlan NewPlan() => new TransferPlan { CaseName = CaseName, Request = Q };

            TransferPlan Refuse(string reason) => new TransferPlan { Ok = false, Refusal = reason, CaseName = CaseName, Request = Q };

            Built Failed(string reason) => new Built { Plan = Refuse(reason) };

            Built FlightFailure(string reason) => new Built { Plan = Refuse(reason), FlightFailed = true };

            void Report(float value)
            {
                if (value <= _progress)
                    return;
                _progress = value;
                Progress?.Report(value);
            }

            string Km(int body, double radius) => ((radius - S[body].Radius) / 1000).ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

            static PlanStep Step(StepKind kind, int body, double ut, double dv, string text, int leg) =>
                new PlanStep { Kind = kind, Body = body, UT = ut, DeltaVMagnitude = dv, Explanation = text, LegIndex = leg };

            static PlanStep Step(StepKind kind, int body, double ut, double dv, string text, int leg, Target aim)
            {
                var st = Step(kind, body, ut, dv, text, leg);
                aim.ApplyTo(st);
                return st;
            }

            static PlanStep Burn(StepKind kind, int body, double ut, Vector3d dv, string text, int leg) =>
                new PlanStep { Kind = kind, Body = body, UT = ut, DeltaV = dv, DeltaVMagnitude = dv.magnitude, Explanation = text, LegIndex = leg };
        }

        // ---------------------------------------------------------------- orbit helpers

        static string Inv(FormattableString text) => FormattableString.Invariant(text);

        static double Cube(double x) => x * x * x;

        static double Finite(double value, double fallback) =>
            double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;

        static double AngleDeg(Vector3d a, Vector3d b) =>
            Math.Atan2(Vector3d.Cross(a, b).magnitude, Vector3d.Dot(a, b)) * 180 / Math.PI;

        // Speed at radius r on an orbit of semi-major axis a.
        static double VisViva(double mu, double r, double a) => Math.Sqrt(mu * (2 / r - 1 / a));

        // The burn at radius r, moving at vAtR, that leaves (or, reversed, captures from) an excess vinf,
        // the burn aligned with the motion: the one Oberth formula used by every estimate.
        static double Oberth(double mu, double r, double vAtR, double vinf) => Math.Sqrt(vinf * vinf + 2 * mu / r) - vAtR;

        // False when the trajectory still has its periapsis ahead and it lies below 'floor', or when it
        // is already below it.
        static bool PeriapsisClear(Vector3d r, Vector3d v, double mu, double floor)
        {
            if (r.magnitude < floor)
                return false;
            var el = OrbitMath.Elements(r, v, mu);
            if (!el.IsClosed && Vector3d.Dot(r, v) >= 0)
                return true; // outgoing hyperbola: the periapsis is behind
            return el.PeriapsisRadius >= floor;
        }

        // The cheaper of the two velocities at r whose asymptote is w, among those that stay above the
        // floor. False when none does.
        static bool AsymptoteBurn(Vector3d r, Vector3d v, Vector3d w, double mu, double floor, out Vector3d dv, out Vector3d vAfter)
        {
            dv = Vector3d.zero;
            vAfter = Vector3d.zero;
            if (!Hyperbola.VelocityForAsymptote(r, w, mu, out var vA, out var vB))
                return false;
            double best = double.PositiveInfinity;
            foreach (var cand in new[] { vA, vB })
            {
                if (!PeriapsisClear(r, cand, mu, floor))
                    continue;
                double c = (cand - v).magnitude;
                if (c < best)
                {
                    best = c;
                    dv = cand - v;
                    vAfter = cand;
                }
            }
            return !double.IsInfinity(best);
        }

        // Time from the periapsis rp of a hyperbola of excess vinf to the radius R.
        static double HyperbolicTime(double rp, double vinf, double mu, double R)
        {
            if (!(R > rp))
                return 0;
            double a = mu / (vinf * vinf);
            double e = 1 + rp / a;
            double coshH = (1 + R / a) / e;
            double h = Math.Log(coshH + Math.Sqrt(coshH * coshH - 1));
            return Math.Sqrt(a * a * a / mu) * (e * Math.Sinh(h) - h);
        }

        // Time for a state to climb to radius R (0 when already there, ∞ when it never does).
        static double TimeToRadius(Vector3d r, Vector3d v, double mu, double R)
        {
            double rMag = r.magnitude;
            if (rMag >= R)
                return 0;
            var el = OrbitMath.Elements(r, v, mu);
            double e = el.Eccentricity;
            double sign = Vector3d.Dot(r, v) >= 0 ? 1 : -1;
            if (!el.IsClosed)
            {
                double a = -el.SemiMajorAxis;
                double h0 = sign * Acosh((1 + rMag / a) / e), h1 = Acosh((1 + R / a) / e);
                return Math.Sqrt(a * a * a / mu) * ((e * Math.Sinh(h1) - h1) - (e * Math.Sinh(h0) - h0));
            }
            if (el.ApoapsisRadius < R || e < 1e-9)
                return double.PositiveInfinity;
            double sma = el.SemiMajorAxis;
            double e0 = sign * Math.Acos(Clamp((1 - rMag / sma) / e)), e1 = Math.Acos(Clamp((1 - R / sma) / e));
            double dM = (e1 - e * Math.Sin(e1)) - (e0 - e * Math.Sin(e0));
            if (dM < 0)
                dM += 2 * Math.PI;
            return dM / Math.Sqrt(mu / (sma * sma * sma));
        }

        // Time to the next periapsis: closed orbits through OrbitMath, hyperbolas while incoming.
        static double TimeToPeriapsis(Vector3d r, Vector3d v, double mu)
        {
            var el = OrbitMath.Elements(r, v, mu);
            if (el.IsClosed)
                return el.Eccentricity < 1e-9 ? 0 : OrbitMath.TimeToTrueAnomaly(r, v, mu, 0);
            if (Vector3d.Dot(r, v) >= 0)
                return double.PositiveInfinity;
            double a = -el.SemiMajorAxis, e = el.Eccentricity;
            double h0 = Acosh((1 + r.magnitude / a) / e);
            return Math.Sqrt(a * a * a / mu) * (e * Math.Sinh(h0) - h0);
        }

        static double Acosh(double x) => x <= 1 ? 0 : Math.Log(x + Math.Sqrt(x * x - 1));

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
    }
}
