using System.Collections.Generic;
using KSP.Modules;
using KSP.Sim.impl;

namespace K2D2.KSPService
{
    public struct ParachuteCounts
    {
        public int Stowed, Armed, SemiDeployed, Deployed, Cut;

        public int Total => Stowed + Armed + SemiDeployed + Deployed + Cut;

        // Can still slow the vessel down: every state but CUT.
        public int Usable => Stowed + Armed + SemiDeployed + Deployed;

        public override string ToString() =>
            $"{Total} parachute(s): {Stowed} stowed, {Armed} armed, {SemiDeployed} semi-deployed, {Deployed} deployed, {Cut} cut";
    }

    // The vessel's parachutes, part by part (VesselBehavior.parts, as VesselAeroLookup walks them).
    // State from Data_Parachute.deployState; arming through Module_Parachute.ArmChute().
    public static class Parachutes
    {
        public static ParachuteCounts Count(VesselComponent vessel)
        {
            var counts = new ParachuteCounts();
            foreach (PartBehavior part in Parts(vessel))
            {
                if (!TryGetData(part, out Data_Parachute data))
                    continue;
                switch (data.deployState.GetValue())
                {
                    case Data_Parachute.DeploymentStates.STOWED: counts.Stowed++; break;
                    case Data_Parachute.DeploymentStates.ARMED: counts.Armed++; break;
                    case Data_Parachute.DeploymentStates.SEMIDEPLOYED: counts.SemiDeployed++; break;
                    case Data_Parachute.DeploymentStates.DEPLOYED: counts.Deployed++; break;
                    case Data_Parachute.DeploymentStates.CUT: counts.Cut++; break;
                }
            }
            return counts;
        }

        // Arms every STOWED parachute. ArmChute() is not documented as idempotent, so call this
        // once per landing. The game then opens each parachute by its own settings
        // (DeploymentMode, deployAltitude, minAirPressureToOpen, drogues before mains).
        public static int ArmStowed(VesselComponent vessel)
        {
            int armed = 0;
            foreach (PartBehavior part in Parts(vessel))
            {
                if (!TryGetData(part, out Data_Parachute data))
                    continue;
                if (data.deployState.GetValue() != Data_Parachute.DeploymentStates.STOWED)
                    continue;
                Module_Parachute module = part.GetModule<Module_Parachute>();
                if (module == null)
                    continue;
                module.ArmChute();
                armed++;
            }
            return armed;
        }

        static IEnumerable<PartBehavior> Parts(VesselComponent vessel)
        {
            if (vessel == null || vessel.SimulationObject == null)
                yield break;
            var vessel_behavior = vessel.SimulationObject.objVesselBehavior;
            if (vessel_behavior == null)
                yield break;
            foreach (PartBehavior part in vessel_behavior.parts)
            {
                if (part != null && part.Model != null)
                    yield return part;
            }
        }

        static bool TryGetData(PartBehavior part, out Data_Parachute data)
        {
            if (!part.Model.TryGetModuleData<PartComponentModule_Parachute, Data_Parachute>(out data))
                return false;
            return data != null;
        }
    }
}
