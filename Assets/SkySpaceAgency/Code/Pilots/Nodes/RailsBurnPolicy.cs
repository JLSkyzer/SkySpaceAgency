using System;
using KSP.Sim;

namespace K2D2.Node
{
    /// <summary>What the burn and the game report, read once per frame of the burn.</summary>
    public struct RailsInputs
    {
        // Game seconds of burn left: remaining Δv / current acceleration.
        public double BurnLeftGameSeconds;
        // Game seconds the active stage can still burn: off rails the stage's
        // OdeSolverStageComponent.MaxPossibleBurnTime (what the game's precomputed burn uses), on
        // rails the precomputed burn's end (GetEndBurnOrbit().EndUT) minus now.
        public double StageBurnLeftSeconds;
        // Real seconds aligned within MaxAlignDeg at a steady full throttle, without a break.
        public double AlignedSteadyRealSeconds;
        // Off rails: predicted largest angle (degrees) between the fixed burn direction (the
        // Δv-to-go now) and the ideal one until the planned exit (PredictAimDrift). On rails: the
        // angle between the direction fixed at entry and the Δv-to-go now. NaN: unknown.
        public double AimDriftDegAtExit;
        // TargetOrbitBurn.allow_rails_warp (false for landing and reentry burns).
        public bool RailsAllowed;
        // In the atmosphere, or the path down to the planned exit below the game's surface
        // envelope (EnvelopeAltitude).
        public bool InAtmosphereOrEnvelope;
        // Within 2 km of a rendezvous target.
        public bool NearTarget;
        // This burn put the warp on rails, and it is still there (index above the physics range,
        // physics mode off).
        public bool CurrentlyOnRails;
        // The game's precomputed burn is running (RailsBurn.GuardOk). Null: not read yet.
        public bool? GuardOk;
        // The game has not switched to its burn yet (IsOrbitalPhysicsUnderThrustActive still
        // false): the only failed guard that may wait, until frame GuardFrames.
        public bool GuardPending;
        // Real frames since TryEnter (the entry frame is 0).
        public int FramesSinceRailsEntry;
        // The game's warp rates (TimeWarp.GetWarpRates, index 0 = 1x).
        public float[] Rates;
        // Real seconds of the last frame (Time.unscaledDeltaTime).
        public double RealFrameSeconds;
        // TimeWarp.CurrentRateIndex.
        public int CurrentIndex;
        // TimeWarp.CurrentRate, the rate really applied this frame (the game may ramp or clamp
        // it). The margins use the higher of it and the table's rate at CurrentIndex.
        public double CurrentRate;
        // A guard already failed for this burn: no rails again until the next burn.
        public bool RefusedBefore;
    }

    public enum RailsAction { Stay, EnterRails, LeaveRails, DropGuard, StepDown }

    /// <summary>The rails decision for this frame.</summary>
    public struct RailsStep
    {
        public RailsAction Action;
        // EnterRails: the rails index. StepDown: LowRailsIndex. LeaveRails, DropGuard: 0. Stay:
        // the current index.
        public int WantedIndex;
        public string Reason;
    }

