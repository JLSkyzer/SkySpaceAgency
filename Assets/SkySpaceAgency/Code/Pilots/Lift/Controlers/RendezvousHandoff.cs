using System;
using System.Globalization;
using K2D2.Controller;
using K2D2.KSPService;
using K2D2.Rendezvous;
using K2D2.UI;
using KSP.Sim;
using KSP.Sim.impl;
using KSP2FlightAssistant.MathLibrary;
using KTools;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace K2D2.Lift
{
    // Rendezvous goal (4.4), after the circularisation: no Lift plane trim (the Rendezvous pilot's
    // plane match does it, above 0.05°). Logs the orbit reached against the prediction, then ends the
    // Lift run and starts the Rendezvous pilot (LiftPilot.ChainToRendezvous). Lift ends first: the
    // Rendezvous pilot's start resets every pilot, and would otherwise reset Lift from inside its own
    // update.
    public class RendezvousHandoff : ExecuteController
    {
        public ILogger logger = SpaceWarp2.API.Logging.LogManager.GetLogger("SkySpaceAgency.Lift");

        readonly LiftPilot lift;
        KSPVessel current_vessel;

        public RendezvousHandoff(LiftPilot lift)
        {
            this.lift = lift;
            current_vessel = K2D2_Plugin.Instance.current_vessel;
        }

        // Never ends the pilot here: Start runs inside LiftPilot's status setter.
        public override void Start()
        {
            base.Start();
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            status_line = "In the parking orbit: handing over to the Rendezvous pilot";
            logger.LogInfo("[Lift] rendezvous: circularized, handing over");
        }

        public override void Update()
        {
            if (finished)
                return;
            finished = true;
            var p = lift.transfer;
            VesselComponent vessel = current_vessel?.VesselComponent;
            if (p == null || p.Goal != LiftGoal.Rendezvous || vessel == null)
            {
                lift.EndLiftPilot(false, "no rendezvous plan");
                return;
            }
            try
            {
                LogOrbit(p, vessel);
            }
            catch (Exception e)
            {
                logger.LogInfo($"[Lift] rendezvous: orbit read failed before the hand-over ({e.GetType().Name}: {e.Message})");
            }
            // The target may have gone during the ascent (recovered, destroyed, docked elsewhere: a
            // new GUID) or landed: 4.3 is never started on it.
            VesselComponent target = RendezvousSnapshot.FindVessel(p.TargetId);
            string why = LiftGoalRules.HandoffRefusal(target != null, target != null && target.LandedOrSplashed,
                target?.Orbit != null && ReferenceEquals(target.Orbit.referenceBody, vessel.Orbit?.referenceBody));
            if (why != null)
            {
                logger.LogInfo("[Lift] rendezvous: hand-over refused: " + why);
                lift.StopRendezvousChain(why, targetLost: true);
                return;
            }
            lift.ChainToRendezvous();
        }

        // The orbit reached, the plane error to the target, and the phase against the prediction
        // (the chosen pass's predicted insertion, flown on to now with Kepler).
        void LogOrbit(LiftTransferPlan p, VesselComponent vessel)
        {
            IKeplerPatch orbit = vessel.Orbit;
            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            var el = K2D2.OrbitPlanning.OrbitMath.Elements(r, v, p.Mu);
            string mode = p.Dock ? "rendezvous + docking" : "rendezvous only";
            VesselComponent target = RendezvousSnapshot.FindVessel(p.TargetId);
            IKeplerPatch to = target?.Orbit;
            if (to == null || !ReferenceEquals(to.referenceBody, orbit.referenceBody))
            {
                logger.LogInfo(Inv($"[Lift] rendezvous: in orbit Ap {(el.ApoapsisRadius - p.BodyRadius) / 1000:F1} Pe {(el.PeriapsisRadius - p.BodyRadius) / 1000:F1} km (parking {(p.OrbitRadius - p.BodyRadius) / 1000:F0}), ") +
                    $"the target is gone or around another body; handing over ({mode})");
                return;
            }
            Vector3d rT = to.GetRelativePositionAtUTZup(now);
            Vector3d vT = to.GetOrbitalVelocityAtUTZup(now);
            Vector3d nT = Vector3d.Cross(rT, vT).normalized;
            double planeError = PlaneSteering.PlaneErrorDeg(r, v, nT);
            double phase = RendezvousLaunch.PhaseDeg(r, rT, Vector3d.Cross(r, v));
            double predicted = double.NaN;
            var rv = p.Rendezvous;
            if (rv != null && rv.Ok && p.Window != null)
            {
                RendezvousLaunch.Insertion(p.Window, rv.ParkingRadius, p.Mu, out var r0, out var v0);
                KeplerPropagator.Propagate(r0, v0, p.Mu, now - p.Window.InsertionUT, out var rPred, out var vPred);
                predicted = RendezvousLaunch.PhaseDeg(rPred, rT, Vector3d.Cross(rPred, vPred));
            }
            logger.LogInfo(Inv($"[Lift] rendezvous: in orbit Ap {(el.ApoapsisRadius - p.BodyRadius) / 1000:F1} Pe {(el.PeriapsisRadius - p.BodyRadius) / 1000:F1} km (parking {(p.OrbitRadius - p.BodyRadius) / 1000:F0}), ") +
                Inv($"plane error to the target {planeError:F3}°, phase {phase:F1}° (predicted {predicted:F1}°), distance {(rT - r).magnitude / 1000:F1} km; handing over ({mode})"));
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            st.Status("Hand-over to the Rendezvous pilot");
            st.Console(status_line);
        }

        public override void UpdateInfoRows(Action<string, string> addRow)
        {
            addRow("Status", "Handing over");
        }

        static string Inv(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }
}
