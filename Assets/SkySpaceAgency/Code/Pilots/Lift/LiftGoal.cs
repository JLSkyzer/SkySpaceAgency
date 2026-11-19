using System;
using System.Collections.Generic;
using System.Globalization;
using SkySpaceAgency.OrbitPlanning;
using SkySpaceAgency.Transfer;
using KSP.Sim;
using KTools;

namespace SkySpaceAgency.Lift
{
    // What a Lift run is for. Orbit is today's ascent, unchanged. PrepareTransfer waits on the pad for
    // the parking-orbit plane of a transfer, lifts into it and trims it (sub-project 4.2). Rendezvous
    // waits for the pass that leaves the cheapest rendezvous, lifts under the target and hands over to
    // the Rendezvous pilot (sub-project 4.4).
    public enum LiftGoal { Orbit, PrepareTransfer, Rendezvous }

    // What the pad wait does now (LiftGoalRules.Decide).
    public enum PadAction { Warp, Hold, Launch, Resolve, LiftedOff }

    // How the launch goes at T-0 (LiftGoalRules.LaunchStagingFor): fire the first stage, lift off
    // on the throttle alone (engines already lit), or refuse.
    public enum LaunchStaging { Stage, ThrottleOnly, Refuse }

    // The plan of a goal with a pad wait (PrepareTransfer or Rendezvous), filled by WaitLaunch and read
    // by the ascent, the trim, the hand-over and the tab. All vectors are in the launch body's inertial
    // Zup frame of the snapshot the plan was made from.
    public class LiftTransferPlan
    {
        public LiftGoal Goal = LiftGoal.PrepareTransfer;
        public string TargetName;

        // Rendezvous goal only.
        public RendezvousLaunchPlan Rendezvous;   // null while planning
        public string TargetId;                   // the target vessel's GUID
        public bool Dock;
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

        public bool Ready => Window != null && Window.Ok
                             && (Goal == LiftGoal.Rendezvous ? Rendezvous != null && Rendezvous.Ok : Parking != null && Parking.Ok);

        // The plane the ascent steers to: the target vessel's, or the transfer's parking plane.
        public Vector3d PlaneNormal => Goal == LiftGoal.Rendezvous ? Rendezvous.TargetPlaneNormal : Parking.PlaneNormal;
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
        public const double TrimMinLead = SkySpaceAgency.Node.BurnWarp.MaxLead + 10;

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

        // The throttle stays at 0 while the vessel waits on the pad: WarpGate allows rails warp only
        // after 1 s and 10 frames of throttle 0 and no engine force, and a vessel often sits on the pad
        // at full throttle (2026-11-04: "throttle 1, 0 force(s)", held at 1x for the whole wait).
        // The launch sets the throttle itself at T-0.
        public static bool HoldsThrottleAtZero(PadAction action) => action == PadAction.Warp || action == PadAction.Hold;

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

        // ---------------------------------------------------------------- ascent profile

        // Gravity Turn (K2-D2 1.4.0) flies Orbit launches only for now (user decision 2026-11-19):
        // the transfer and rendezvous goals need the launch-window azimuth and plane steering, which
        // only the Guided ascent has, so they fly Guided whatever is selected.
        public static AscentProfile ProfileFor(LiftGoal goal, AscentProfile selected) =>
            goal == LiftGoal.Orbit ? selected : AscentProfile.Guided;

        public const string GravityTurnOrbitOnly = "Gravity Turn flies Orbit launches only for now; using Guided";

        // ---------------------------------------------------------------- rendezvous goal (4.4)

        // Only the Orbit goal may leave the circularization to the player ("Circularize Once Ascent
        // Complete" off, K2-D2 1.4.0): the transfer and rendezvous goals need the parking orbit.
        public static bool CircularizationForced(LiftGoal goal) => goal != LiftGoal.Orbit;

        // The run ends after Adjust, apoapsis set, without circularizing.
        public static bool EndsAfterAdjust(LiftGoal goal, bool autoCircularize) =>
            !autoCircularize && !CircularizationForced(goal);

        // The status after a finished one. Orbit and PrepareTransfer keep the order they always had
        // (PlaneTrim, the last, back to Off); the rendezvous goal hands over after the circularization;
        // the Orbit goal without auto-circularize stops after Adjust.
        public static LiftPilot.LiftStatus NextStatus(LiftPilot.LiftStatus finished, LiftGoal goal, bool autoCircularize = true)
        {
            if (finished == LiftPilot.LiftStatus.Adjust && EndsAfterAdjust(goal, autoCircularize))
                return LiftPilot.LiftStatus.Off;
            if (goal == LiftGoal.Rendezvous && finished == LiftPilot.LiftStatus.Circularize)
                return LiftPilot.LiftStatus.Handoff;
            if (finished == LiftPilot.LiftStatus.PlaneTrim || finished == LiftPilot.LiftStatus.Handoff)
                return LiftPilot.LiftStatus.Off;
            return finished.Next();
        }

        public enum ResolveMode { NextPass, ReplanAll }

        // A missed launch time: the transfer solves the next window again; a rendezvous plans every
        // pass again, since the target's phase has moved on.
        public static ResolveMode OnMissedLaunch(LiftGoal goal) =>
            goal == LiftGoal.Rendezvous ? ResolveMode.ReplanAll : ResolveMode.NextPass;

        public static string StepName(LiftPilot.LiftStatus step)
        {
            switch (step)
            {
                case LiftPilot.LiftStatus.WaitLaunch: return "the pad wait";
                case LiftPilot.LiftStatus.Ascent: return "the ascent";
                case LiftPilot.LiftStatus.Coasting: return "the coast to the apoapsis";
                case LiftPilot.LiftStatus.Adjust: return "the apoapsis adjustment";
                case LiftPilot.LiftStatus.Circularize: return "the circularization";
                case LiftPilot.LiftStatus.Handoff: return "the hand-over";
                default: return step.ToString();
            }
        }

