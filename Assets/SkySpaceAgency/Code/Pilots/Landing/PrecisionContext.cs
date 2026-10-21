using System;
using K2D2.Landing.Braking;
using K2D2.Landing.Precision;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;

namespace K2D2.Landing
{
    // Inputs of the pure precision planner (Precision/), read from the live game once. Thin on
    // purpose: every game call is here, in one try/catch, so a failure is a refusal with a reason.
    // Frame: body-relative inertial Zup (GetRelativePositionAtUTZup), z = rotation axis, positions
    // given in the body's orientation at UT. Not unit-tested (game types).
    public class PrecisionContext
    {
        public Vector3d R, V;
        public double UT;
        public Precision.Body Body;
        public Precision.Vehicle Vehicle;
        public SurfaceGeometry.Reference Reference;
        public Vector3d TargetDirNow;    // unit, target in the orientation at UT
        public double TargetSlopeDeg;    // terrain slope at the target (two 50 m baselines)
        public double TargetTerrainM;    // terrain height above Radius at the target
        // True only when the pilot's rotation sign was set from a decisive, finite surface-speed
        // measurement for this body (LandingPilot.UpdateRotationSign). False: Body.Omega uses the
        // +1 default, which can be wrong (~36 km miss on the Mun). It stays false on a polar
        // orbit and on very slow bodies. Callers (Task 9) must not fly a precision landing while
        // it is false: retry later (TryBuild again) or refuse with a clear reason.
        public bool RotationSignCalibrated;

        // Rotation used to measure the longitude sign, and the largest gap (degrees) between the
        // game's latitude and the one implied by z = rotation axis before refusing.
        const double LonProbeRad = 0.01;
        const double MaxLatitudeGapDeg = 0.5;
        // Baseline of the slope samples (m), centred on the target.
        const double SlopeBaselineM = 50;
        public const double MaxSlopeDeg = 15;

        static double Wrap180(double d)
        {
            d = (d + 180) % 360;
            if (d < 0) d += 360;
            return d - 180;
        }

        static void GameLatLon(CelestialBodyComponent body, Vector3d p_zup, out double lat, out double lon)
        {
            Vector3d p_yup = new Vector3d(p_zup.x, p_zup.z, p_zup.y);
            Position pos = new Position(body.SimulationObject.transform.celestialFrame, p_yup);
            body.GetLatLonAltFromRadius(pos, out lat, out lon, out _);
        }

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        // log: write the calibration line (once per landing start; re-plans pass false).
        public static bool TryBuild(LandingPilot pilot, out PrecisionContext ctx, out string reason, bool log = true)
        {
            ctx = null;
            reason = null;
            try
            {
                reason = Build(pilot, log, out ctx);
            }
            catch (Exception e)
            {
                ctx = null;
                reason = $"Cannot read the game state for precision landing ({e.GetType().Name}: {e.Message}).";
            }
            if (reason != null)
            {
                ctx = null;
                if (log)
                    pilot?.logger.LogWarning($"[Landing] precision: refused: {reason}");
                return false;
            }
            return true;
        }

