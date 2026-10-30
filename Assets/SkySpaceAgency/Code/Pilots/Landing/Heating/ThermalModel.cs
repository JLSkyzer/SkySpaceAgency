using System;
using K2D2.Landing.Precision;

namespace K2D2.Landing.Heating
{
    // The game's part heating, as read in the IL of Ksp2Redux.dll (Redux 26w42c, 2026-10-30). Pure
    // maths, tested in EditMode. What is read and what is assumed:
    //
    // Read in the IL:
    // - Reentry flux, kW (Redux.Ecs.Systems.ThermalUpdate.ReentryFlux.Evaluate, called by
    //   ThermalUpdateSystem/PrepareFluxJob.UpdateReentryFlux):
    //     Scale · (v / ReferenceSpeed)^VelocityExponent · area · ρ^DensityExponent
    //       · SkinFrictionScale · Cd^SkinFrictionExponent · harshness,
    //   0 when v <= 0, ρ <= 0 or ReferenceSpeed <= 0. v = ThermalData.PartAirspeed (part Mach × the
    //   vessel's sound speed: the air speed), ρ = PartBehavior.atmDensity, area =
    //   ExposedAreaProportion × ThermalData.CubeDataArea (= the drag cube's AreaAirstream in the flow
    //   direction, Module_Drag.UpdateCubeData), Cd = the drag cube's DragCoeff, harshness =
    //   CelestialBodyComponent.reentryHarshness. The constants are PhysicsSettings.GLOBAL_REENTRY_FLUX_SCALE,
    //   _REFERENCE_SPEED, _VELOCITY_EXPONENT, _DENSITY_EXPONENT, GLOBAL_REENTRY_SKIN_FRICTION_SCALE and
    //   THERMAL_SKIN_FRICTION (500, 300, 3, 0.53, 1, 0.3 in PhysicsSettings.json, 2026-10-30).
    // - PrepareFluxJob.Execute forces it to 0 without an atmosphere, for ShieldedFromAirstream, and
    //   when ExposedAreaProportion or DragCoefficient is 0; ThermalBehavior.UpdateOcclusionConvection
    //   sets every part's ExposedAreaProportion to 0 below Mach 1: no reentry flux under Mach 1.
    // - Exchange with the air (PrepareFluxJob.UpdateEnvironmentFlux): G = max(0, WettedArea ·
    //   GLOBAL_ATMOSPHERE_FLUX_SCALE (50) · k(alt)), k(alt) = 0.5 · 0.026 · (1 − tanh((alt − depth +
    //   2·500) / 500)) (GetThermalConductivity), source G · Tatm. Radiation: R = ε · σ · WettedArea ·
    //   RadiationFactor · 0.001 (W -> kW), ε = EmissiveConstant clamped to 0..1, 0.4 when <= 0.
    // - Temperature (ThermalIntegrator.Integrate, backward Euler, Newton with a bisection fallback):
    //   C / (dt · s) · (T' − T) = P(T'), P(T) = heatScale · Source + G · Tatm − G · T + R · (Ta⁴ − T⁴),
    //   C = PartComponent.ThermalMass (dry mass × 500 × thermalMassModifier, kJ/K), s =
    //   GLOBAL_POSITIVE_PART_TEMPERATURE_CHANGE_RATE_SCALE (1) when P(T) >= 0, else the NEGATIVE one (5).
    //   No conduction between parts.
    // - Destruction: Temperature > MaxTemp (FinalizeJob), unless the difficulty turns it off.
    //
    // Assumed here: the difficulty's HeatScaling is 1 (not read); Ta = Tatm (the air temperature, 0 K
    // above the atmosphere); every part's ExposedAreaProportion is 1 on the airstream area of its drag
    // cube as attached (the game shades parts behind the leading one with shock cones, not modelled:
    // conservative).
    public struct ThermalConstants
    {
        public double FluxScale, ReferenceSpeed, VelocityExponent, DensityExponent, SkinFrictionScale, SkinFrictionExponent;
        public double AtmosphereFluxScale;   // GLOBAL_ATMOSPHERE_FLUX_SCALE
        public double AtmConductivity;       // 0.026 in GetThermalConductivity
        public double WindowSmoothing;       // 500 m in GetThermalConductivity
        public double StefanBoltzmann;       // W/m²/K⁴
        public double RadiationFactor;
        public double PositiveRateScale, NegativeRateScale;
        public double HeatScale;             // difficulty HeatScaling (assumed 1)

        public static ThermalConstants Defaults => new ThermalConstants
        {
            FluxScale = 500, ReferenceSpeed = 300, VelocityExponent = 3, DensityExponent = 0.53,
            SkinFrictionScale = 1, SkinFrictionExponent = 0.3,
            AtmosphereFluxScale = 50, AtmConductivity = 0.026, WindowSmoothing = 500,
            StefanBoltzmann = 5.670373e-8, RadiationFactor = 1,
            PositiveRateScale = 1, NegativeRateScale = 5, HeatScale = 1,
        };

