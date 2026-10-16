using KTools;
using UnityEngine.UIElements;
using K2D2.UI;
using K2D2.Controller;
using K2D2.Node;
using K2D2.KSPService;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.Lift
{
    // Final ascent phase: circularizes the orbit at apoapsis with the closed-loop CircularizeBurn
    // (no maneuver node). The node-based Turn/Warp/BurnManeuver sequence used before followed the
    // game's maneuver model, which is sized on the active stage: at the end of an ascent that is
    // often a nearly empty booster, and the burn went in the wrong direction. Unlike Landing's
    // Circularize, there is no "close enough, skip" or "too high, refuse" gating: Adjust's
    // fine-tuning burn always leaves periapsis low/suborbital, so this phase always has real
    // circularizing work to do.
    public class FinalCircularize : ExecuteController
    {
        public ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.Lift.FinalCircularize");

        LiftSettings lift_settings = null;
        KSPVessel current_vessel;
        LiftPilot lift;

        CircularizeBurn circularize = new CircularizeBurn();

        public FinalCircularize(LiftPilot lift, LiftSettings lift_settings)
        {
            current_vessel = K2D2_Plugin.Instance.current_vessel;
            this.lift = lift;
            this.lift_settings = lift_settings;
            sub_contollers.Add(circularize);
        }

        public override void Start()
        {
            base.Start();
            finished = false;

            if (current_vessel == null)
            {
                lift.EndLiftPilot(false, "No vessel");
                return;
            }

            // Zero throttle unconditionally on entry - Adjust's own finish branches already do
            // this, but re-zeroing here guarantees a clean start regardless of which phase ran
            // before.
            current_vessel.SetThrottle(0);

            logger.LogInfo($"[FinalCircularize] now={GeneralTools.Current_UT:n1}: closed-loop circularize at apoapsis");
            circularize.allow_warp = true;
            // Already past the apoapsis and falling (a late or slow ascent): burn at once instead
            // of refusing; the closed-loop guidance also cancels the downward speed.
            circularize.refuse_past_apoapsis = false;
            // A refusal is handled in Update, not here: ending the Lift pilot from inside its own
            // status setter (which calls this Start) would nest two status changes.
            circularize.Start(atApoapsis: true);
            status_line = circularize.status_line;
        }

        public override void Update()
        {
            if (finished) return;

            // Does nothing once circularize has finished or refused.
            base.Update();
            status_line = circularize.status_line;

            if (circularize.failure_reason != null)
            {
                logger.LogInfo($"[FinalCircularize] stopped: {circularize.failure_reason}");
                lift.EndLiftPilot(false, circularize.failure_reason);
                return;
            }

            if (!circularize.finished)
                return;

            status_line = "Circularize burn complete";
            finished = true;
            logger.LogInfo("[FinalCircularize] circularize burn complete");
            lift.EndLiftPilot(true, "Circularized - ascent complete");
        }

        public override void updateUI(VisualElement root_el, FullStatus st)
        {
            circularize.updateUI(root_el, st);
        }
    }
}