        static string Build(LandingPilot pilot, bool log, out PrecisionContext ctx)
        {
            ctx = null;
            var vessel = pilot?.current_vessel?.VesselComponent;
            if (vessel == null)
                return "No active vessel.";
            IKeplerPatch orbit = vessel.Orbit;
            if (orbit == null)
                return "The vessel has no orbit.";
            CelestialBodyComponent body = orbit.referenceBody;
            if (body == null)
                return "The vessel has no reference body.";

            double now = GeneralTools.Current_UT;
            Vector3d r = orbit.GetRelativePositionAtUTZup(now);
            Vector3d v = orbit.GetOrbitalVelocityAtUTZup(now);
            if (!Finite(r.magnitude) || !Finite(v.magnitude) || r.magnitude <= 0)
                return "The vessel's position or velocity is not available.";

            // Body and rotation (the pilot's calibrated sign; calibrates it if not done yet).
            double omega = BodyRotation.AngularSpeed(body.rotationPeriod);
            pilot.UpdateRotationSign(body, r, v, omega);
            int rotationSign = pilot.rotation_sign;
            bool signCalibrated = pilot.rotation_sign_body == body.Name;
            var bodyIn = new Precision.Body
            {
                Mu = body.gravParameter,
                Radius = body.radius,
                Omega = BodyRotation.AngularVelocity(omega, rotationSign),
                TerrainHeight = p => LandingPilot.TerrainHeightAt(body, p),
            };

            // Vehicle: active engines, else every engine (Isp then unknown -> pessimistic).
            pilot.burn_dV.Compute_Thrust();
            double thrust = pilot.burn_dV.active_thrust;
            bool fallback = false;
            if (!(thrust > 0))
            {
                thrust = pilot.burn_dV.full_thrust.magnitude;
                fallback = true;
            }
            double mass = pilot.burn_dV.mass;
            double isp = pilot.burn_dV.active_isp;
            if (!(thrust > 0))
                return "No engine thrust available.";
            if (!(mass > 0))
                return "The vessel's mass is not available.";
            var vehicle = new Precision.Vehicle
            {
                ThrustKN = thrust,
                MassT = mass,
                Isp = Finite(isp) && isp > 0 ? isp : 0,
                StopHeight = pilot.settings.start_touchdown_altitude.V,
            };

            // Surface reference: the vessel now, with its game latitude/longitude.
            double hxy = Math.Sqrt(r.x * r.x + r.y * r.y);
            if (hxy < 1e-6 * r.magnitude)
                return "The vessel is above a pole: the longitude cannot be calibrated.";
            GameLatLon(body, r, out double lat, out double lonA);
            // Latitude-axis check: if z is not the rotation axis, every target lands elsewhere.
            double latZ = Math.Atan2(r.z, hxy) * 180 / Math.PI;
            double latGap = Math.Abs(lat - latZ);

            // Longitude sign, measured: the same position rotated by +LonProbeRad about z.
            double c = Math.Cos(LonProbeRad), s = Math.Sin(LonProbeRad);
            Vector3d rRot = new Vector3d(c * r.x - s * r.y, s * r.x + c * r.y, r.z);
            GameLatLon(body, rRot, out _, out double lonB);
            double dLon = Wrap180(lonB - lonA);
            double expected = LonProbeRad * 180 / Math.PI;
            int lonSign = dLon >= 0 ? 1 : -1;

            // Latitude probe: r turned by LonProbeRad toward +z (toward -z within ~1° of the
            // north pole) must change the game latitude by the same angle, with the same sign.
            // Catches a flipped axis even at the equator, where latZ ≈ lat ≈ 0 either way.
            Vector3d n = r / r.magnitude;
            Vector3d north = new Vector3d(0, 0, 1) - n.z * n;
            north = north / north.magnitude;
            double probeDir = latZ > 89 ? -1 : 1;
            Vector3d rUp = r.magnitude * (Math.Cos(LonProbeRad) * n + probeDir * Math.Sin(LonProbeRad) * north);
            GameLatLon(body, rUp, out double latUp, out _);
            double dLat = latUp - lat;
            double expectedLat = probeDir * expected;

            var settings = pilot.settings;
            double targetLat = settings.target_latitude.V;
            double targetLon = settings.target_longitude.V;

            if (log)
                pilot.logger.LogInfo($"[Landing] precision: body {body.Name} (μ {body.gravParameter:e4}, R {body.radius:n0} m, " +
                    $"period {body.rotationPeriod:n1} s) | longitude sign {lonSign} (lon {lonA:0.0000} -> {lonB:0.0000}, " +
                    $"expected step {expected:0.0000}°) | latitude axis check: game {lat:0.0000}°, atan2(z, hypot(x, y)) {latZ:0.0000}° " +
                    $"(gap {latGap:0.0000}°), probe lat {lat:0.0000} -> {latUp:0.0000} (expected step {expectedLat:0.0000}°) | " +
                    $"rotation sign {rotationSign}, calibrated {signCalibrated}");

            if (!Finite(lat) || !Finite(lonA) || !Finite(lonB) || !Finite(latUp))
                return "The game did not return the vessel's latitude/longitude.";
            if (latGap > MaxLatitudeGapDeg)
                return $"Latitude check failed (game {lat:0.00}°, frame {latZ:0.00}°): the body's rotation axis is not the frame's z axis.";
            if (Math.Abs(dLat - expectedLat) > 0.1 * expected)
                return $"Latitude check failed (moved {dLat:0.0000}° for {expectedLat:0.0000}°): the body's rotation axis is flipped or is not the frame's z axis.";
            // The game longitude must move by the probe angle, in one direction or the other.
            if (Math.Abs(Math.Abs(dLon) - expected) > 0.1 * expected)
                return $"Longitude check failed (moved {dLon:0.0000}° for {expected:0.0000}°): the body's rotation axis is not the frame's z axis.";

            var reference = new SurfaceGeometry.Reference
            {
                Position = r,
                Latitude = lat,
                Longitude = lonA,
                LonSign = lonSign,
            };

            // Target and its slope (two perpendicular 50 m baselines centred on the target).
            if (!Finite(targetLat) || !Finite(targetLon) || Math.Abs(targetLat) > 90)
                return "The target latitude/longitude is not valid.";
            Vector3d targetDir = SurfaceGeometry.Direction(reference, targetLat, targetLon);
            double terrain = bodyIn.TerrainHeight(targetDir * body.radius);
            double slope = SlopeDeg(reference, bodyIn, targetLat, targetLon, body.radius, out double dhEast, out double dhNorth);
            if (!Finite(terrain) || !Finite(slope))
                return "The terrain at the target is not available.";

            if (log)
                pilot.logger.LogInfo($"[Landing] precision: vehicle thrust {thrust:n1} kN{(fallback ? " (all engines: none active)" : "")}, " +
                    $"mass {mass:n2} t, Isp {vehicle.Isp:n0} s, stop height {vehicle.StopHeight:n0} m | target lat {targetLat:0.0000} " +
                    $"lon {targetLon:0.0000}, terrain {terrain:n0} m, slope {slope:0.0}° (Δh east {dhEast:n1} m, north {dhNorth:n1} m over {SlopeBaselineM:n0} m)" +
                    (slope > MaxSlopeDeg ? $" - steeper than {MaxSlopeDeg:n0}°" : ""));

            ctx = new PrecisionContext
            {
                R = r,
                V = v,
                UT = now,
                Body = bodyIn,
                Vehicle = vehicle,
                Reference = reference,
                TargetDirNow = targetDir,
                TargetSlopeDeg = slope,
                TargetTerrainM = terrain,
                RotationSignCalibrated = signCalibrated,
            };
            return null;
        }

