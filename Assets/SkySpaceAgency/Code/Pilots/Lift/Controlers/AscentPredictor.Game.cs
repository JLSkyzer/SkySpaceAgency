using System.Collections.Generic;
using System.Text;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;

namespace K2D2.Lift
{
    // The game side of AscentPredictor: the body's air and the stages of the game's delta-v
    // readout (VesselDeltaV.StageInfo), turned into the plain numbers the pure part flies with.
    public partial class AscentPredictor
    {
        public AscentPredictor(VesselComponent vessel, Start start, GravityTurnControl template, float target_km, float end_error_km)
            : this(ReadBody(vessel.mainBody), ReadStages(vessel), start, template, target_km, end_error_km)
        {
        }

        public static Body ReadBody(CelestialBodyComponent body) => new Body
        {
            mu = body.gravParameter,
            radius = body.radius,
            atmosphere_depth = body.hasAtmosphere ? body.atmosphereDepth : 0,
            pressure = altitude => body.GetPressure(altitude),
            density = altitude => body.GetDensity(body.GetPressure(altitude), body.GetTemperature(altitude)),
        };

        // Null when the game has no readout (the pure part then says "no stage data").
        public static List<StageReading> ReadStages(VesselComponent vessel)
        {
            var info = vessel.VesselDeltaV?.StageInfo;
            if (info == null)
                return null;
            var stages = new List<StageReading>();
            foreach (var s in info)
            {
                if (s == null)
                    continue;
                var reading = new StageReading
                {
                    stage = s.Stage,
                    start_mass = s.StartMass,
                    end_mass = s.EndMass,
                    burn_time_s = s.StageBurnTime,
                    thrust_asl = s.ThrustASL,
                    thrust_vac = s.ThrustVac,
                };
                if (s.EnginesActiveInStage != null)
                    foreach (var e in s.EnginesActiveInStage)
                        if (e?.Engine != null)
                            reading.engines.Add(new EngineReading
                            {
                                thrust_asl = e.ThrustASL,
                                thrust_vac = e.ThrustVac,
                                isp_vac = e.IspVac,
                                throttle_locked = e.Engine.currentEngineModeData?.throttleLocked ?? false,
                            });
                stages.Add(reading);
            }
            return stages;
        }

        static double ActiveThrustVac(DeltaVStageInfo s)
        {
            double thrust = 0;
            if (s.EnginesActiveInStage != null)
                foreach (var e in s.EnginesActiveInStage)
                    if (e?.Engine != null)
                        thrust += e.ThrustVac;
            return thrust;
        }

        // For the log: the game's own stage list, unfiltered, to check what the predictor made of it.
        public static string RawStages(VesselComponent vessel)
        {
            var info = vessel.VesselDeltaV?.StageInfo;
            if (info == null)
                return "none";
            var text = new StringBuilder();
            foreach (var s in info)
                if (s != null)
                    text.Append(System.FormattableString.Invariant(
                        $" | S{s.Stage}: {s.StartMass:n1}->{s.EndMass:n1}t burn {s.StageBurnTime:n0}s vac {s.ThrustVac:n0}kN active {ActiveThrustVac(s):n0}kN ({s.EnginesActiveInStage?.Count ?? 0}) dropped {s.DecoupledMass:n1}t"));
            return text.ToString();
        }
    }
}
