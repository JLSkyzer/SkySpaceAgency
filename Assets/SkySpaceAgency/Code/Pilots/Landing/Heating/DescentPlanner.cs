using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using K2D2.OrbitPlanning;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Heating
{
    public struct DescentInput
    {
        public Vector3d R, V;
        public double UT;
        public Body Body;
        public Vehicle Vehicle;          // StopHeight 0 = BrakeSimulator.SafeAltitude (the normal landing)
        public bool Atmospheric;
        public Atmosphere Air;
        public DescentVehicle Aero;      // drag and heated parts (atmospheric only)
        public double EarliestBurnUT;    // earliest deorbit impulse
        public double HeatLimit;         // <= 0 = DescentPlanner.HeatLimit
        /// <summary>Atmospheric only: the deorbit periapsis radius to use (a re-plan keeps the start's
        /// choice); NaN or <= 0 = choose among DescentPlanner.EntryFractions.</summary>
        public double FixedPeriapsisRadius;
    }

    public struct DescentPlan
    {
        public bool Ok;
        public string Reason;
        public bool NeedsDeorbit;
        public Impulse Deorbit;
        public double DeorbitDeltaV;
        public double DeorbitPeriapsisRadius;    // NaN without deorbit
        public bool NeedsPreBrake;
        public Impulse PreBrake;
        public double PreBrakeDeltaV;
        public double BrakeDeltaV;
        public double BrakeStartUT;
        public double TotalDeltaV;
        // Atmospheric: the predicted descent after every planned burn, and whether heating was modelled.
        public bool HeatModelled;
        public HeatPrediction Heat;
        public double HeatPeak;                  // the predicted peak up to the brake start (what the limit is checked on)
        public string HeatPeakPart;
        public string HeatWarning;               // hot, but nothing can be planned about it (already in the air)
        public double InterfaceUT;
        public AeroBrakeResult AeroBrake;
        public StopPrediction Stop;              // airless brake (staged details)
        public string Candidates;                // atmospheric entry choice, for the log
        public int Predictions;

        public string Breakdown => string.Format(CultureInfo.InvariantCulture, "deorbit {0:0}, pre-brake {1:0}, brake {2:0}",
            DeorbitDeltaV, PreBrakeDeltaV, BrakeDeltaV);
    }

    // The normal (non-precision) landing's plan from wherever the vessel is: the deorbit, the
    // pre-brake that keeps the reentry heating under the limit, the brake, and their Δv. Airless: a
    // retrograde impulse lowering the periapsis to R − DeorbitSearch.DescentPeriapsisMargin (the
    // precision landing's own target: below the datum, so the conic crosses the terrain, and shallow,
    // the cheapest powered descent), then StopPointPredictor. Atmospheric: an entry periapsis among
    // EntryFractions of the atmosphere depth, chosen for the lowest total Δv whose heating stays under
    // HeatLimit, a retrograde pre-brake ending PreBrakeMargin before the interface when needed, and
    // AeroBrake on the descent predicted with drag. Pure maths, tested in EditMode.
    public static class DescentPlanner
    {
        // Planned peak of Temperature / MaxTemp. Under the live guard's 75 % on purpose: planning up to
        // the 90 % ceiling would make the guard fire on every nominal entry, spending Δv the start check
        // did not count.
        public const double HeatLimit = 0.70;
        public const double PreBrakeMargin = 15;       // s between the pre-brake's end and the interface
        public const double PreBrakeMaxFraction = 0.95; // of the air speed at the interface
        public const int PreBrakeIterations = 16;
        public const double PreBrakePrecision = 2;     // m/s
        public static readonly double[] EntryFractions = { 0.6, 0.5, 0.4, 0.3, 0.2, 0.1, 0 };

        public static DescentPlan Plan(DescentInput input)
        {
            var plan = new DescentPlan
            {
                DeorbitPeriapsisRadius = double.NaN, BrakeStartUT = double.NaN, InterfaceUT = double.NaN,
            };
            double energy = 0.5 * input.V.sqrMagnitude - input.Body.Mu / input.R.magnitude;
            if (!(energy < 0))
                return Refuse(plan, "Escape trajectory: lower the orbit first.");
            return input.Atmospheric ? PlanAtmospheric(input, plan) : PlanAirless(input, plan);
        }

        static DescentPlan PlanAirless(DescentInput input, DescentPlan plan)
        {
            Body body = input.Body;
            double rp = body.Radius - DeorbitSearch.DescentPeriapsisMargin;
            Impulse? deorbit = null;
            if (DeorbitSearch.Periapsis(input.R, input.V, body.Mu) > rp)
            {
                Impulse burn = DeorbitSearch.Burn(input.R, input.V, input.UT, body, Math.Max(input.UT, input.EarliestBurnUT), rp);
                if (burn.DeltaV.magnitude > 0)
                    deorbit = burn;
            }
            plan.Predictions++;
            StopPrediction stop = StopPointPredictor.Predict(input.R, input.V, input.UT, body, input.Vehicle, deorbit);
            plan.Stop = stop;
            if (deorbit.HasValue)
            {
                plan.NeedsDeorbit = true;
                plan.Deorbit = deorbit.Value;
                plan.DeorbitDeltaV = deorbit.Value.DeltaV.magnitude;
                plan.DeorbitPeriapsisRadius = rp;
            }
            if (!stop.Ok)
                return Refuse(plan, stop.Reason ?? LandingFeasibility.CannotStopMessage);
            plan.BrakeDeltaV = stop.BrakeDeltaV;
            plan.BrakeStartUT = stop.BrakeStartUT;
            plan.TotalDeltaV = plan.DeorbitDeltaV + plan.BrakeDeltaV;
            plan.Ok = true;
            return plan;
        }

        struct Candidate
        {
            public DescentPlan Plan;
            public string Line;
        }

        static DescentPlan PlanAtmospheric(DescentInput input, DescentPlan plan)
        {
            Body body = input.Body;
            Atmosphere air = input.Air;
            double interfaceR = body.Radius + air.Depth;
            bool inAir = input.R.magnitude <= interfaceR;
            bool enters = DeorbitSearch.Periapsis(input.R, input.V, body.Mu) < interfaceR;
            var radii = new List<double>();
            if (inAir || enters)
                radii.Add(double.NaN);   // no deorbit
            else if (input.FixedPeriapsisRadius > 0)
                radii.Add(input.FixedPeriapsisRadius);
            else
                foreach (double f in EntryFractions)
                    radii.Add(body.Radius + f * air.Depth);

            var lines = new StringBuilder();
            Candidate? best = null;
            // The deepest entry's reason when none works: shallow ones fail by skipping out first.
            string lastReason = null;
            int predictions = 0;
            foreach (double rp in radii)
            {
                Candidate c = Evaluate(input, rp, ref predictions);
                if (lines.Length > 0)
                    lines.Append(" | ");
                lines.Append(c.Line);
                if (!c.Plan.Ok)
                {
                    lastReason = c.Plan.Reason;
                    continue;
                }
                if (!best.HasValue || c.Plan.TotalDeltaV < best.Value.Plan.TotalDeltaV)
                    best = c;
            }
            DescentPlan chosen = best.HasValue ? best.Value.Plan : plan;
            chosen.Candidates = lines.ToString();
            chosen.Predictions = predictions;
            if (!best.HasValue)
                return Refuse(chosen, lastReason ?? "No entry works.");
            return chosen;
        }

        // One entry: the deorbit to rp (NaN = none), the pre-brake, the brake.
        static Candidate Evaluate(DescentInput input, double rp, ref int predictions)
        {
            var plan = new DescentPlan { DeorbitPeriapsisRadius = rp, BrakeStartUT = double.NaN, InterfaceUT = double.NaN };
            Body body = input.Body;
            Vector3d r = input.R, v = input.V;
            double t = input.UT;
            Vehicle vehicle = input.Vehicle;
            DescentVehicle aero = input.Aero;
            string label = double.IsNaN(rp) ? "no deorbit" : FormattableString.Invariant($"pe {(rp - body.Radius) / 1000:0.0} km");

            if (!double.IsNaN(rp))
            {
                Impulse burn = DeorbitSearch.Burn(r, v, t, body, Math.Max(t, input.EarliestBurnUT), rp);
                plan.NeedsDeorbit = burn.DeltaV.magnitude > 0;
                plan.Deorbit = burn;
                plan.DeorbitDeltaV = burn.DeltaV.magnitude;
                if (burn.UT > t)
                    KeplerPropagator.Propagate(r, v, body.Mu, burn.UT - t, out r, out v);
                v = v + burn.DeltaV;
                t = Math.Max(t, burn.UT);
                vehicle = vehicle.AfterImpulse(plan.DeorbitDeltaV);
                aero.MassT = Lighter(aero.MassT, plan.DeorbitDeltaV, input.Vehicle.Isp);
            }
            Body bodyT = AeroBrake.BodyAt(body, input.UT, t);
            double limit = input.HeatLimit > 0 ? input.HeatLimit : HeatLimit;
            plan.HeatModelled = aero.Parts != null && aero.Parts.Length > 0;

            PreBrakeResult pre = FindPreBrake(r, v, t, bodyT, input.Air, aero, vehicle, limit, ref predictions);
            plan.Heat = pre.Heat;
            plan.HeatPeak = pre.Peak;
            plan.HeatPeakPart = pre.PeakPart;
            plan.InterfaceUT = pre.Heat.InterfaceUT;
            plan.AeroBrake = pre.Brake;
            if (!pre.Ok)
                return Done(plan, label, pre.Reason);
            if (pre.DeltaV > 0)
            {
                plan.NeedsPreBrake = true;
                plan.PreBrake = pre.Impulse;
                plan.PreBrakeDeltaV = pre.DeltaV;
            }
            if (pre.HotInAir)
                plan.HeatWarning = string.Format(CultureInfo.InvariantCulture,
                    "Predicted heating {0:0} % of the max temperature on {1}: already in the air, the heat guard will slow down.",
                    pre.Peak * 100, pre.PeakPart);
            plan.BrakeDeltaV = pre.Brake.DeltaV;
            plan.BrakeStartUT = pre.Brake.StartUT;
            plan.TotalDeltaV = plan.DeorbitDeltaV + plan.PreBrakeDeltaV + plan.BrakeDeltaV;
            plan.Ok = true;
            return Done(plan, label, null);
        }

        static Candidate Done(DescentPlan plan, string label, string reason)
        {
            if (reason != null)
            {
                plan.Ok = false;
                plan.Reason = reason;
            }
            string heat = plan.HeatModelled && plan.Heat.PartPeaks != null
                ? string.Format(CultureInfo.InvariantCulture, ", peak {0:0} % ({1}) before the brake, {2:0} % unbraked",
                    plan.HeatPeak * 100, plan.HeatPeakPart ?? "-", plan.Heat.PeakRatio * 100)
                : ", heating not modelled";
            string line = plan.Ok
                ? string.Format(CultureInfo.InvariantCulture, "{0}: {1}, total {2:0} m/s{3}", label, plan.Breakdown, plan.TotalDeltaV, heat)
                : $"{label}: refused ({reason})";
            return new Candidate { Plan = plan, Line = line };
        }

        static double Lighter(double massT, double deltaV, double isp) =>
            isp > 0 ? massT * Math.Exp(-Math.Abs(deltaV) / (isp * BrakeSimulator.G0)) : massT;

        struct PreBrakeResult
        {
            public bool Ok;
            public string Reason;
            public double DeltaV;
            public Impulse Impulse;
            public HeatPrediction Heat;
            public AeroBrakeResult Brake;
            public double Peak;          // up to the brake start (HeatingPredictor.PeakBefore)
            public string PeakPart;
            public bool HotInAir;
        }

        // One descent judged: the predicted heating, the brake on it, and the peak up to that brake.
        struct Trial
        {
            public HeatPrediction Heat;
            public AeroBrakeResult Brake;
            public double Peak;
            public string PeakPart;
            public bool Ok => Heat.Ok && Brake.Ok;
        }

        static Trial Judge(Vector3d r, Vector3d v, double t, Body body, Atmosphere air, DescentVehicle aero, Vehicle vehicle,
            Impulse? impulse, ref int predictions)
        {
            var trial = new Trial();
            predictions++;
            trial.Heat = HeatingPredictor.Predict(r, v, t, body, air, aero, impulse, vehicle.Isp);
            if (!trial.Heat.Ok)
                return trial;
            Vehicle after = impulse.HasValue ? vehicle.AfterImpulse(impulse.Value.DeltaV.magnitude) : vehicle;
            trial.Brake = AeroBrake.Find(trial.Heat.Samples, body, t, after);
            trial.Peak = HeatingPredictor.PeakBefore(trial.Heat, trial.Brake.Ok ? trial.Brake.StartUT : double.NaN, out trial.PeakPart);
            return trial;
        }

        // The smallest retrograde impulse (against the air velocity) ending PreBrakeMargin before the
        // interface that keeps the predicted peak, up to the planned brake, under the limit. Bisection:
        // a harder pre-brake enters slower (assumed monotone; the result is the verified side).
        static PreBrakeResult FindPreBrake(Vector3d r, Vector3d v, double t, Body body, Atmosphere air, DescentVehicle aero,
            Vehicle vehicle, double limit, ref int predictions)
        {
            Trial trial0 = Judge(r, v, t, body, air, aero, vehicle, null, ref predictions);
            var result = new PreBrakeResult { Heat = trial0.Heat, Brake = trial0.Brake, Peak = trial0.Peak, PeakPart = trial0.PeakPart };
            if (!trial0.Heat.Ok)
            {
                result.Reason = trial0.Heat.Reason;
                return result;
            }
            bool modelled = aero.Parts != null && aero.Parts.Length > 0;
            bool hot = modelled && trial0.Peak > limit;
            if (!hot || trial0.Heat.StartsInAir)
            {
                result.HotInAir = hot;
                result.Ok = trial0.Brake.Ok;
                if (!result.Ok)
                    result.Reason = LandingFeasibility.CannotStopMessage + " (" + trial0.Brake.Reason + ")";
                return result;
            }

            HeatPrediction heat0 = trial0.Heat;
            KeplerPropagator.Propagate(r, v, body.Mu, heat0.InterfaceUT - t, out Vector3d rI, out Vector3d vI);
            double interfaceAirSpeed = (vI - Vector3d.Cross(body.Omega, rI)).magnitude;
            double interfaceUT = heat0.InterfaceUT;

            Impulse Make(double dv)
            {
                double d = BurnTiming.Duration(dv, vehicle.ThrustKN, vehicle.MassT, vehicle.Isp);
                double tb = interfaceUT - (double.IsNaN(d) ? 0 : d / 2) - PreBrakeMargin;
                tb = Math.Max(tb, t);
                Vector3d rb = r, vb = v;
                if (tb > t)
                    KeplerPropagator.Propagate(r, v, body.Mu, tb - t, out rb, out vb);
                Vector3d vAir = vb - Vector3d.Cross(body.Omega, rb);
                double m = vAir.magnitude;
                Vector3d dir = m > 0 ? vAir / m : vb / vb.magnitude;
                return new Impulse { UT = tb, DeltaV = dir * (-dv) };
            }

            double hi = PreBrakeMaxFraction * interfaceAirSpeed;
            Trial hiTrial = Judge(r, v, t, body, air, aero, vehicle, Make(hi), ref predictions);
            if (!hiTrial.Ok || hiTrial.Peak > limit)
            {
                Trial shown = hiTrial.Heat.Ok ? hiTrial : trial0;
                result.Heat = shown.Heat;
                result.Peak = shown.Peak;
                result.PeakPart = shown.PeakPart;
                result.Reason = string.Format(CultureInfo.InvariantCulture,
                    "Reentry too hot: even braking {0:0} m/s before the atmosphere, {1} reaches {2:0} % of its max temperature (limit {3:0} %).",
                    hi, shown.PeakPart, shown.Peak * 100, limit * 100);
                return result;
            }
            Trial best = hiTrial;
            double lo = 0;
            for (int i = 0; i < PreBrakeIterations && hi - lo > PreBrakePrecision; i++)
            {
                double mid = 0.5 * (lo + hi);
                Trial trial = Judge(r, v, t, body, air, aero, vehicle, Make(mid), ref predictions);
                if (trial.Ok && trial.Peak <= limit)
                {
                    hi = mid;
                    best = trial;
                }
                else
                    lo = mid;
            }
            result.Ok = true;
            result.DeltaV = hi;
            result.Impulse = Make(hi);
            result.Heat = best.Heat;
            result.Brake = best.Brake;
            result.Peak = best.Peak;
            result.PeakPart = best.PeakPart;
            return result;
        }

        static DescentPlan Refuse(DescentPlan plan, string reason)
        {
            plan.Ok = false;
            plan.Reason = reason;
            return plan;
        }
    }

    // The start check of a planned descent: the engine and TWR checks of LandingFeasibility, then the
    // whole landing's Δv with LandingFeasibility.DeltaVMargin, with its breakdown. A staged vessel
    // (airless) checks per stage instead, as the precision landing does: the active stage pays the
    // deorbit with 10 % to spare, the landing stage its part of the brake with StagedBrakePlan.DeltaVMargin.
    public static class DescentFeasibility
    {
        public static FeasibilityResult Check(DescentPlan plan, double accel, double surfaceGravity, double deltaVRemaining, Vehicle vehicle)
        {
            FeasibilityResult basic = LandingFeasibility.Check(accel, surfaceGravity, null, deltaVRemaining);
            if (!basic.Ok)
                return basic;
            if (!plan.Ok)
                return new FeasibilityResult { Ok = false, Error = plan.Reason };

            if (vehicle.Staged)
            {
                BrakeStage[] stages = vehicle.Stages;
                if (LandingFeasibility.DeltaVMargin * (plan.DeorbitDeltaV + plan.PreBrakeDeltaV) > stages[0].DeltaV)
                    return Refuse(string.Format(CultureInfo.InvariantCulture,
                        "Stage 1 has {0:0} m/s for the {1:0} m/s (+10 %) of the deorbit: choose another landing stage.",
                        stages[0].DeltaV, plan.DeorbitDeltaV + plan.PreBrakeDeltaV));
                BrakeStage landing = stages[stages.Length - 1];
                double need = plan.Stop.LandingStageDeltaV;
                if (StagedBrakePlan.DeltaVMargin * need > landing.DeltaV)
                {
                    List<BrakeDrop> drops = plan.Stop.Drops;
                    double speed = drops != null && drops.Count > 0 ? drops[drops.Count - 1].SurfaceSpeed : double.NaN;
                    return Refuse(StagedBrakePlan.CannotBrakeMessage(stages.Length, speed, landing.DeltaV));
                }
                return new FeasibilityResult { Ok = true, Warning = plan.HeatWarning };
            }

            if (!(deltaVRemaining > 0))
                return new FeasibilityResult { Ok = true, Warning = LandingFeasibility.DeltaVUnknownMessage };
            double needed = LandingFeasibility.DeltaVMargin * plan.TotalDeltaV;
            if (needed > deltaVRemaining)
                return Refuse(RefusalMessage(deltaVRemaining, plan));
            return new FeasibilityResult { Ok = true, Warning = plan.HeatWarning };
        }

        public static string RefusalMessage(double deltaVRemaining, DescentPlan plan) => string.Format(CultureInfo.InvariantCulture,
            "Δv {0:0} m/s for ~{1:0} m/s needed ({2}, +10 %).", deltaVRemaining, LandingFeasibility.DeltaVMargin * plan.TotalDeltaV, plan.Breakdown);

        static FeasibilityResult Refuse(string error) => new FeasibilityResult { Ok = false, Error = error };
    }
}