    /// <summary>
    /// Rails warp for long burns, with the game's own precomputed burn. Pure, tested in the
    /// EditMode tests (RailsBurnPolicyTests).
    ///
    /// In the IL (Redux 26w42c, Ksp2Redux.dll): when the warp goes past the physics range with
    /// physics mode off, VesselBehavior.OnUniverseTimeScaleChanged switches the vessel to Orbital,
    /// and VesselComponent.SetModelPhysicsMode(Orbital) calls HandleOrbitalPhysicsUnderThrustStart
    /// when IsUnderEngineThrust (an engine force with a non-zero RelativeForce). That burn is
    /// solved once: the active stage's first OdeSolverStageComponent (flow, exhaust velocity in a
    /// fixed direction), from now to now + MaxPossibleBurnTime, so it keeps one direction, ignores
    /// steering and throttle, and stops at the stage's burnout. Back to RigidBody ends it
    /// (HandleOrbitalPhysicsUnderThrustEnd). The burn's start and end therefore stay in physics
    /// warp, where the closed loop steers.
    /// </summary>
    public static class RailsBurnPolicy
    {
        // Rails only with more than this much game time of burn left.
        public const double MinBurnLeft = 120;
        // Aligned within this angle (degrees) for MinSteadySeconds of real time.
        public const double MaxAlignDeg = 1;
        public const double MinSteadySeconds = 1;
        // The fixed direction may stray from the ideal one by less than this (degrees).
        public const double MaxAimDriftDeg = 5;
        // Leave rails before the remaining Δv falls under this much burn at the current
        // acceleration, or the stage's burn under StageMargin.
        public const double BurnMargin = 15;
        public const double StageMargin = 10;
        // On top of those game-time margins, this much real time at the current rate: one frame
        // hitch (Unity caps a frame at 0.33 s, 16.5 s of game time at x50) must not run past them.
        public const double RealMargin = 2;
        // Above x10, step down to x10 when less than this much real time is left before the exit
        // at the current rate.
        public const double StepDownRealSeconds = 5;
        // The rails stretch lasts at least this much real time at the chosen rate.
        public const double MinRealSeconds = 15;
        // Rails indices used: x10 and x50 in the game's table.
        public const int LowRailsIndex = 3;
        public const int HighRailsIndex = 4;
        // The guard must hold from this frame after the entry.
        public const int GuardFrames = 2;

        /// <summary>
        /// Rails decision for this frame. Off rails: EnterRails at the highest of x50 and x10
        /// leaving MinRealSeconds of real time before the planned exit, when every condition
        /// holds. On rails: DropGuard when the game's burn is not confirmed by frame GuardFrames
        /// (or lost later), LeaveRails at the exit margins or when a safety input turns, else Stay.
        /// </summary>
        public static RailsStep Decide(RailsInputs i)
        {
            return i.CurrentlyOnRails ? DecideOnRails(i) : DecideEntry(i);
        }

        static RailsStep DecideEntry(RailsInputs i)
        {
            int current = Math.Max(0, i.CurrentIndex);
            if (i.RefusedBefore)
                return Stay(current, "refused earlier in this burn");
            if (!i.RailsAllowed)
                return Stay(current, "rails warp not allowed for this burn");
            if (i.NearTarget)
                return Stay(current, "near the rendezvous target");
            if (i.InAtmosphereOrEnvelope)
                return Stay(current, "in the atmosphere or below the surface envelope");
            if (current > PhysicsWarpPolicy.MaxPhysicsIndex)
                return Stay(current, "not in physics warp or at 1x");
            if (!(i.BurnLeftGameSeconds > MinBurnLeft))
                return Stay(current, $"{i.BurnLeftGameSeconds:n0} s of burn left (rails above {MinBurnLeft:n0} s)");
            if (!(i.AlignedSteadyRealSeconds >= MinSteadySeconds))
                return Stay(current, $"not steady yet ({i.AlignedSteadyRealSeconds:n1} s aligned within {MaxAlignDeg}°)");
            if (!(i.AimDriftDegAtExit < MaxAimDriftDeg))
                return Stay(current, $"aim drift {i.AimDriftDegAtExit:n1}° until the exit (limit {MaxAimDriftDeg}°)");

            double planned = PlannedRailsSeconds(i.BurnLeftGameSeconds, i.StageBurnLeftSeconds);
            for (int index = HighRailsIndex; index >= LowRailsIndex; index--)
            {
                double rate = RateAt(i.Rates, index);
                if (!(rate > RateAt(i.Rates, PhysicsWarpPolicy.MaxPhysicsIndex)))
                    continue;
                double stretch = planned - RealMargin * rate;
                if (stretch / rate >= MinRealSeconds)
                    return new RailsStep
                    {
                        Action = RailsAction.EnterRails,
                        WantedIndex = index,
                        Reason = $"x{rate:0}: {stretch:n0} s to the exit, {stretch / rate:n1} s real",
                    };
            }
            return Stay(current, $"{planned:n0} s to the planned exit: under {MinRealSeconds:n0} s real even at x{RateAt(i.Rates, LowRailsIndex):0}");
        }

