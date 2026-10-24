using System;
using System.Globalization;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Node;
using K2D2.OrbitPlanning;
using K2D2.UI;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Lift
{
    // Transfer goal, after the circularisation: when the plane is more than 0.1° off the parking
    // plane, a closed-loop burn (TargetOrbitBurn, OrbitTargetGuidance) at the next crossing of the
    // two planes, toward the same apsides in the target plane. Then the end message; the transfer
    // itself is never started here (the Transfer tab plans from the orbit reached).
    public class PlaneTrim : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Lift");

        readonly LiftPilot lift;
        KSPVessel current_vessel;
        readonly TargetOrbitBurn burn = new TargetOrbitBurn();
        bool burning;
        string refusal;
        double error_before = double.NaN;

        public PlaneTrim(LiftPilot lift)
        {
            this.lift = lift;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Ends in Update, never here (Start runs inside LiftPilot's status setter).
        public override void Start()
        {
            base.Start();
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            burning = false;
            refusal = null;
            status_line = "";

            var p = lift.transfer;
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (p == null || !p.Ready || vessel == null)
            {
                refusal = "Plane trim: no transfer plan.";
                return;
            }
            IKeplerPatch orbit = vessel.Orbit;
            double mu = orbit.referenceBody.gravParameter;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            Vector3d n = p.Parking.PlaneNormal;
            error_before = PlaneSteering.PlaneErrorDeg(r, v, n);
            logger.LogInfo(Inv($"[Lift] plane error after the circularisation: {error_before:F3}° (launch window expected {p.Window.PlaneErrorDeg:F3}°)"));
            if (!LiftGoalRules.TrimNeeded(error_before))
                return;

            double toNode = LiftGoalRules.TimeToTrimNode(r, v, mu, n, LiftGoalRules.TrimMinLead, out _);
            if (double.IsNaN(toNode))
                return;
            var el = OrbitMath.Elements(r, v, mu);
            var target = new TargetOrbit { Ra = el.ApoapsisRadius, Rp = el.PeriapsisRadius, PlaneNormal = n.normalized };
            double dv = LiftGoalRules.PlaneChangeDeltaV(v.magnitude, error_before);
            var thrust = new BurndV();
            thrust.Compute_Thrust();
            double duration = BurnTiming.Duration(dv, thrust.active_thrust, thrust.mass, thrust.active_isp);
            double nodeUT = now + toNode;
            double start = BurnTiming.CenteredStart(nodeUT, duration, now);
            logger.LogInfo(Inv($"[Lift] plane trim: {error_before:F3}° at the node in {toNode:F0} s, about {dv:F1} m/s, burn {duration:F1} s, start in {start - now:F0} s"));

            burn.allow_warp = true;
            burn.Start(target, start);
            burning = true;
            status_line = burn.status_line;
        }

        public override void Update()
        {
            if (finished)
                return;
            if (refusal != null)
            {
                finished = true;
                lift.EndLiftPilot(false, refusal);
                return;
            }
            if (burning)
            {
                burn.Update();
                status_line = burn.status_line;
                if (burn.failure_reason != null)
                {
                    finished = true;
                    logger.LogInfo($"[Lift] plane trim stopped: {burn.failure_reason}");
                    TimeWarpTools.SetRateIndex(0, false);
                    lift.EndLiftPilot(false, "Plane trim: " + burn.failure_reason);
                    return;
                }
                if (!burn.finished)
                    return;
            }
            Finish();
        }

        void Finish()
        {
            finished = true;
            var p = lift.transfer;
            IKeplerPatch orbit = current_vessel.VesselComponent.Orbit;
            double now = GeneralTools.Current_UT;
            double after = PlaneSteering.PlaneErrorDeg(orbit.GetRelativePositionAtUTZup(now), orbit.GetOrbitalVelocityAtUTZup(now),
                p.Parking.PlaneNormal);
            logger.LogInfo(Inv($"[Lift] plane error {(burning ? "after the trim" : "(no trim needed)")}: {error_before:F3}° -> {after:F3}°"));
            bool ok = LiftGoalRules.EndMessage(p.TargetName, after, out string text);
            lift.EndLiftPilot(ok, text);
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            if (burning)
                burn.updateUI(root_el, st);
            else
            {
                st.Status("Plane trim");
                st.Console(status_line);
            }
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Plane error", Inv($"{error_before:F3}° before the trim"));
        }

        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
