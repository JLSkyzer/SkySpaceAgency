using System;
using KSP.Sim;

namespace SkySpaceAgency.Node
{
    /// <summary>What the burn and the game report, read once per frame of the burn.</summary>
    public struct RailsInputs
    {
        // Game seconds of burn left: remaining Δv / current acceleration.
        public double BurnLeftGameSeconds;
        // Game seconds the active stage can still burn: off rails the stage's
        // OdeSolverStageComponent.MaxPossibleBurnTime, on rails the game's burn end (the lit
        // engines' propellant, GetEndBurnOrbit().EndUT in 26w46a; the entry estimate in 26w47a, whose burn
        // plan rolls, see StageEndUT) minus now.
        public double StageBurnLeftSeconds;
        // Real seconds aligned within MaxAlignDeg at a steady full throttle, without a break.
        public double AlignedSteadyRealSeconds;
        // Off rails: predicted largest angle (degrees) between the fixed burn direction (the
        // Δv-to-go now) and the ideal one until the planned exit (PredictAimDrift). On rails: the
        // angle between the direction fixed at entry and the Δv-to-go now. NaN: unknown. The
        // vessel does not turn on rails, so the game's burn keeps the entry direction.
        public double AimDriftDegAtExit;
        // The burn's mode is Rails, with TargetOrbitBurn.allow_warp and allow_rails_warp (false for
        // landing and reentry burns).
        public bool RailsAllowed;
        // In the atmosphere, or the path down to the planned exit below the game's surface
        // envelope (EnvelopeAltitude).
        public bool InAtmosphereOrEnvelope;
        // Within 2 km of a rendezvous target.
        public bool NearTarget;
        // This burn put the warp on rails, and it is still there (index above the physics range,
        // physics mode off).
        public bool CurrentlyOnRails;
        // The game flies the burn (RailsBurn.GuardOk: IsOrbitalPhysicsUnderThrustActive). Null:
        // not read yet.
        public bool? GuardOk;
        // The game has not taken the burn yet (IsOrbitalPhysicsUnderThrustActive still false):
        // the only failed guard that may wait, until frame GuardFrames.
        public bool GuardPending;
        // Real frames since TryEnter (the entry frame is 0).
        public int FramesSinceRailsEntry;
        // Game seconds since TryEnter.
        public double RailsGameSeconds;
        // Thrust acceleration (m/s²) measured on the trajectory the game flies (smoothed), and the
        // one modelled from the entry thrust and mass flow. NaN: unknown.
        public double MeasuredAccel;
        public double ModelledAccel;
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
        // EnterRails: the rails index. StepDown: one index lower. LeaveRails, DropGuard: 0. Stay:
        // the current index.
        public int WantedIndex;
        public string Reason;
    }

    /// <summary>
    /// The game's native rails warp during a burn, as a native maneuver node uses it: ignition and
    /// the end of the burn at 1x in closed loop, the middle on rails. Pure, tested in the EditMode
    /// tests (RailsBurnPolicyTests).
    ///
    /// In the IL (Redux 26w46a, Ksp2Redux.dll, read on 2026-11-15): entering rails with physics
    /// mode off switches the vessel to Orbital, and VesselComponent.SetModelPhysicsMode(Orbital)
    /// calls HandleOrbitalPhysicsUnderThrustStart when an engine pushes or is commanded to. It
    /// builds the burn from the lit engines (TryBuildTrackedEngineBurn: their thrust, direction
    /// and mass flow, until their propellant runs out), sets IsOrbitalPhysicsUnderThrustActive only
    /// on success, rebuilds it each frame the thrust (direction included) or the mass flow changes
    /// (OnUpdateOrbitUnderThrust), and ends it when no engine pushes or asks to, or when its
    /// trajectory starts away from the conic. The vessel does not turn on rails: the burn keeps the
    /// direction it had at entry. Back to RigidBody ends it (HandleOrbitalPhysicsUnderThrustEnd).
    /// In 26w42c it was solved once from the stage's data, kept to the stage's burnout whatever the
    /// engines did, and reported active even when it built nothing: hence the old 120 s threshold
    /// and the three-part guard, both gone.
    /// </summary>
    public static class RailsBurnPolicy
    {
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
        public const double MinRealSeconds = 8;
        // Rails indices used: x10 to x100 in the game's table (the game lowers the rate itself
        // near the end of its burn, TimeWarp.ClampRateToOrbitTransitions).
        public const int LowRailsIndex = 3;
        public const int HighRailsIndex = 5;
        // The guard must hold from this frame after the entry.
        public const int GuardFrames = 3;
        // After this much game time and PushCheckFrames frames on rails (at x100 a frame is 2 s of
        // game time, and the game's first updates still run on the conic), a measured acceleration
        // under MinPushShare of the modelled one means the game's burn does not push: back to 1x.
        public const double PushCheckSeconds = 3;
        public const int PushCheckFrames = 10;
        public const double MinPushShare = 0.5;

        /// <summary>
        /// Rails decision for this frame. Off rails: EnterRails at the highest rate from x100 down
        /// to x10 leaving MinRealSeconds of real time before the planned exit, when every condition
        /// holds. On rails: DropGuard when the game's burn is not confirmed by frame GuardFrames
        /// (or lost later) or does not push, LeaveRails at the exit margins or when a safety input
        /// turns, StepDown one index at a time near the exit, else Stay.
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
            if (!(i.BurnLeftGameSeconds > 0))
                return Stay(current, "no burn time left");
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
            return Stay(current, $"{planned:n0} s to the planned exit: under {MinRealSeconds:n0} s real even at x{RateAt(i.Rates, LowRailsIndex):0}, stays at 1x");
        }

        static RailsStep DecideOnRails(RailsInputs i)
        {
            int current = Math.Max(0, i.CurrentIndex);
            // Only a game that has not switched yet may wait (frames 0 and 1). A switch without a
            // burn trajectory or patch, or a guard lost later, drops at once.
            if (i.GuardOk != true)
            {
                if (i.GuardPending && i.FramesSinceRailsEntry < GuardFrames)
                    return Stay(current, "waiting for the game to take the burn");
                return new RailsStep { Action = RailsAction.DropGuard, WantedIndex = 0,
                    Reason = i.GuardPending ? "the game is not flying the burn (orbital physics under thrust inactive)"
                        : "the guard was not read" };
            }
            if (i.RailsGameSeconds >= PushCheckSeconds && i.FramesSinceRailsEntry >= PushCheckFrames && i.ModelledAccel > 0 && i.MeasuredAccel >= 0
                && i.MeasuredAccel < MinPushShare * i.ModelledAccel)
                return new RailsStep { Action = RailsAction.DropGuard, WantedIndex = 0,
                    Reason = $"the game's burn does not push: {i.MeasuredAccel:n3} m/s² measured on its trajectory for " +
                        $"{i.ModelledAccel:n3} modelled after {i.RailsGameSeconds:n1} s" };
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
                    return new RailsStep { Action = RailsAction.StepDown, WantedIndex = Math.Max(LowRailsIndex, current - 1),
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

        /// <summary>
        /// UT of the end of the stage's propellant while on rails: the game's burn end when it reports one, else
        /// the entry UT plus the stage's burn seconds taken at entry (26w47a's rolling burn plan has no such end).
        /// </summary>
        public static double StageEndUT(double gameBurnEndUT, double entryUT, double stageSecondsAtEntry) =>
            double.IsNaN(gameBurnEndUT) ? entryUT + stageSecondsAtEntry : gameBurnEndUT;

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
