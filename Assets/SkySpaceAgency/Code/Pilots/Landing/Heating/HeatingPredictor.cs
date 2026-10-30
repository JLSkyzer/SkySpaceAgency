using System;
using System.Collections.Generic;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Heating
{
    // The vessel as the unpowered descent sees it: whole-vessel drag at retrograde, the parts heated.
    public struct DescentVehicle
    {
        public double MassT;
        public DragTable Drag;          // m² (Cd × area, multipliers folded in) against Mach, at retrograde
        public double DragScale;        // <= 0 = 1
        public ThermalPart[] Parts;     // null or empty = no heating modelled
        public ThermalConstants Constants;
        public double Harshness;        // CelestialBodyComponent.reentryHarshness; <= 0 = 1
    }

    // One point of the predicted descent, in the air (body-relative inertial Zup).
    public struct DescentSample
    {
        public double UT;
        public Vector3d R, V;
        public double Altitude;   // m above the terrain
        public double AirSpeed;   // m/s
        // The highest Temperature / MaxTemp of any part up to this sample, and that part.
        public double PeakRatio;
        public string PeakPart;
    }

    public struct HeatPrediction
    {
        public bool Ok;
        public string Reason;
        public double InterfaceUT;      // first point in the air (the start when already in it)
        public bool StartsInAir;
        public double PeakRatio;        // highest Temperature / MaxTemp of any part (the start included)
        public string PeakPart;
        public double PeakUT, PeakAltitude, PeakAirSpeed;
        public double PeakFlux;         // kW, highest reentry flux on any part
        public double[] PartPeaks;      // per part, same order as DescentVehicle.Parts
        public double TouchdownUT, TouchdownSpeed;
        public List<DescentSample> Samples;   // every SampleEvery s in the air, the touchdown last
        public int Steps, Passes;

        public override string ToString() => FormattableString.Invariant(
            $"interface T{InterfaceUT:F0}{(StartsInAir ? " (in the air)" : "")}, peak {PeakRatio * 100:0} % on {PeakPart ?? "-"} at T{PeakUT:F0} ") + FormattableString.Invariant(
            $"({PeakAltitude / 1000:0.0} km, {PeakAirSpeed:0} m/s), peak flux {PeakFlux:0} kW, touchdown T{TouchdownUT:F0} at {TouchdownSpeed:0} m/s, {Steps} steps, {Passes} pass(es)");
    }

    // Unpowered descent through an atmosphere co-rotating with the body, with every listed part
    // heated by the game's own model (ThermalModel): Kepler coast to the interface, then RK2
    // (midpoint) with drag 0.5·ρ·|v_air|²·CdA·DragScale/m against v_air = v − ω×r, as
    // ReentryPredictor, until the terrain. The vessel is assumed to hold surface retrograde (the drag
    // table and the parts' areas are at retrograde). Pure maths, tested in EditMode.
    public static class HeatingPredictor
    {
        public const double Step = 0.5;                     // s
        public const double SampleEvery = 2;                // s
        public const double MaxAtmosphereTime = 4 * 3600;   // s
        public const int MaxPasses = 4;

        public static HeatPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Atmosphere air,
            DescentVehicle vehicle, Impulse? impulse, double impulseIsp = 0)
        {
            ThermalPart[] parts = vehicle.Parts ?? new ThermalPart[0];
            var pred = new HeatPrediction
            {
                InterfaceUT = double.NaN, PeakUT = double.NaN, PeakAltitude = double.NaN, PeakAirSpeed = double.NaN,
                TouchdownUT = double.NaN, TouchdownSpeed = double.NaN,
                PartPeaks = new double[parts.Length],
                Samples = new List<DescentSample>(),
            };
            if (!(vehicle.MassT > 0))
                return Fail(pred, "No mass");
            if (!(air.Depth > 0))
                return Fail(pred, "No atmosphere");

            double t = ut;
            double massT = vehicle.MassT;
            if (impulse.HasValue)
            {
                Impulse burn = impulse.Value;
                if (burn.UT < ut - 1e-6)
                    return Fail(pred, "The burn is in the past");
                if (burn.UT > ut)
                    KeplerPropagator.Propagate(r, v, body.Mu, burn.UT - ut, out r, out v);
                v = v + burn.DeltaV;
                t = Math.Max(burn.UT, ut);
                if (impulseIsp > 0)
                    massT *= Math.Exp(-burn.DeltaV.magnitude / (impulseIsp * Braking.BrakeSimulator.G0));
            }

            double mass = massT * 1000;
            double dragScale = vehicle.DragScale > 0 ? vehicle.DragScale : 1;
            double harshness = vehicle.Harshness > 0 ? vehicle.Harshness : 1;
            ThermalConstants c = vehicle.Constants;
            Vector3d omega = body.Omega;
            Func<Vector3d, double> terrain = body.TerrainHeight;
            double TerrainAt(Vector3d p, double at) => terrain == null ? 0 : terrain(SurfaceGeometry.AtTime(p, omega, -(at - ut)));
            double Clearance(Vector3d p, double at)
            {
                double alt = p.magnitude - body.Radius;
                return Math.Min(alt - air.Depth, alt - TerrainAt(p, at));
            }

            // Temperatures start where they are; the start counts in the peak.
            var temps = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                temps[i] = parts[i].Temperature;
                double ratio = parts[i].Ratio;
                pred.PartPeaks[i] = ratio;
                if (ratio > pred.PeakRatio)
                {
                    pred.PeakRatio = ratio;
                    pred.PeakPart = parts[i].Name;
                    pred.PeakUT = t;
                }
            }
            var conductance = new double[parts.Length];
            var radiation = new double[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                radiation[i] = ThermalModel.RadiationCoefficient(c, parts[i].Emissivity, parts[i].WettedArea);

            pred.StartsInAir = r.magnitude - body.Radius <= air.Depth;
            if (!pred.StartsInAir)
            {
                string why = ReentryPredictor.Coast(ref r, ref v, ref t, body, Clearance);
                if (why != null)
                    return Fail(pred, why);
            }
            pred.InterfaceUT = t;
            pred.Passes = 1;

            Vector3d Accel(Vector3d p, Vector3d u)
            {
                double rm = p.magnitude;
                Vector3d a = p * (-body.Mu / (rm * rm * rm));
                double alt = rm - body.Radius;
                double rho = air.DensityAt(alt);
                if (rho > 0)
                {
                    Vector3d vAir = u - Vector3d.Cross(omega, p);
                    double s = vAir.magnitude;
                    double sound = air.SoundSpeedAt(alt);
                    double cda = vehicle.Drag.At(sound > 0 ? s / sound : 0);
                    double k = 0.5 * rho * s * cda * dragScale / mass;
                    if (k > 0 && !double.IsNaN(k))
                        a = a - vAir * k;
                }
                return a;
            }

            double airTime = 0;
            double nextSample = t;
            double h = r.magnitude - body.Radius - TerrainAt(r, t);
            while (h > 0)
            {
                if (airTime >= MaxAtmosphereTime)
                    return Fail(pred, $"No touchdown after 4 h of flight in the air ({pred.Steps} steps)");

                // Skipped out: coast to the next interface crossing.
                if (r.magnitude - body.Radius > air.Depth && Vector3d.Dot(r, v) > 0)
                {
                    if (++pred.Passes > MaxPasses)
                        return Fail(pred, $"Skips out of the atmosphere {MaxPasses} times");
                    string why = ReentryPredictor.Coast(ref r, ref v, ref t, body, Clearance);
                    if (why != null)
                        return Fail(pred, "Skips out of the atmosphere: " + why);
                    h = r.magnitude - body.Radius - TerrainAt(r, t);
                    if (h <= 0)
                        break;
                }

                if (t >= nextSample)
                {
                    pred.Samples.Add(Sample(r, v, t, h, omega, pred));
                    nextSample = t + SampleEvery;
                }

                Vector3d a0 = Accel(r, v);
                Vector3d rMid = r + v * (0.5 * Step);
                Vector3d vMid = v + a0 * (0.5 * Step);
                Vector3d aMid = Accel(rMid, vMid);
                Vector3d rNext = r + vMid * Step;
                Vector3d vNext = v + aMid * Step;
                double tNext = t + Step;
                double hNext = rNext.magnitude - body.Radius - TerrainAt(rNext, tNext);
                pred.Steps++;
                airTime += Step;

                double dt = Step;
                if (hNext <= 0)
                {
                    double f = h / (h - hNext);
                    rNext = r + (rNext - r) * f;
                    vNext = v + (vNext - v) * f;
                    dt = Step * f;
                    tNext = t + dt;
                    hNext = 0;
                }
                r = rNext;
                v = vNext;
                t = tNext;
                h = hNext;

                if (parts.Length > 0 && dt > 0)
                    HeatParts(ref pred, parts, temps, conductance, radiation, c, air, body, r, v, t, omega, harshness, dt);
            }

            Vector3d surfaceVelocity = v - Vector3d.Cross(omega, r);
            pred.TouchdownUT = t;
            pred.TouchdownSpeed = surfaceVelocity.magnitude;
            pred.Samples.Add(Sample(r, v, t, 0, omega, pred));
            pred.Ok = true;
            return pred;
        }

        static void HeatParts(ref HeatPrediction pred, ThermalPart[] parts, double[] temps, double[] conductance, double[] radiation,
            ThermalConstants c, Atmosphere air, Body body, Vector3d r, Vector3d v, double t, Vector3d omega, double harshness, double dt)
        {
            double alt = r.magnitude - body.Radius;
            double rho = air.DensityAt(alt);
            double sound = air.SoundSpeedAt(alt);
            double tAir = air.TemperatureAt(alt);
            double airSpeed = (v - Vector3d.Cross(omega, r)).magnitude;
            double mach = sound > 0 ? airSpeed / sound : 0;
            for (int i = 0; i < parts.Length; i++)
            {
                ThermalPart p = parts[i];
                double flux = ThermalModel.GatedFlux(c, airSpeed, sound, rho, p.AirstreamArea.At(mach), p.DragCoefficient.At(mach), harshness);
                if (flux > pred.PeakFlux)
                    pred.PeakFlux = flux;
                conductance[i] = ThermalModel.Conductance(c, p.WettedArea, alt, air.Depth);
                temps[i] = ThermalModel.Step(c, temps[i], p.ThermalMass, dt, flux, conductance[i], tAir, radiation[i]);
                double ratio = p.MaxTemp > 0 ? temps[i] / p.MaxTemp : 0;
                if (ratio > pred.PartPeaks[i])
                    pred.PartPeaks[i] = ratio;
                if (ratio > pred.PeakRatio)
                {
                    pred.PeakRatio = ratio;
                    pred.PeakPart = p.Name;
                    pred.PeakUT = t;
                    pred.PeakAltitude = alt;
                    pred.PeakAirSpeed = airSpeed;
                }
            }
        }

        static DescentSample Sample(Vector3d r, Vector3d v, double t, double h, Vector3d omega, HeatPrediction pred) => new DescentSample
        {
            UT = t, R = r, V = v, Altitude = h, AirSpeed = (v - Vector3d.Cross(omega, r)).magnitude,
            PeakRatio = pred.PeakRatio, PeakPart = pred.PeakPart,
        };

        /// <summary>
        /// The peak up to `ut` (the last sample at or before it; the whole descent for NaN or past the
        /// touchdown). The planned brake slows the vessel hard (the flux falls with the cube of the
        /// air speed): the heating after its start is not counted, the live guard watches it.
        /// </summary>
        public static double PeakBefore(HeatPrediction pred, double ut, out string part)
        {
            part = pred.PeakPart;
            if (double.IsNaN(ut) || pred.Samples == null || pred.Samples.Count == 0)
                return pred.PeakRatio;
            DescentSample? found = null;
            foreach (DescentSample s in pred.Samples)
            {
                if (s.UT > ut)
                    break;
                found = s;
            }
            if (!found.HasValue)
            {
                // Before the first sample in the air: the start's temperatures only.
                DescentSample first = pred.Samples[0];
                part = first.PeakPart;
                return first.PeakRatio;
            }
            part = found.Value.PeakPart;
            return found.Value.PeakRatio;
        }

        static HeatPrediction Fail(HeatPrediction pred, string reason)
        {
            pred.Ok = false;
            pred.Reason = reason;
            return pred;
        }
    }
}
