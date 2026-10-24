using System;
using System.Collections.Generic;
using System.Globalization;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;

namespace K2D2.Lift
{
    // What a Lift run is for. Orbit is today's ascent, unchanged. PrepareTransfer waits on the pad for
    // the parking-orbit plane of a transfer, lifts into it and trims it (sub-project 4.2). Rendezvous
    // stays hidden until 4.4.
    public enum LiftGoal { Orbit, PrepareTransfer, Rendezvous }

    // What the pad wait does now (LiftGoalRules.Decide).
    public enum PadAction { Warp, Hold, Launch, Resolve, LiftedOff }

    // How the launch goes at T-0 (LiftGoalRules.LaunchStagingFor): fire the first stage, lift off
    // on the throttle alone (engines already lit), or refuse.
    public enum LaunchStaging { Stage, ThrottleOnly, Refuse }

    // The transfer goal's plan, filled by WaitLaunch and read by the ascent, the trim and the tab. All
    // vectors are in the launch body's inertial Zup frame of the snapshot the plan was made from.
    public class LiftTransferPlan
    {
        public string TargetName;
        public string BodyName;
        public double BodyRadius;
        public double Mu;
        public double OrbitRadius;          // the parking orbit (circular)
        public Vector3d Omega;              // the body's angular velocity

        public ParkingPlan Parking;         // null while planning
        public LaunchWindowResult Window;   // null while planning

        public double LeadSeconds;          // the ascent lead used by the window
        public double DownrangeRad;
        public bool LeadMeasured;           // false: the estimate

        public double PlannedLaunchUT = double.NaN;  // the first window, for the log
        public int Resolves;                         // passes missed and solved again
        public bool Staged;                          // the mod fired the first stage this run (confirmed)
        public bool EarlyLiftoff;                    // lifted off before the launch time: no lead measured

        public double LiftoffUT = double.NaN;
        public Vector3d LiftoffPosition;

        public bool Ready => Parking != null && Parking.Ok && Window != null && Window.Ok;
        public double OrbitalSpeed => OrbitRadius > 0 && Mu > 0 ? Math.Sqrt(Mu / OrbitRadius) : 0;
    }

    // The pure part of the transfer goal: the pad decisions, the parking altitude, the ascent lead,
    // the trim node and the texts. Tested in EditMode (LiftGoalTests).
    public static class LiftGoalRules
    {
        // The warp stops this long before the launch time, and the clock then runs at 1×.
        public const double HoldSeconds = 30;
        // Still on the pad this long after the launch time: the pass is missed, solve again.
        public const double MissedSeconds = 5;
        // The plane is "right" within this angle; above it, a trim burn.
        public const double TrimThresholdDeg = 0.1;
        // The trim node must be this far ahead for TargetOrbitBurn's longest alignment lead.
        public const double TrimMinLead = K2D2.Node.BurnWarp.MaxLead + 10;

        // ESTIMATE (not measured): until a first transfer ascent has measured its own, the time from
        // liftoff to the end of the Lift ascent burn and the ground distance covered meanwhile, for a
        // Kerbin-like ascent to ~100 km.
        public const double EstimatedLeadSeconds = 150;
        public const double EstimatedDownrangeDeg = 5;

        // The stored ascent lead stays in this range (a value outside is clamped), and a new
        // measurement counts for this share against the previous one.
        public const double MinLeadSeconds = 60, MaxLeadSeconds = 600;
        public const double MinDownrangeDeg = 1, MaxDownrangeDeg = 30;
        public const double LeadSmoothing = 0.5;

        public const string StagedRefusal =
            "The vessel was staged before the launch time and no engine is lit: the mod does not fire another stage on the pad. Revert to the launch, or light the engines yourself.";

        const double Deg = Math.PI / 180;

