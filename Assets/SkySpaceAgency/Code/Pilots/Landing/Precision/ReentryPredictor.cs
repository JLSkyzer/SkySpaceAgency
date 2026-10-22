using System;
using K2D2.Landing.Braking;
using KSP.Sim;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing.Precision
{
    // Air tables by altitude above Body.Radius, from 0 to Depth every SampleStep metres. Linear
    // interpolation; every value is 0 above Depth (or with missing tables).
    public struct Atmosphere
    {
        public double Depth;          // m
        public double[] Density;      // kg/m³
        public double[] Pressure;     // the unit of Chutes.MinPressureToOpen
        public double[] SoundSpeed;   // m/s
        public double SampleStep;     // m

        public double DensityAt(double altitude) => Sample(Density, altitude);
        public double PressureAt(double altitude) => Sample(Pressure, altitude);
        public double SoundSpeedAt(double altitude) => Sample(SoundSpeed, altitude);

        double Sample(double[] table, double altitude)
        {
            if (table == null || table.Length == 0 || !(SampleStep > 0) || !(altitude <= Depth))
                return 0;
            double x = Math.Max(0, altitude) / SampleStep;
            int i = (int)x;
            if (i >= table.Length - 1)
                return table[table.Length - 1];
            double f = x - i;
            return table[i] + f * (table[i + 1] - table[i]);
        }
    }

    // The capsule's drag area (m², Cd × area as the game's AreaDrag cube, multipliers folded in)
    // at retrograde attitude, against Mach. Linear interpolation, clamped at both ends.
    public struct DragTable
    {
        public double[] Mach;
        public double[] CdA;

        public double At(double mach)
        {
            if (CdA == null || CdA.Length == 0)
                return 0;
            if (Mach == null || Mach.Length != CdA.Length || CdA.Length == 1 || !(mach > Mach[0]))
                return CdA[0];
            int last = Mach.Length - 1;
            if (mach >= Mach[last])
                return CdA[last];
            int i = 0;
            while (Mach[i + 1] < mach)
                i++;
            double span = Mach[i + 1] - Mach[i];
            double f = span > 0 ? (mach - Mach[i]) / span : 1;
            return CdA[i] + f * (CdA[i + 1] - CdA[i]);
        }
    }

    public struct Chutes
    {
        public double Count;
        public double AreaDeployed;          // m² of drag area (Cd × area) per fully open chute
        public double SemiAreaFraction;      // calibration; <= 0 = ReentryPredictor.DefaultSemiAreaFraction
        public double MinPressureToOpen;     // same unit as Atmosphere.Pressure
        public double DeployAltitude;        // m above the terrain
        /// <summary>Lower bound of the air speed (m/s) for the chutes to open: the game's
        /// Data_Parachute.minimumSpeedToDeploy ("minimum vessel speed required for the parachute to
        /// deploy"). 0 = none.</summary>
        public double MinDeploySpeed;
        /// <summary>Upper bound of the air speed (m/s) for a safe opening, from the SAFE deployment
        /// mode's limit (Module_Parachute.MaxSafeVelocityAtLocalBody). 0 or NaN = no limit.</summary>
        public double MaxDeploySpeed;
        public double SemiToFullDelay;       // s, calibration; <= 0 = ReentryPredictor.DefaultSemiToFullDelay
    }

    public struct ReentryVehicle
    {
        public double MassT;
        public DragTable Drag;
        public Chutes Chutes;
        public double DragScale;   // calibration, multiplies all drag; <= 0 = 1
    }

    // What the reentry did, for the log. NaN = did not happen.
    public struct ReentryTrace
    {
        public double InterfaceUT;            // first crossing of the atmosphere interface
        public double SemiUT, SemiAltitude, SemiAirSpeed;
        public double FullUT, FullAltitude;
        public double TouchdownSpeed;         // m/s relative to the surface
        public double TouchdownVerticalSpeed; // m/s, negative = down
        public double PeakDecel;              // m/s² of drag
        public double SemiAreaFraction, SemiToFullDelay;  // calibration used
        public int Steps;

        public override string ToString() =>
            $"interface T{InterfaceUT:F0}, semi T{SemiUT:F0} at {SemiAltitude:F0} m ({SemiAirSpeed:F0} m/s), " +
            $"full T{FullUT:F0} at {FullAltitude:F0} m, touchdown {TouchdownSpeed:F1} m/s (vertical {TouchdownVerticalSpeed:F1}), " +
            $"peak drag {PeakDecel / BrakeSimulator.G0:F1} g, {Steps} steps, semi area {SemiAreaFraction:F2}, semi->full {SemiToFullDelay:F1} s";
    }

    // Where a parachute reentry stops: Kepler coast (after the optional impulse) to the
    // atmosphere interface, then RK2 (midpoint) through an atmosphere co-rotating with the body,
    // with drag 0.5·ρ·|v_air|²·CdA·DragScale/m against v_air = v − ω×r and the chute phases,
    // until the terrain. Frames as StopPointPredictor: body-relative inertial Zup, z = rotation
    // axis. Pure maths, tested in EditMode.
    public static class ReentryPredictor
    {
        public const double Step = 0.5;                      // s
        public const double MaxAtmosphereTime = 4 * 3600;    // s of simulated flight in the air
        public const double MaxCoast = StopPointPredictor.MaxCoast;
        public const double MinCoastStep = 0.5, MaxCoastStep = 60, InterfacePrecision = 0.01;  // s
        public const double DefaultSemiAreaFraction = 0.1;
        public const double DefaultSemiToFullDelay = 4;      // s
        // Horizontal air speed (m/s) above which the arrival track is still read: under a canopy
        // the horizontal speed decays to the Coriolis drift (cm/s), whose direction is not the track.
        public const double TrackMinSpeed = 10;

        enum Canopy { Packed, Semi, Full }

        public static StopPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Atmosphere air,
            ReentryVehicle vehicle, Impulse? impulse, double impulseIsp = 0)
        {
            return Predict(r, v, ut, body, air, vehicle, impulse, out _, impulseIsp);
        }

        /// <param name="impulseIsp">Engine Isp (s) of the impulse: the capsule enters lighter by the
        /// propellant it used (StopPointPredictor's convention). 0 = constant mass.</param>
        public static StopPrediction Predict(Vector3d r, Vector3d v, double ut, Body body, Atmosphere air,
            ReentryVehicle vehicle, Impulse? impulse, out ReentryTrace trace, double impulseIsp = 0)
        {
            Chutes chutes = vehicle.Chutes;
            double semiFraction = chutes.SemiAreaFraction > 0 ? chutes.SemiAreaFraction : DefaultSemiAreaFraction;
            double semiToFull = chutes.SemiToFullDelay > 0 ? chutes.SemiToFullDelay : DefaultSemiToFullDelay;
            trace = new ReentryTrace
            {
                InterfaceUT = double.NaN, SemiUT = double.NaN, SemiAltitude = double.NaN, SemiAirSpeed = double.NaN,
                FullUT = double.NaN, FullAltitude = double.NaN,
                TouchdownSpeed = double.NaN, TouchdownVerticalSpeed = double.NaN,
                SemiAreaFraction = semiFraction, SemiToFullDelay = semiToFull,
            };
            if (!(vehicle.MassT > 0))
                return Fail("No mass");

            double t = ut;
            double massT = vehicle.MassT;
            if (impulse.HasValue)
            {
                Impulse burn = impulse.Value;
                if (burn.UT < ut - 1e-6)
                    return Fail("The burn is in the past");
                if (burn.UT > ut)
                    KeplerPropagator.Propagate(r, v, body.Mu, burn.UT - ut, out r, out v);
                v = v + burn.DeltaV;
                t = Math.Max(burn.UT, ut);
                if (impulseIsp > 0)
                    massT *= Math.Exp(-burn.DeltaV.magnitude / (impulseIsp * BrakeSimulator.G0));
            }

            double mass = massT * 1000;   // kg
            double dragScale = vehicle.DragScale > 0 ? vehicle.DragScale : 1;
            bool hasChutes = chutes.Count > 0 && chutes.AreaDeployed > 0;
            double fullArea = hasChutes ? chutes.Count * chutes.AreaDeployed : 0;
            Vector3d omega = body.Omega;
            Func<Vector3d, double> terrain = body.TerrainHeight;
            double TerrainAt(Vector3d p, double at) => terrain == null ? 0 : terrain(SurfaceGeometry.AtTime(p, omega, -(at - ut)));

            // Air above the terrain at the interface (Depth) or the terrain itself, whichever first.
            double Clearance(Vector3d p, double at)
            {
                double alt = p.magnitude - body.Radius;
                return Math.Min(alt - air.Depth, alt - TerrainAt(p, at));
            }

            if (r.magnitude - body.Radius > air.Depth)
            {
                string why = Coast(ref r, ref v, ref t, body, Clearance);
                if (why != null)
                    return Fail(why);
            }
            // Just before the interface (the coast stops on its outer side), or already in the air.
            trace.InterfaceUT = t;

            Canopy canopy = Canopy.Packed;
            double fullAt = double.NaN;
            double airTime = 0;
            double peak = 0;
            Vector3d trackNow = new Vector3d(0, 0, 0);   // arrival track, body orientation at ut

            Vector3d Accel(Vector3d p, Vector3d u, Canopy c)
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
                    if (c == Canopy.Semi) cda += semiFraction * fullArea;
                    else if (c == Canopy.Full) cda += fullArea;
                    double k = 0.5 * rho * s * cda * dragScale / mass;   // drag accel / s
                    if (k > 0 && !double.IsNaN(k))
                    {
                        a = a - vAir * k;
                        peak = Math.Max(peak, k * s);
                    }
                }
                return a;
            }

            double h = r.magnitude - body.Radius - TerrainAt(r, t);
            while (h > 0)
            {
                if (airTime >= MaxAtmosphereTime)
                    return Fail($"No touchdown after 4 h of flight in the air ({trace.Steps} steps)", ref trace, peak);

                // Skipped out: coast to the next interface crossing.
                if (r.magnitude - body.Radius > air.Depth && Vector3d.Dot(r, v) > 0)
                {
                    string why = Coast(ref r, ref v, ref t, body, Clearance);
                    if (why != null)
                        return Fail("Skips out of the atmosphere: " + why, ref trace, peak);
                    h = r.magnitude - body.Radius - TerrainAt(r, t);
                    if (h <= 0)
                        break;
                }

                Vector3d a0 = Accel(r, v, canopy);
                Vector3d rMid = r + v * (0.5 * Step);
                Vector3d vMid = v + a0 * (0.5 * Step);
                Vector3d aMid = Accel(rMid, vMid, canopy);
                Vector3d rNext = r + vMid * Step;
                Vector3d vNext = v + aMid * Step;
                double tNext = t + Step;
                double hNext = rNext.magnitude - body.Radius - TerrainAt(rNext, tNext);
                trace.Steps++;
                airTime += Step;

                if (hNext <= 0)
                {
                    double f = h / (h - hNext);
                    r = r + (rNext - r) * f;
                    v = v + (vNext - v) * f;
                    t = t + Step * f;
                    h = 0;
                    break;
                }
                r = rNext;
                v = vNext;
                t = tNext;
                h = hNext;

                Vector3d airVelocity = v - Vector3d.Cross(omega, r);
                Vector3d horizontal = airVelocity - (Vector3d.Dot(airVelocity, r) / r.sqrMagnitude) * r;
                if (horizontal.magnitude >= TrackMinSpeed)
                    trackNow = SurfaceGeometry.AtTime(horizontal, omega, -(t - ut));

                if (!hasChutes)
                    continue;
                double altitude = r.magnitude - body.Radius;
                if (canopy == Canopy.Packed)
                {
                    double pressure = air.PressureAt(altitude);
                    double airSpeed = (v - Vector3d.Cross(omega, r)).magnitude;
                    if (pressure > 0 && pressure >= chutes.MinPressureToOpen && SpeedAllowsOpening(chutes, airSpeed))
                    {
                        canopy = Canopy.Semi;
                        trace.SemiUT = t;
                        trace.SemiAltitude = h;
                        trace.SemiAirSpeed = airSpeed;
                    }
                }
                else if (canopy == Canopy.Semi)
                {
                    if (double.IsNaN(fullAt) && h <= chutes.DeployAltitude)
                        fullAt = t + semiToFull;
                    if (t >= fullAt)
                    {
                        canopy = Canopy.Full;
                        trace.FullUT = t;
                        trace.FullAltitude = h;
                    }
                }
            }

            Vector3d surfaceVelocity = v - Vector3d.Cross(omega, r);
            trace.TouchdownSpeed = surfaceVelocity.magnitude;
            trace.TouchdownVerticalSpeed = Vector3d.Dot(v, r) / r.magnitude;
            trace.PeakDecel = peak;

            Vector3d stopNow = SurfaceGeometry.AtTime(r, omega, -(t - ut));
            double m = stopNow.magnitude;
            var prediction = new StopPrediction
            {
                Ok = true,
                StopUT = t,
                StopDirNow = m > 0 ? stopNow / m : stopNow,
                StopAltitude = 0,
                // Where the Kepler arc ends and the drag begins.
                BrakeStartUT = trace.InterfaceUT,
                // The last horizontal air velocity above TrackMinSpeed: DeorbitSearch.GroundTrack splits the miss along it.
                TrackDirNow = trackNow.sqrMagnitude > 0 ? trackNow / trackNow.magnitude : trackNow,
            };
            if (hasChutes && canopy == Canopy.Packed)
            {
                prediction.Ok = false;
                prediction.Reason = $"The chutes never open (they need air speed {SpeedWindow(chutes)} " +
                                    $"and pressure at least {chutes.MinPressureToOpen:G3}); touchdown at {trace.TouchdownSpeed:F0} m/s";
            }
            else if (hasChutes && canopy == Canopy.Semi)
            {
                prediction.Ok = false;
                prediction.Reason = $"The chutes never fully open before the ground; touchdown at {trace.TouchdownSpeed:F0} m/s";
            }
            return prediction;
        }

        static StopPrediction Fail(string reason) => new StopPrediction { Ok = false, Reason = reason };

        static bool HasMaxSpeed(Chutes c) => c.MaxDeploySpeed > 0;   // false for 0 and NaN

        static bool SpeedAllowsOpening(Chutes c, double airSpeed) =>
            airSpeed >= c.MinDeploySpeed && (!HasMaxSpeed(c) || airSpeed <= c.MaxDeploySpeed);

        static string SpeedWindow(Chutes c) =>
            HasMaxSpeed(c) ? $"between {c.MinDeploySpeed:F0} and {c.MaxDeploySpeed:F0} m/s" : $"at least {c.MinDeploySpeed:F0} m/s";

        static StopPrediction Fail(string reason, ref ReentryTrace trace, double peak)
        {
            trace.PeakDecel = peak;
            return Fail(reason);
        }

        // Kepler coast from above the interface to just before clearance(r, t) <= 0 (the
        // interface, or the terrain if it sticks out of the air). The step never exceeds the time
        // to cover the clearance at the current speed. Null on success, else why not.
        static string Coast(ref Vector3d r, ref Vector3d v, ref double t, Body body, Func<Vector3d, double, double> clearance)
        {
            double energy = 0.5 * v.sqrMagnitude - body.Mu / r.magnitude;
            if (energy >= 0)
                return "Escape trajectory: no landing on this body";
            double a = -body.Mu / (2 * energy);
            double horizon = Math.Min(2 * Math.PI * Math.Sqrt(a * a * a / body.Mu), MaxCoast);

            Vector3d r0 = r, v0 = v;
            double t0 = t;
            double g = clearance(r0, t0);
            if (g <= 0)
                return null;
            double tc = t0;
            double speed = v0.magnitude;
            while (tc - t0 < horizon)
            {
                double step = Math.Max(MinCoastStep, Math.Min(MaxCoastStep, g / Math.Max(speed, 1)));
                double next = tc + step;
                KeplerPropagator.Propagate(r0, v0, body.Mu, next - t0, out var rn, out var vn);
                double gn = clearance(rn, next);
                if (gn <= 0)
                {
                    double lo = tc, hi = next;
                    while (hi - lo > InterfacePrecision)
                    {
                        double mid = 0.5 * (lo + hi);
                        KeplerPropagator.Propagate(r0, v0, body.Mu, mid - t0, out var rm, out _);
                        if (clearance(rm, mid) <= 0)
                            hi = mid;
                        else
                            lo = mid;
                    }
                    if (lo > t0)
                        KeplerPropagator.Propagate(r0, v0, body.Mu, lo - t0, out r, out v);
                    t = lo;
                    return null;
                }
                tc = next;
                g = gn;
                speed = vn.magnitude;
            }
            return "The trajectory does not reach the atmosphere";
        }
    }
}