        static RailsStep DecideOnRails(RailsInputs i)
        {
            int current = Math.Max(0, i.CurrentIndex);
            // Only a game that has not switched yet may wait (frames 0 and 1). A switch without a
            // burn trajectory or patch, or a guard lost later, drops at once.
            if (i.GuardOk != true)
            {
                if (i.GuardPending && i.FramesSinceRailsEntry < GuardFrames)
                    return Stay(current, "waiting for the guard");
                return new RailsStep { Action = RailsAction.DropGuard, WantedIndex = 0,
                    Reason = i.GuardPending ? "the game did not start its precomputed burn in time"
                        : i.GuardOk == false ? "the game's precomputed burn has no trajectory or patch" : "the guard was not read" };
            }
            if (!i.RailsAllowed)
                return Leave("rails warp no longer allowed");
            if (i.NearTarget)
                return Leave("near the rendezvous target");
            if (i.InAtmosphereOrEnvelope)
                return Leave("in the atmosphere or below the surface envelope");

            // Margins in game time plus RealMargin of real time at the rate applied, checked after
            // this frame's game time has passed.
            double rate = EffectiveRate(i);
            double frame = rate * Math.Max(0, i.RealFrameSeconds);
            double real = RealMargin * rate;
            if (!(i.BurnLeftGameSeconds - frame > BurnMargin + real))
                return Leave($"{i.BurnLeftGameSeconds:n1} s of burn left (margin {BurnMargin:n0} s + {real:n0} s at x{rate:0})");
            if (!(i.StageBurnLeftSeconds - frame > StageMargin + real))
                return Leave($"{i.StageBurnLeftSeconds:n1} s of stage burn left (margin {StageMargin:n0} s + {real:n0} s at x{rate:0})");
            if (i.AimDriftDegAtExit >= MaxAimDriftDeg)
                return Leave($"aim {i.AimDriftDegAtExit:n1}° away from the fixed burn direction");
            if (current > LowRailsIndex)
            {
                double left = PlannedRailsSeconds(i.BurnLeftGameSeconds, i.StageBurnLeftSeconds) - real - frame;
                if (left / rate < StepDownRealSeconds)
                    return new RailsStep { Action = RailsAction.StepDown, WantedIndex = LowRailsIndex,
                        Reason = $"{left:n0} s to the exit at x{rate:0}: {left / rate:n1} s real" };
            }
            return Stay(current, "on rails");
        }

        // The rate for the margins: the higher of the rate applied and the table's (1 if unknown).
        static double EffectiveRate(RailsInputs i)
        {
            double table = RateAt(i.Rates, Math.Max(0, i.CurrentIndex));
            double rate = double.IsNaN(table) ? 1 : table;
            if (i.CurrentRate > rate)
                rate = i.CurrentRate;
            return Math.Max(1, rate);
        }

        /// <summary>
        /// Thrust acceleration (m/s²) between two states dt game seconds apart: the velocity change
        /// minus gravity (mean of both ends). NaN for bad inputs.
        /// </summary>
        public static double MeasuredAccel(Vector3d r0, Vector3d v0, Vector3d r1, Vector3d v1, double dt, double mu)
        {
            if (!(dt > 0))
                return double.NaN;
            Vector3d g = (Accel(r0, mu, Vector3d.zero) + Accel(r1, mu, Vector3d.zero)) * 0.5;
            double a = ((v1 - v0) - g * dt).magnitude / dt;
            return double.IsNaN(a) || double.IsInfinity(a) ? double.NaN : a;
        }

        /// <summary>
        /// Acceleration to time the exit with on rails: the lower of the modelled and the measured
        /// (smoothed) values, the earlier exit. Either one alone when the other is unknown.
        /// </summary>
        public static double ExitAccel(double modelled, double measured)
        {
            bool m = modelled > 0, s = measured > 0;
            if (m && s)
                return Math.Min(modelled, measured);
            return m ? modelled : s ? measured : double.NaN;
        }