        // The first stage is fired only on a vessel nothing was staged on: the stage
        // ActivateNextStage would fire is the first one (the last stage, StageCount-1, not active:
        // JettisonPlan.NextStage) and no engine is lit. Otherwise the player has staged already:
        // lift off on the throttle if engines are lit, else refuse (the next stage could be the
        // boosters' decouplers).
        public static LaunchStaging LaunchStagingFor(bool stagedByMod, int stageCount, bool lastStageActive, int activeEngines)
        {
            if (!stagedByMod && stageCount > 0 && !lastStageActive && activeEngines == 0)
                return LaunchStaging.Stage;
            if (activeEngines > 0)
                return LaunchStaging.ThrottleOnly;
            return LaunchStaging.Refuse;
        }

        // ActivateNextStage either fires at once or does nothing (paused, warp on rails): a stage
        // fired shows on the next frame as a new stage count or a newly active last stage.
        public static bool StageFired(int countBefore, bool lastActiveBefore, int countNow, bool lastActiveNow) =>
            countNow != countBefore || lastActiveNow != lastActiveBefore;

        // The lead and downrange to store after an ascent: the measurement clamped to the plausible
        // range, averaged with the previous stored value when there is one. False when the
        // measurement is unusable (non-finite or not positive): keep the previous value.
        public static bool UpdatedLead(double previousSeconds, double previousDeg, double measuredSeconds, double measuredDeg,
            out double seconds, out double deg)
        {
            seconds = previousSeconds;
            deg = previousDeg;
            if (!(measuredSeconds > 0) || double.IsInfinity(measuredSeconds) || double.IsNaN(measuredDeg) || double.IsInfinity(measuredDeg))
                return false;
            double s = Math.Max(MinLeadSeconds, Math.Min(MaxLeadSeconds, measuredSeconds));
            double d = Math.Max(MinDownrangeDeg, Math.Min(MaxDownrangeDeg, measuredDeg));
            bool hasPrevious = previousSeconds >= MinLeadSeconds && previousSeconds <= MaxLeadSeconds
                               && previousDeg >= MinDownrangeDeg && previousDeg <= MaxDownrangeDeg;
            seconds = hasPrevious ? LeadSmoothing * previousSeconds + (1 - LeadSmoothing) * s : s;
            deg = hasPrevious ? LeadSmoothing * previousDeg + (1 - LeadSmoothing) * d : d;
            return true;
        }

        public static double WarpEndUT(double launchUT) => launchUT - HoldSeconds;

        // launchUT is the liftoff (LaunchWindowResult.LaunchUT, the ascent lead already in it).
        public static PadAction Decide(double now, double launchUT, bool landed)
        {
            if (!landed)
                return PadAction.LiftedOff;
            if (double.IsNaN(launchUT) || double.IsNaN(now) || now > launchUT + MissedSeconds)
                return PadAction.Resolve;
            if (now >= launchUT)
                return PadAction.Launch;
            if (now < WarpEndUT(launchUT))
                return PadAction.Warp;
            return PadAction.Hold;
        }

        // The Ap the ascent aims for, in whole km: the body's low orbit (rounded up) or the Ap
        // setting if that is higher.
        public static int ParkingApKm(double lowOrbitRadius, double bodyRadius, int apSettingKm)
        {
            double lowKm = (lowOrbitRadius - bodyRadius) / 1000;
            int low = double.IsNaN(lowKm) || double.IsInfinity(lowKm) ? 0 : (int)Math.Ceiling(lowKm - 1e-9);
            return Math.Max(low, apSettingKm);
        }

        // The measured lead and downrange when they are usable, else the estimate.
        public static void AscentLead(double measuredLeadSeconds, double measuredDownrangeDeg,
            out double leadSeconds, out double downrangeRad, out bool measured)
        {
            measured = measuredLeadSeconds >= MinLeadSeconds && measuredLeadSeconds <= MaxLeadSeconds
                       && measuredDownrangeDeg >= MinDownrangeDeg && measuredDownrangeDeg <= MaxDownrangeDeg;
            leadSeconds = measured ? measuredLeadSeconds : EstimatedLeadSeconds;
            downrangeRad = (measured ? measuredDownrangeDeg : EstimatedDownrangeDeg) * Deg;
        }

