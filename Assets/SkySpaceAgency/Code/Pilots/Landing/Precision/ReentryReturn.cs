using System;
using System.Globalization;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    /// <summary>
    /// The stop predictor of a parachute precision return (DeorbitSearch, PlaneChange and
    /// CourseCorrection take its Fn): ReentryPredictor with the capsule alone. The vessel flies
    /// with its service module only above the atmosphere, where the coast is Kepler whatever the
    /// vehicle, and the capsule's mass does not change with the burns (the propellant is in the
    /// service module), so the impulses carry no Isp. Before the jettison (Jettisoned false) a
    /// trajectory that leaves less than JettisonLead between the last burn and the interface is
    /// refused: the jettison would not fit. After it, the predictor models the capsule alone and
    /// checks nothing more. Counts its calls and the predictions whose chutes never open, so a
    /// refused plan can say why (the trace, not the Reason text). Pure, tested in EditMode.
    /// </summary>
    public class JettisonAwarePredictor
    {
        public const double JettisonLead = 120;   // s between the jettison and the interface

        public readonly Body Body;
        public readonly Atmosphere Air;
        public readonly ReentryVehicle Capsule;
        public bool Jettisoned;

        public int Calls { get; private set; }
        // Predictions that reached the ground with the chutes still packed (trace.SemiUT NaN).
        public int NeverOpen { get; private set; }
        public int Succeeded { get; private set; }

        public JettisonAwarePredictor(Body body, Atmosphere air, ReentryVehicle capsule, bool jettisoned = false)
        {
            Body = body;
            Air = air;
            Capsule = capsule;
            Jettisoned = jettisoned;
        }

        public StopPredictorFn Fn => (r, v, ut, impulse) => Predict(r, v, ut, impulse, out _);

        public StopPrediction Predict(Vector3d r, Vector3d v, double ut, Impulse? impulse, out ReentryTrace trace) =>
            Predict(r, v, ut, impulse, Capsule, out trace);

        /// <summary>The same with another capsule (a drag scale changed for a sensitivity or calibration run).</summary>
        public StopPrediction Predict(Vector3d r, Vector3d v, double ut, Impulse? impulse, ReentryVehicle capsule, out ReentryTrace trace)
        {
            Calls++;
            StopPrediction p = ReentryPredictor.Predict(r, v, ut, Body, Air, capsule, impulse, out trace);
            if (ChutesNeverOpen(p, trace))
                NeverOpen++;
            if (!p.Ok || Jettisoned)
            {
                if (p.Ok)
                    Succeeded++;
                return p;
            }
            double lastBurn = impulse.HasValue ? Math.Max(impulse.Value.UT, ut) : ut;
            if (!(trace.InterfaceUT - JettisonLead >= lastBurn))
                return new StopPrediction
                {
                    Ok = false,
                    Reason = string.Format(CultureInfo.InvariantCulture,
                        "No time to jettison the service module: the interface is {0:F0} s after the burn", trace.InterfaceUT - lastBurn),
                };
            Succeeded++;
            return p;
        }

        /// <summary>The descent reached the ground with the chutes packed: told by the trace
        /// (SemiUT NaN after a touchdown), not by the Reason text.</summary>
        public static bool ChutesNeverOpen(StopPrediction p, ReentryTrace trace) =>
            !p.Ok && !double.IsNaN(trace.TouchdownSpeed) && double.IsNaN(trace.SemiUT) && double.IsNaN(trace.FullUT);

        /// <summary>The jettison time of a prediction: JettisonLead before its interface (NaN without one).</summary>
        public static double JettisonUT(ReentryTrace trace) => trace.InterfaceUT - JettisonLead;
    }

    /// <summary>The expected accuracy shown at the start: how far the stop moves when the drag is
    /// off by the assumed error, from the model itself.</summary>
    public struct ExpectedAccuracy
    {
        public bool Ok;
        public double ErrorFraction;   // assumed drag error (0.20, or 0.10 with a calibrated scale)
        public double ShiftLowM;       // stop shift for DragScale × (1 − ErrorFraction); NaN when refused
        public double ShiftHighM;      // the same for × (1 + ErrorFraction)
        public double ExpectedM;       // max(MinExpectedM, the larger shift); MinExpectedM when neither is known
    }

    public static class ReentryAccuracy
    {
        public const double MinExpectedM = 3000;
        public const double DefaultDragError = 0.20;
        public const double CalibratedDragError = 0.10;
        // A drag scale this far from 1 has been set by the player (calibrated).
        public const double CalibratedTolerance = 1e-3;

        public static bool IsCalibrated(double dragScale) => Math.Abs(dragScale - 1) > CalibratedTolerance;

        public static double DragError(double dragScale) => IsCalibrated(dragScale) ? CalibratedDragError : DefaultDragError;

        /// <summary>
        /// Stop shift (great circle, m on radius) between the stop at dragScale and the stops at
        /// dragScale × (1 ∓ error), error from DragError(dragScale). predictAtScale gives the stop
        /// of the planned trajectory for a scale. Ok false when the nominal prediction is refused.
        /// </summary>
        public static ExpectedAccuracy Estimate(Func<double, StopPrediction> predictAtScale, double dragScale, double radius)
        {
            double error = DragError(dragScale);
            var result = new ExpectedAccuracy
            {
                ErrorFraction = error,
                ShiftLowM = double.NaN,
                ShiftHighM = double.NaN,
                ExpectedM = MinExpectedM,
            };
            StopPrediction nominal = predictAtScale(dragScale);
            if (!nominal.Ok)
                return result;
            result.Ok = true;
            StopPrediction low = predictAtScale(dragScale * (1 - error));
            StopPrediction high = predictAtScale(dragScale * (1 + error));
            if (low.Ok)
                result.ShiftLowM = SurfaceGeometry.GreatCircle(nominal.StopDirNow, low.StopDirNow, radius);
            if (high.Ok)
                result.ShiftHighM = SurfaceGeometry.GreatCircle(nominal.StopDirNow, high.StopDirNow, radius);
            double shift = Math.Max(Value(result.ShiftLowM), Value(result.ShiftHighM));
            result.ExpectedM = Math.Max(MinExpectedM, shift);
            return result;
        }

        static double Value(double x) => double.IsNaN(x) ? 0 : x;

        public const string UncalibratedNote = " (uncalibrated: apply the suggested drag scale after the first return)";

        /// <summary>"Deorbit 85 m/s at T+12m 30s, expected within about 4 km" (time in seconds from now),
        /// with UncalibratedNote while the drag scale is still 1.</summary>
        public static string PlanMessage(double deorbitDeltaV, double secondsFromNow, double expectedM, bool calibrated) =>
            string.Format(CultureInfo.InvariantCulture, "Deorbit {0:F0} m/s at T+{1}, expected within about {2:F0} km{3}",
                deorbitDeltaV, Duration(secondsFromNow), Math.Ceiling(expectedM / 1000), calibrated ? "" : UncalibratedNote);

        static string Duration(double s)
        {
            if (double.IsNaN(s) || s < 0)
                s = 0;
            long total = (long)Math.Round(s);
            long h = total / 3600, m = total / 60 % 60, sec = total % 60;
            return h > 0 ? $"{h}h {m:00}m {sec:00}s" : m > 0 ? $"{m}m {sec:00}s" : $"{sec}s";
        }
    }

    public struct DragCalibrationResult
    {
        public bool Ok;            // a scale was found within the range (or at a limit, AtLimit)
        public string Reason;      // why not Ok
        public double Scale;       // the drag scale whose predicted stop falls on the touchdown point
        public double AlongM;      // the touchdown relative to that stop, along the track (+ ahead)
        public double CrossM;      // across it (+ left): the drag cannot remove it
        public bool AtLimit;       // the touchdown is beyond the range: Scale is the nearest limit
        public int Predictions;
    }

    /// <summary>
    /// After a precision return lands: the drag scale whose predicted stop would have fallen on
    /// the real touchdown point, by bisection on the along-track miss (more drag stops the capsule
    /// earlier, so the touchdown moves ahead of the stop as the scale grows) between MinScale and
    /// MaxScale. A suggestion only: the setting stays manual. Pure, tested in EditMode.
    /// </summary>
    public static class DragCalibration
    {
        public const double MinScale = 0.3, MaxScale = 3;   // the setting's range
        public const double ScalePrecision = 0.002;
        public const double AlongPrecision = 20;            // m
        public const int MaxIterations = 40;

        /// <param name="predictAtScale">The stop from the logged state (jettison or interface) for a drag scale.</param>
        /// <param name="touchdownDir">The real touchdown point in the body orientation of that state's UT.</param>
        public static DragCalibrationResult Solve(Func<double, StopPrediction> predictAtScale, Vector3d touchdownDir, double radius,
            double minScale = MinScale, double maxScale = MaxScale)
        {
            var result = new DragCalibrationResult { Scale = double.NaN, AlongM = double.NaN, CrossM = double.NaN };
            Vector3d target = touchdownDir / touchdownDir.magnitude;
            int calls = 0;

            bool Along(double scale, out double along, out double cross)
            {
                calls++;
                along = cross = double.NaN;
                StopPrediction p = predictAtScale(scale);
                if (!p.Ok || !(p.TrackDirNow.sqrMagnitude > 0))
                    return false;
                SurfaceGeometry.Split(p.StopDirNow, target, p.TrackDirNow, radius, out along, out cross);
                return true;
            }

            double lo = minScale, hi = maxScale;
            if (!Along(lo, out double fLo, out double cLo) || !Along(hi, out double fHi, out double cHi))
            {
                result.Ok = false;
                result.Reason = "No prediction at the ends of the drag scale range";
                result.Predictions = calls;
                return result;
            }
            if (fLo >= 0 == fHi >= 0)
            {
                // Not bracketed: the nearest limit.
                bool lowBest = Math.Abs(fLo) <= Math.Abs(fHi);
                result.Ok = true;
                result.AtLimit = true;
                result.Scale = lowBest ? lo : hi;
                result.AlongM = lowBest ? fLo : fHi;
                result.CrossM = lowBest ? cLo : cHi;
                result.Predictions = calls;
                return result;
            }

            double best = Math.Abs(fLo) < Math.Abs(fHi) ? lo : hi;
            double bestAlong = Math.Abs(fLo) < Math.Abs(fHi) ? fLo : fHi, bestCross = Math.Abs(fLo) < Math.Abs(fHi) ? cLo : cHi;
            for (int i = 0; i < MaxIterations && hi - lo > ScalePrecision && Math.Abs(bestAlong) > AlongPrecision; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (!Along(mid, out double f, out double c))
                {
                    result.Ok = false;
                    result.Reason = string.Format(CultureInfo.InvariantCulture, "No prediction at drag scale {0:0.###}", mid);
                    result.Predictions = calls;
                    return result;
                }
                if (Math.Abs(f) < Math.Abs(bestAlong))
                {
                    best = mid;
                    bestAlong = f;
                    bestCross = c;
                }
                if (f >= 0 == fLo >= 0)
                {
                    lo = mid;
                    fLo = f;
                }
                else
                    hi = mid;
            }
            result.Ok = true;
            result.Scale = best;
            result.AlongM = bestAlong;
            result.CrossM = bestCross;
            result.Predictions = calls;
            return result;
        }

        public static string SuggestionMessage(double suggested, double current) =>
            string.Format(CultureInfo.InvariantCulture, "Suggested reentry drag scale: {0:0.00} (current {1:0.00})", suggested, current);
    }

    // The stages of a parachute precision return, in flight order.
    public enum ReentryStage { PlaneChange, Deorbit, MidCourse, Jettison }

    public enum ReentryAction
    {
        Stop,                   // stop the pilot with the reason (the orbit still clears the atmosphere)
        Skip,                   // log, warn, go on (a refused correction: the uncorrected course is kept)
        FallBackParachute,      // plain ParachuteDescent without target (with the service module if not jettisoned)
        StopCapsuleLost,        // the active vessel is not the capsule: stop, the player lands it
    }

    /// <summary>
    /// What a failure of a parachute precision return does. Before the deorbit has lowered the
    /// periapsis into the atmosphere, the pilot stops as part 2 does; after it, a failure never
    /// stops it: it lands under parachutes without target, except when the active vessel is not
    /// the capsule (ParachuteDescent would fly the wrong vessel). Pure, tested in EditMode.
    /// </summary>
    public static class ReentryFailurePolicy
    {
        /// <param name="entersAtmosphere">The orbit's periapsis is inside the atmosphere (or a collision is predicted).</param>
        /// <param name="stageActivated">Jettison only: the stage was activated (the split may have happened).</param>
        /// <param name="activeHoldsCapsule">Jettison only: the active vessel holds every capsule part.</param>
        /// <param name="capsuleLost">Jettison only: Jettison reported capsule_lost.</param>
        public static ReentryAction Decide(ReentryStage stage, bool entersAtmosphere, bool stageActivated = false,
            bool activeHoldsCapsule = true, bool capsuleLost = false)
        {
            switch (stage)
            {
                case ReentryStage.MidCourse:
                    return ReentryAction.Skip;
                case ReentryStage.Jettison:
                    if (capsuleLost || (stageActivated && !activeHoldsCapsule))
                        return ReentryAction.StopCapsuleLost;
                    return ReentryAction.FallBackParachute;
                default:
                    return entersAtmosphere ? ReentryAction.FallBackParachute : ReentryAction.Stop;
            }
        }

        /// <summary>The periapsis is under the top of the atmosphere, or the game predicts a collision.</summary>
        public static bool EntersAtmosphere(double periapsisRadius, double bodyRadius, double atmosphereDepth, bool collisionPredicted) =>
            collisionPredicted || periapsisRadius < bodyRadius + atmosphereDepth;

        public static string FallBackMessage(string reason) =>
            string.Format(CultureInfo.InvariantCulture, "Precision return aborted: {0}. Landing under parachutes without target.", Trim(reason));

        public static string ServiceModuleKeptMessage(string reason) =>
            string.Format(CultureInfo.InvariantCulture,
                "Precision return aborted: {0}. The service module is still attached: landing under parachutes without target.", Trim(reason));

        public const string CapsuleLostMessage =
            "Precision return aborted: the capsule is not the active vessel. Switch to it and land manually.";

        public static string SkipMessage(string reason) => "Course correction skipped: " + Trim(reason) + ".";

        static string Trim(string reason) => string.IsNullOrEmpty(reason) ? "unknown reason" : reason.Trim().TrimEnd('.');
    }

    /// <summary>The start checks of a parachute precision return that need no game call.</summary>
    public static class ReentryStart
    {
        public const double OrbitMargin = 5000;   // m above the atmosphere for the periapsis
        public const string NoEngineMessage = "No engine to deorbit with.";
        public const string ChutesNeverOpenMessage = "Parachutes would not open in the model (speed or pressure): precision unavailable.";
        public const double DeltaVMargin = 1.10;

        /// <summary>Null when the orbit is closed with its periapsis at least OrbitMargin above the atmosphere; else why not.</summary>
        public static string CheckOrbit(double eccentricity, double periapsisRadius, double bodyRadius, double atmosphereDepth)
        {
            if (!(eccentricity < 1))
                return PrecisionStart.NotClosedMessage;
            double floor = atmosphereDepth + OrbitMargin;
            if (!(periapsisRadius - bodyRadius >= floor))
                return string.Format(CultureInfo.InvariantCulture,
                    "Periapsis {0:F1} km: a precision return starts from an orbit with its periapsis above {1:F0} km (the atmosphere + {2:F0} km).",
                    (periapsisRadius - bodyRadius) / 1000, floor / 1000, OrbitMargin / 1000);
            return null;
        }

        /// <summary>Null when the vessel's Δv covers needed × DeltaVMargin; else the refusal. NaN remaining: unknown, not refused.</summary>
        public static string CheckDeltaV(double needed, double remaining)
        {
            if (double.IsNaN(remaining) || double.IsInfinity(remaining))
                return null;
            if (remaining >= needed * DeltaVMargin)
                return null;
            return string.Format(CultureInfo.InvariantCulture,
                "Not enough Δv for the precision return: {0:F0} m/s needed with the margin (plane change, deorbit and correction), {1:F0} m/s left.",
                needed * DeltaVMargin, remaining);
        }
    }
}