        /// <summary>Game seconds from now to the planned rails exit: the earlier of the two margins.</summary>
        public static double PlannedRailsSeconds(double burnLeft, double stageBurnLeft) =>
            Math.Min(burnLeft - BurnMargin, stageBurnLeft - StageMargin);

        /// <summary>
        /// The game's surface envelope altitude (m above the reference radius), as
        /// Redux.Sim.SurfaceApproachWarpLimit.GetEnvelopeAltitude (internal, IL): the atmosphere's
        /// top when there is one, or the highest terrain, whichever is higher, plus 500 m.
        /// </summary>
        public static double EnvelopeAltitude(bool hasAtmosphere, double atmosphereDepth, double maxTerrainHeight) =>
            Math.Max(hasAtmosphere ? atmosphereDepth : 0, maxTerrainHeight) + 500;

        /// <summary>
        /// Acceleration (m/s²) after t seconds of thrust: thrust (N) on a mass (kg) that drops by
        /// massFlow (kg/s). NaN once the mass would be gone or for bad inputs.
        /// </summary>
        public static double AccelAfter(double thrustN, double massKg, double massFlowKgS, double t)
        {
            double m = massKg - Math.Max(0, massFlowKgS) * Math.Max(0, t);
            if (!(thrustN > 0) || !(m > 0))
                return double.NaN;
            return thrustN / m;
        }

        /// <summary>
        /// Largest angle (degrees) between the fixed burn direction (dvToGo now) and the ideal one
        /// (dvToGo along the predicted path) at `checks` points up to `seconds` ahead. The path is
        /// integrated (leapfrog, steps of 2 s at most) under the body's gravity plus a constant
        /// acceleration along the fixed direction. minRadius: the lowest radius along the path. NaN
        /// when the guidance gives no direction.
        /// </summary>
        public static double PredictAimDrift(Func<Vector3d, Vector3d, double, Vector3d> dvToGo, Vector3d r, Vector3d v,
            double ut, double mu, double accel, double seconds, out double minRadius, int checks = 8)
        {
            minRadius = r.magnitude;
            Vector3d d0 = dvToGo(r, v, ut);
            if (!Finite(d0) || !(d0.magnitude > 0) || !(seconds > 0) || checks < 1)
                return double.NaN;
            Vector3d thrust = d0.normalized * Math.Max(0, accel);

            int perCheck = Math.Max(1, (int)Math.Ceiling(seconds / checks / 2.0));
            double h = seconds / checks / perCheck;
            double t = ut;
            double worst = 0;
            for (int c = 0; c < checks; c++)
            {
                for (int s = 0; s < perCheck; s++)
                {
                    v = v + Accel(r, mu, thrust) * (h / 2);
                    r = r + v * h;
                    v = v + Accel(r, mu, thrust) * (h / 2);
                    t += h;
                    minRadius = Math.Min(minRadius, r.magnitude);
                }
                Vector3d d = dvToGo(r, v, t);
                if (!Finite(d) || !(d.magnitude > 0))
                    return double.NaN;
                worst = Math.Max(worst, Vector3d.Angle(d0, d));
            }
            return worst;
        }

        static Vector3d Accel(Vector3d r, double mu, Vector3d thrust)
        {
            double rm = r.magnitude;
            if (!(mu > 0) || !(rm > 0))
                return thrust;
            return thrust - r * (mu / (rm * rm * rm));
        }

        static bool Finite(Vector3d x) =>
            !double.IsNaN(x.x) && !double.IsNaN(x.y) && !double.IsNaN(x.z)
            && !double.IsInfinity(x.x) && !double.IsInfinity(x.y) && !double.IsInfinity(x.z);

        public static double RateAt(float[] rates, int index) =>
            rates != null && index >= 0 && index < rates.Length ? rates[index] : double.NaN;

        static RailsStep Stay(int index, string reason) =>
            new RailsStep { Action = RailsAction.Stay, WantedIndex = index, Reason = reason };

        static RailsStep Leave(string reason) =>
            new RailsStep { Action = RailsAction.LeaveRails, WantedIndex = 0, Reason = reason };
    }
}