        // Slope (degrees) from the terrain gradient: east-west and north-south samples
        // SlopeBaselineM apart, centred on the target.
        static double SlopeDeg(SurfaceGeometry.Reference reference, Precision.Body body,
            double latDeg, double lonDeg, double radius, out double dhEast, out double dhNorth)
        {
            double halfRad = SlopeBaselineM / 2 / radius;
            double halfLatDeg = halfRad * 180 / Math.PI;
            double cosLat = Math.Cos(latDeg * Math.PI / 180);
            // Near a pole, an east-west baseline in longitude degenerates: use its true length.
            double halfLonDeg = cosLat > 1e-3 ? halfLatDeg / cosLat : 0;

            double H(double la, double lo) => body.TerrainHeight(SurfaceGeometry.Direction(reference, la, lo) * radius);

            dhNorth = H(latDeg + halfLatDeg, lonDeg) - H(latDeg - halfLatDeg, lonDeg);
            dhEast = halfLonDeg > 0 ? H(latDeg, lonDeg + halfLonDeg) - H(latDeg, lonDeg - halfLonDeg) : 0;
            double grad = Math.Sqrt(dhEast * dhEast + dhNorth * dhNorth) / SlopeBaselineM;
            return Math.Atan(grad) * 180 / Math.PI;
        }
    }
}