        // What a stop in the chain says: the step, the reason and what is left to do.
        // targetLost: the target is gone, landed or elsewhere: the advice is to choose one.
        public static string ChainStopped(LiftPilot.LiftStatus step, string target, string why, bool targetLost = false)
        {
            string left;
            if (targetLost)
                left = "choose a target in the Rendezvous tab, then start the rendezvous there";
            else switch (step)
            {
                case LiftPilot.LiftStatus.WaitLaunch:
                    left = "press Start again on the pad: the launch is planned again";
                    break;
                case LiftPilot.LiftStatus.Ascent:
                case LiftPilot.LiftStatus.Coasting:
                case LiftPilot.LiftStatus.Adjust:
                    left = "reach orbit by hand, then start the rendezvous from the Rendezvous tab (the target is set)";
                    break;
                case LiftPilot.LiftStatus.Circularize:
                    left = "circularize, then start the rendezvous from the Rendezvous tab (the target is set)";
                    break;
                default:
                    left = "start the rendezvous from the Rendezvous tab (the target is set)";
                    break;
            }
            string reason = (why ?? "").Trim().TrimEnd('.');
            return $"Rendezvous with {target} stopped during {StepName(step)}: {reason}. Left to do: {left}";
        }

        // Why a run was stopped from outside: null is the Stop button (no reset in progress).
        public static string StopReason(ResetCause? cause)
        {
            switch (cause)
            {
                case null: return "Stop pressed";
                case ResetCause.AnotherPilot: return "another pilot started";
                case ResetCause.SceneChange: return "the scene changed (flight left)";
                case ResetCause.VesselChange: return "the active vessel changed";
                case ResetCause.ModDisabled: return "the mod is off in this campaign";
                default: return cause.ToString();
            }
        }

        // A refusal of the rendezvous planner on the pad: the same answer on every Start, so no
        // "press Start again".
        public static string PlanRefused(string target, string why)
        {
            string reason = (why ?? "").Trim().TrimEnd('.');
            return $"Rendezvous with {target} refused on the pad: {reason}. Nothing was launched.";
        }

        // The target at the hand-over: null when it can still be met, else why not.
        public static string HandoffRefusal(bool exists, bool landed, bool sameBody)
        {
            if (!exists)
                return "the target vessel no longer exists (recovered, destroyed, or docked elsewhere)";
            if (landed)
                return "the target is landed";
            if (!sameBody)
                return "the target no longer orbits the launch body";
            return null;
        }

        // The run's last words at the hand-over: started, or the Rendezvous pilot's refusal.
        public static bool HandoffMessage(bool started, string target, bool dock, string rendezvousStatus, out string text)
        {
            if (started)
            {
                text = $"In the parking orbit: the Rendezvous pilot takes over ({(dock ? "rendezvous + docking" : "rendezvous only")} with {target}), see the Rendezvous tab";
                return true;
            }
            text = ChainStopped(LiftPilot.LiftStatus.Handoff, target, rendezvousStatus);
            return false;
        }

        static List<(string, string)> RendezvousReadout(LiftTransferPlan p, double now)
        {
            var rows = new List<(string, string)>();
            var rv = p.Rendezvous;
            if (rv == null)
            {
                rows.Add(("Rendezvous plan", $"planning with {p.TargetName}..."));
                return rows;
            }
            if (!rv.Ok || p.Window == null || !p.Window.Ok)
            {
                rows.Add(("Rendezvous plan", !rv.Ok ? rv.Refusal : p.Window?.Refusal ?? ""));
                return rows;
            }
            var w = p.Window;
            double toLaunch = w.LaunchUT - now;
            rows.Add(("Launch in", toLaunch >= 0 ? "T-" + KerbinCalendar.Duration(toLaunch) : "T+" + KerbinCalendar.Duration(-toLaunch)));
            rows.Add(("Azimuth", Inv($"{w.AzimuthDeg:F1}°")));
            double inc = Math.Acos(Math.Max(-1, Math.Min(1, rv.TargetPlaneNormal.normalized.z))) / Deg;
            rows.Add(("Target plane", Inv($"{inc:F2}°")));
            rows.Add(("Plane change left", Inv($"{w.PlaneErrorDeg:F2}° ({w.PlaneChangeDeltaV:F0} m/s)")));
            rows.Add(("Parking orbit", Inv($"{(rv.ParkingRadius - p.BodyRadius) / 1000:F0} km ({rv.ParkingReason})")));
            if (rv.Rendezvous != null)
                rows.Add(("Rendezvous", Inv($"{rv.Rendezvous.TotalDeltaV:F0} m/s, arrival {KerbinCalendar.Date(rv.Rendezvous.ArrivalUT)}")));
            if (rv.Passes != null && rv.Passes.Count > 0)
            {
                double dv1 = rv.Passes[0].DeltaV;
                string first = double.IsNaN(dv1) ? "first pass: no rendezvous" : Inv($"first pass {dv1:F0} m/s");
                rows.Add(("Launch pass", $"{rv.Chosen + 1} of {rv.Passes.Count}, {first}"));
            }
            return rows;
        }


        // The tab's rows for the plan, at UT now.
        public static List<(string, string)> Readout(LiftTransferPlan p, double now)
        {
            var rows = new List<(string, string)>();
            if (p == null)
                return rows;
            if (p.Goal == LiftGoal.Rendezvous)
                return RendezvousReadout(p, now);
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