        public override string ToString() => FormattableString.Invariant(
            $"flux {FluxScale:0.###}·(v/{ReferenceSpeed:0.###})^{VelocityExponent:0.###}·A·ρ^{DensityExponent:0.###}·{SkinFrictionScale:0.###}·Cd^{SkinFrictionExponent:0.###}, ") + FormattableString.Invariant(
            $"air exchange {AtmosphereFluxScale:0.###}×{AtmConductivity:0.####}, σ {StefanBoltzmann:0.####e0}×{RadiationFactor:0.###}, rate scales +{PositiveRateScale:0.###}/−{NegativeRateScale:0.###}, heat scale {HeatScale:0.###} (assumed)");
    }

    // One part as the predictor heats it.
    public struct ThermalPart
    {
        public string Name;
        public double ThermalMass;     // kJ/K
        public double MaxTemp;         // K
        public double Temperature;     // K, now
        public DragTable AirstreamArea;  // m² against Mach, at retrograde, as attached (DragTable reused: Mach -> value)
        public DragTable DragCoefficient; // against Mach
        public double WettedArea;      // m²; 0 = unknown (no air exchange nor radiation: conservative)
        public double Emissivity;      // 0..1

        public double Ratio => MaxTemp > 0 ? Temperature / MaxTemp : 0;
    }

    public static class ThermalModel
    {
        /// <summary>The game's ReentryFlux.Evaluate (kW); the caller passes area = exposed area.</summary>
        public static double ReentryFlux(ThermalConstants c, double airSpeed, double density, double area, double cd, double harshness)
        {
            if (!(airSpeed > 0) || !(density > 0) || !(c.ReferenceSpeed > 0))
                return 0;
            return c.FluxScale * Math.Pow(airSpeed / c.ReferenceSpeed, c.VelocityExponent) * area
                * Math.Pow(density, c.DensityExponent) * c.SkinFrictionScale * Math.Pow(Math.Max(0, cd), c.SkinFrictionExponent) * harshness;
        }

        /// <summary>The game's flux with its gates: none below Mach 1 or without drag coefficient.</summary>
        public static double GatedFlux(ThermalConstants c, double airSpeed, double soundSpeed, double density, double area, double cd, double harshness)
        {
            if (!(soundSpeed > 0) || airSpeed < soundSpeed || !(cd > 0) || !(area > 0))
                return 0;
            return ReentryFlux(c, airSpeed, density, area, cd, harshness);
        }

        /// <summary>Air exchange coefficient G (kW/K) at an altitude above sea level.</summary>
        public static double Conductance(ThermalConstants c, double wettedArea, double altitude, double depth)
        {
            if (!(wettedArea > 0) || !(c.WindowSmoothing > 0))
                return 0;
            double k = 0.5 * c.AtmConductivity * (1 - Math.Tanh((altitude - depth + 2 * c.WindowSmoothing) / c.WindowSmoothing));
            return Math.Max(0, wettedArea * c.AtmosphereFluxScale * k);
        }

        /// <summary>Radiation coefficient R (kW/K⁴); ε <= 0 counts as 0.4, as the game does.</summary>
        public static double RadiationCoefficient(ThermalConstants c, double emissivity, double wettedArea)
        {
            if (!(wettedArea > 0))
                return 0;
            double e = emissivity > 0 ? Math.Min(1, emissivity) : 0.4;
            return e * c.StefanBoltzmann * wettedArea * c.RadiationFactor * 0.001;
        }

        static double Power(ThermalConstants c, double source, double g, double tAir, double rad, double t) =>
            c.HeatScale * source + g * tAir - g * t + rad * (Pow4(tAir) - Pow4(t));

        static double Pow4(double t) { double t2 = t * t; return t2 * t2; }

        /// <summary>
        /// One backward-Euler step of the game's integrator: solves C/(dt·s)·(T' − T) = P(T'), s from
        /// the sign of P(T). Newton from T with a bisection bracket; never below 0 K.
        /// </summary>
        public static double Step(ThermalConstants c, double t, double capacity, double dt, double source, double g, double tAir, double rad)
        {
            if (!(capacity > 0) || !(dt > 0))
                return t;
            double p0 = Power(c, source, g, tAir, rad, t);
            double s = p0 >= 0 ? c.PositiveRateScale : c.NegativeRateScale;
            if (!(s > 0))
                return t;
            double k = capacity / (dt * s);
            // f(T') = k (T' − T) − P(T'): increasing in T' (P decreases with T'), one root.
            double F(double x) => k * (x - t) - Power(c, source, g, tAir, rad, x);
            if (Math.Abs(p0) < 1e-12)
                return t;
            // Bracket: the root is on the side P(T) points to.
            double lo, hi;
            if (p0 > 0)
            {
                lo = t;
                hi = t + p0 / k;   // P only falls above T: the root is below this
            }
            else
            {
                hi = t;
                lo = Math.Max(0, t + p0 / k);
                if (F(lo) > 0)
                    return Math.Max(0, lo);
            }
            double x0 = 0.5 * (lo + hi);
            for (int i = 0; i < 64; i++)
            {
                double fx = F(x0);
                if (Math.Abs(fx) < 1e-9 * Math.Max(1, k))
                    break;
                if (fx > 0) hi = x0; else lo = x0;
                double df = k + g + 4 * rad * x0 * x0 * x0;
                double next = x0 - fx / df;
                x0 = next > lo && next < hi ? next : 0.5 * (lo + hi);
                if (hi - lo < 1e-9)
                    break;
            }
            return Math.Max(0, x0);
        }
    }
}