        // The angle over the ground from the liftoff site to the insertion point, dt later: the site
        // turned with the body (ω about z) against the insertion position, both body-relative.
        public static double DownrangeRad(Vector3d liftoff, Vector3d insertion, Vector3d omega, double dt)
        {
            double a = omega.z * dt;
            double c = Math.Cos(a), s = Math.Sin(a);
            var site = new Vector3d(c * liftoff.x - s * liftoff.y, s * liftoff.x + c * liftoff.y, liftoff.z);
            return Math.Atan2(Vector3d.Cross(site, insertion).magnitude, Vector3d.Dot(site, insertion));
        }

        public static bool TrimNeeded(double planeErrorDeg) => planeErrorDeg > TrimThresholdDeg;

        public static double PlaneChangeDeltaV(double speed, double errorDeg) => 2 * speed * Math.Sin(errorDeg * Deg / 2);

        // Seconds to the next crossing of the target plane at least minLead ahead (the other one when
        // the nearer is too close), with its direction; NaN when the planes are the same.
        public static double TimeToTrimNode(Vector3d r, Vector3d v, double mu, Vector3d targetNormal, double minLead,
            out Vector3d nodeDir)
        {
            nodeDir = Vector3d.zero;
            Vector3d h = Vector3d.Cross(r, v);
            Vector3d line = Vector3d.Cross(h.normalized, targetNormal.normalized);
            if (!(line.magnitude > 1e-9))
                return double.NaN;
            line = line.normalized;
            double tPlus = OrbitMath.TimeToDirection(r, v, mu, line);
            double tMinus = OrbitMath.TimeToDirection(r, v, mu, -1.0 * line);
            bool plus = tPlus >= minLead && (tMinus < minLead || tPlus <= tMinus);
            if (!plus && tMinus < minLead)
                plus = tPlus >= tMinus; // both too close (a very short period): the later one
            nodeDir = plus ? line : -1.0 * line;
            return plus ? tPlus : tMinus;
        }

        // The run's last words, and whether the plane is the one promised.
        public static bool EndMessage(string bodyName, double planeErrorDeg, out string text)
        {
            if (!TrimNeeded(planeErrorDeg) && !double.IsNaN(planeErrorDeg))
            {
                text = $"Ready for the {bodyName} transfer: open the Transfer tab";
                return true;
            }
            text = Inv($"In the parking orbit, but its plane is still {planeErrorDeg:F2}° off the {bodyName} transfer's (more than {TrimThresholdDeg:0.0}°): the Transfer tab will plan from this orbit");
            return false;
        }

        // The tab's rows for the plan, at UT now.
        public static List<(string, string)> Readout(LiftTransferPlan p, double now)
        {
            var rows = new List<(string, string)>();
            if (p == null)
                return rows;
            if (p.Parking == null || p.Window == null)
            {
                rows.Add(("Transfer plan", $"planning for {p.TargetName}..."));
                return rows;
            }
            if (!p.Parking.Ok || !p.Window.Ok)
            {
                rows.Add(("Transfer plan", p.Parking.Ok ? p.Window.Refusal : p.Parking.Refusal));
                return rows;
            }
            double toLaunch = p.Window.LaunchUT - now;
            rows.Add(("Launch in", toLaunch >= 0 ? "T-" + KerbinCalendar.Duration(toLaunch) : "T+" + KerbinCalendar.Duration(-toLaunch)));
            rows.Add(("Azimuth", Inv($"{p.Window.AzimuthDeg:F1}°")));
            double inc = Math.Acos(Math.Max(-1, Math.Min(1, p.Parking.PlaneNormal.normalized.z))) / Deg;
            rows.Add(("Plane inclination", Inv($"{inc:F2}°")));
            rows.Add(("Plane change left", Inv($"{p.Window.PlaneErrorDeg:F2}° ({p.Window.PlaneChangeDeltaV:F0} m/s)")));
            rows.Add(("Transfer window", Inv($"{KerbinCalendar.Date(p.Parking.WindowUT)} ({p.Parking.TransferDeltaV:F0} m/s)")));
            return rows;
        }

        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
