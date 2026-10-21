using System;
using K2D2.Landing.Braking;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    public struct Miss
    {
        public bool Ok;
        public string Reason;      // why there is no prediction; null when Ok
        public double DistanceM;   // great circle, predicted stop to target
        public double AlongM;      // target relative to the stop, along the ground track (+ ahead)
        public double CrossM;      // across it (+ left)
        public StopPrediction Stop;
    }

    // The flight decisions of a precision landing between the planner calls (LandingPilot), kept
    // pure so they are tested in EditMode.
    public static class PrecisionPhases
    {
        // Smallest correction worth flying (m/s), and TargetOrbitBurn's fixed end threshold
        // (its private DoneDeltaV): a smaller burn ends before it starts.
        public const double MinCorrection = 0.05;
        public const double BurnEndDeltaV = 0.2;
        // Seconds between planning a burn and its impulse: TargetOrbitBurn.AlignLead plus a margin.
        public const double PlanLead = 30;
        // Seconds between two predicted-distance readouts outside the burns.
        public const double ReadoutInterval = 2;
        // Hand-over to terminal guidance below this share of the hand-over height while still
        // braking; a hand-over below LateShare is late (possible drift at touchdown).
        public const double ForcedHandOverShare = 0.5;
        public const double LateShare = 0.8;
        // Floor of the speed (m/s) at which the brake counts as stopped.
        public const double MinStopSpeed = 2;
        public const double DeltaVMargin = 1.10;

        /// <summary>
        /// Predicted stop of the coast from (r, v, ut) without a burn, and its miss to the target.
        /// One StopPointPredictor call (cheap enough for a readout every ReadoutInterval).
        /// </summary>
        public static Miss Predict(Vector3d r, Vector3d v, double ut, Body body, Vehicle vehicle, Vector3d targetDirNow)
        {
            StopPrediction stop = StopPointPredictor.Predict(r, v, ut, body, vehicle, null);
            var miss = new Miss { Ok = stop.Ok, Reason = stop.Ok ? null : stop.Reason, Stop = stop };
            if (!stop.Ok)
                return miss;
            Vector3d target = targetDirNow / targetDirNow.magnitude;
            miss.DistanceM = SurfaceGeometry.GreatCircle(stop.StopDirNow, target, body.Radius);
            var none = new Impulse { UT = ut, DeltaV = new Vector3d(0, 0, 0) };
            if (DeorbitSearch.GroundTrack(r, v, ut, body, none, stop, out Vector3d track))
                SurfaceGeometry.Split(stop.StopDirNow, target, track, body.Radius, out miss.AlongM, out miss.CrossM);
            else
                miss.AlongM = miss.CrossM = double.NaN;
            return miss;
        }

        /// <summary>The correction is flown only when Ok and at least MinCorrection.</summary>
        public static bool ShouldFly(Correction c) => c.Ok && c.DeltaV.magnitude >= MinCorrection;

        /// <summary>
        /// When to plan the mid-course correction: PlanLead before the halfway point between now
        /// (the end of the deorbit burn) and the predicted brake start; now when that is past or
        /// the brake start is unknown.
        /// </summary>
        public static double CorrectionPlanUT(double now, double brakeStartUT)
        {
            if (double.IsNaN(brakeStartUT) || brakeStartUT <= now)
                return now;
            return Math.Max(now, 0.5 * (now + brakeStartUT) - PlanLead);
        }

        /// <summary>
        /// Earliest deorbit burn time: after the plane change settles, and late enough that a burn
        /// of durationGuess seconds centred on it starts at least PlanLead from now.
        /// </summary>
        public static double EarliestDeorbitUT(double now, double afterPlaneChangeUT, double durationGuess)
        {
            double half = double.IsNaN(durationGuess) ? 0 : 0.5 * Math.Max(0, durationGuess);
            double earliest = now + PlanLead + half;
            return double.IsNaN(afterPlaneChangeUT) ? earliest : Math.Max(earliest, afterPlaneChangeUT);
        }

        /// <summary>
        /// The precision brake's throttle: DescentEnvelope.ThrustFraction, as BrakeSimulator plans
        /// it, so the predicted stop stays valid; full throttle when the brake simulated from the
        /// current state says it would stop below SafeAltitude or not at all.
        /// </summary>
        public static float BrakeThrottle(BrakeResult? sim)
        {
            if (sim.HasValue)
            {
                BrakeResult b = sim.Value;
                if (b.Status == BrakeStatus.Impossible
                    || (b.Status == BrakeStatus.TooLate && b.StopAltitude < BrakeSimulator.SafeAltitude))
                    return 1f;
            }
            return (float)DescentEnvelope.ThrustFraction;
        }

        /// <summary>
        /// Brake → terminal guidance: the brake has stopped (BrakeSimulator's stop test, floored at
        /// MinStopSpeed), or the vessel is already below ForcedHandOverShare of the hand-over height.
        /// </summary>
        public static bool BrakeHandOver(double surfaceSpeed, double accel, double altitude, double handOverHeight)
        {
            double stopSpeed = Math.Max(MinStopSpeed, Math.Max(BrakeSimulator.StopSpeed,
                DescentEnvelope.ThrustFraction * Math.Max(0, accel) * BrakeSimulator.Step));
            return surfaceSpeed <= stopSpeed || altitude < ForcedHandOverShare * handOverHeight;
        }

        public static bool IsLateHandOver(double altitude, double handOverHeight) => altitude < LateShare * handOverHeight;

        /// <summary>
        /// The remaining Δv cannot cover the terminal descent plus the lateral reach to the target
        /// (with DeltaVMargin). Unknown Δv (≤ 0 or NaN) is never short.
        /// </summary>
        public static bool DeltaVShort(double deltaVRemaining, double terminalNeed)
        {
            if (!(deltaVRemaining > 0) || double.IsNaN(terminalNeed))
                return false;
            return DeltaVMargin * terminalNeed > deltaVRemaining;
        }

        /// <summary>
        /// The guidance offset when the target is given up: the drift-stop point (horizontal
        /// position + velocity × TerminalGuidance.DriftNullTime) becomes the origin, so the
        /// guidance only stops the drift and lands. The height is kept.
        /// </summary>
        public static Vector3d GiveUpOffset(Vector3d offset, Vector3d velocity) =>
            new Vector3d(-velocity.x * TerminalGuidance.DriftNullTime, -velocity.y * TerminalGuidance.DriftNullTime, offset.z);

        /// <summary>Distance (m) from the target to the drift-stop point, for GiveUpOffset's message.</summary>
        public static double DriftStopDistance(Vector3d offset, Vector3d velocity)
        {
            double x = offset.x + velocity.x * TerminalGuidance.DriftNullTime;
            double y = offset.y + velocity.y * TerminalGuidance.DriftNullTime;
            return Math.Sqrt(x * x + y * y);
        }

        public static string OutOfReachMessage(double distanceM) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Target out of reach: landing as close as possible ({0:0} m away).", distanceM);

        public static string LandedMessage(double distanceM) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "Landed {0:0} m from the target.", distanceM);

        public static string SlopeMessage(double slopeDeg) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "Target on a {0:0}° slope.", slopeDeg);
    }

    /// <summary>
    /// Shows the "target out of reach" message only after HoldSeconds of consecutive drops:
    /// TerminalGuidance.TargetDropped is per call and can be transient. Never latched: one call
    /// that keeps the target resets it.
    /// </summary>
    public class DropDebounce
    {
        public const double HoldSeconds = 2;
        double since = double.NaN;

        public bool Update(bool dropped, double ut)
        {
            if (!dropped)
            {
                since = double.NaN;
                return false;
            }
            if (double.IsNaN(since))
                since = ut;
            return ut - since >= HoldSeconds;
        }

        public void Reset() => since = double.NaN;
    }
}
