using System.Collections.Generic;
using System.Text;
using KSP.Modules;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using ILogger = ReduxLib.Logging.ILogger;

namespace K2D2.KSPService
{
    // Landing legs. A leg is a part with a deployable module that is bound to the Gear action
    // group (Data_Deployable.DefaultActionGroup) or also carries a wheel/suspension module
    // (Data_WheelBase): Module_Deployable also drives solar panels and antennas, which this
    // leaves out.
    //
    // Deploy() fires the Gear action group, unless GetActionGroupState(Gear) already says True.
    // RecheckDelay seconds of game time later, Update() reads every leg back, and a leg still
    // Retracted is extended through its own "Extend Part" toggle (Data_Deployable.toggleExtend):
    // Module_Deployable.Extend() is protected. Nobody has checked in game yet that Gear deploys
    // stock legs, nor what GetActionGroupState(Gear) reports, so every step is logged as
    // "[Landing] gear: ...".
    public class LandingGear
    {
        public const double RecheckDelay = 3; // s of game time

        readonly ILogger logger = ReduxLib.ReduxLib.GetLogger("SkySpaceAgency.LandingGear");

        bool requested = false;
        double recheck_ut = -1;   // < 0: no re-check pending
        double final_log_ut = -1; // < 0: no post-fallback log pending

        // Forget the previous landing's request.
        public void Reset()
        {
            requested = false;
            recheck_ut = -1;
            final_log_ut = -1;
        }

        public static List<Data_Deployable> FindLegs(VesselComponent vessel)
        {
            var legs = new List<Data_Deployable>();
            if (vessel == null || vessel.SimulationObject == null)
                return legs;
            var vessel_behavior = vessel.SimulationObject.objVesselBehavior;
            if (vessel_behavior == null)
                return legs;

            foreach (PartBehavior part in vessel_behavior.parts)
            {
                if (part == null || part.Model == null)
                    continue;
                if (!part.Model.TryGetModuleData<PartComponentModule_Deployable, Data_Deployable>(out Data_Deployable data) || data == null)
                    continue;
                // Bound to Gear, or carrying a wheel/suspension module (Data_WheelBase covers "rover
                // wheels and landing legs"): either way it is landing gear, not a solar panel or an
                // antenna. The second test keeps legs whose default action group is not Gear.
                bool on_gear = (data.DefaultActionGroup & KSPActionGroup.Gear) != 0;
                bool has_wheel = part.Model.TryGetModuleData<PartComponentModule_WheelBase, Data_WheelBase>(out Data_WheelBase wheel) && wheel != null;
                if (!on_gear && !has_wheel)
                    continue;
                legs.Add(data);
            }
            return legs;
        }

        // "No legs", or for example "4 legs: 2 Extended, 2 Retracted".
        public static string Describe(List<Data_Deployable> legs)
        {
            if (legs.Count == 0)
                return "No legs";

            var counts = new SortedDictionary<string, int>();
            foreach (var leg in legs)
            {
                string state = leg.CurrentDeployState.GetValue().ToString();
                counts[state] = counts.TryGetValue(state, out int n) ? n + 1 : 1;
            }

            var text = new StringBuilder();
            text.Append(legs.Count).Append(legs.Count == 1 ? " leg: " : " legs: ");
            bool first = true;
            foreach (var pair in counts)
            {
                if (!first)
                    text.Append(", ");
                text.Append(pair.Value).Append(' ').Append(pair.Key);
                first = false;
            }
            return text.ToString();
        }

        public static string Summary(VesselComponent vessel) => Describe(FindLegs(vessel));

        // Deploys the legs once per run (until Reset). reason goes to the log.
        public void Deploy(VesselComponent vessel, string reason)
        {
            if (requested || vessel == null)
                return;
            requested = true;

            var legs = FindLegs(vessel);
            if (legs.Count == 0)
            {
                logger.LogInfo($"[Landing] gear: {reason}: no legs on this vessel, nothing to deploy");
                return;
            }

            string before = Describe(legs);
            KSPActionGroupState group = vessel.GetActionGroupState(KSPActionGroup.Gear);
            string path;
            if (group == KSPActionGroupState.True)
            {
                path = "Gear action group already True, not fired";
            }
            else
            {
                vessel.SetActionGroup(KSPActionGroup.Gear, true);
                path = $"Gear action group set to true (was {group})";
            }
            recheck_ut = GeneralTools.Current_UT + RecheckDelay;
            logger.LogInfo($"[Landing] gear: {reason}: {path}; before: {before}");
        }

        // Every frame: the re-check RecheckDelay after Deploy, then one more log line
        // RecheckDelay after a fallback.
        public void Update(VesselComponent vessel)
        {
            if (vessel == null)
                return;
            double now = GeneralTools.Current_UT;

            if (recheck_ut >= 0 && now >= recheck_ut)
            {
                recheck_ut = -1;
                var legs = FindLegs(vessel);
                int toggled = 0, stuck = 0;
                foreach (var leg in legs)
                {
                    if (leg.CurrentDeployState.GetValue() != Data_Deployable.DeployState.Retracted)
                        continue;
                    if (leg.toggleExtend.GetValue())
                    {
                        // Already asked to extend and still retracted: flipping the toggle off
                        // and on again could retract it instead. Leave it, the log says so.
                        stuck++;
                        continue;
                    }
                    leg.toggleExtend.SetValue(true);
                    toggled++;
                }

                string after = Describe(legs);
                if (toggled == 0 && stuck == 0)
                {
                    logger.LogInfo($"[Landing] gear: after {RecheckDelay:0} s: {after}; no fallback needed");
                }
                else
                {
                    logger.LogInfo($"[Landing] gear: after {RecheckDelay:0} s: {after}; fallback: {toggled} leg(s) " +
                        $"extended through their own toggle, {stuck} leg(s) toggled on but still retracted");
                    final_log_ut = now + RecheckDelay;
                }
            }

            if (final_log_ut >= 0 && now >= final_log_ut)
            {
                final_log_ut = -1;
                logger.LogInfo($"[Landing] gear: after fallback: {Summary(vessel)}");
            }
        }
    }
}
