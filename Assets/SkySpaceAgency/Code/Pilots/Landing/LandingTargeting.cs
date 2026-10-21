using System;
using KSP.Sim;
using KSP.Sim.impl;
using KTools;
using KSP2FlightAssistant.MathLibrary;

namespace K2D2.Landing
{
    // Orbit helpers from state vectors (period, apsides, time to the next apsis), shared by the
    // Node tab's closed-loop circularization and the Landing tab. The deorbit search that lived
    // here (FindBestDeorbitBurn, PredictImpactLongitude and its empirical 1.307 rotation factor)
    // was replaced by the pure precision planner in Precision/ on 2026-10-21.
    public static class LandingTargeting
    {
        // ------------------------------------------------------------------------------------
        // Shared helpers
        // ------------------------------------------------------------------------------------

        // Current orbital period from a state vector pair (vis-viva -> semi-major axis ->
        // Kepler's third law).
        public static double OrbitalPeriodFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 2.0 * Math.PI * Math.Sqrt(sma * sma * sma / gravParameter);
        }

        // Apoapsis/periapsis radii of the orbit through (r0, v0). Semi-major axis from vis-viva (same formula as OrbitalPeriodFromStateVectors
        // above), apoapsis/periapsis radii from the standard eccentricity vector
        // (e = ((v^2 - mu/r) r - (r.v) v) / mu) rather than anything engine-specific - deliberately
        // avoids IKeplerPatch/PatchedConicsOrbit member access entirely (unlike ManeuverCreator's
        // existing Circularize* methods, which read orbit.Apoapsis/orbit.Periapsis off a
        // "GetLastOrbit() as PatchedConicsOrbit" cast - the same cast pattern that throws
        // InvalidCastException for the actively-flown vessel under Redux, since its orbit is a
        // CurrentPatchedConicsOrbit, not a PatchedConicsOrbit).
        public static void OrbitalElementsFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter,
            out double semiMajorAxis, out double apoapsisRadius, out double periapsisRadius)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            semiMajorAxis = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);

            double rDotV = Vector3d.Dot(r0, v0);
            Vector3d eccVec = ((v0mag * v0mag - gravParameter / r0mag) * r0 - rDotV * v0) / gravParameter;
            double eccentricity = eccVec.magnitude;

            apoapsisRadius = semiMajorAxis * (1.0 + eccentricity);
            periapsisRadius = semiMajorAxis * (1.0 - eccentricity);
        }

        // Not a real orbital period - a hyperbolic/unbound trajectory only ever passes periapsis
        // once, it doesn't repeat. This is just a sensible time SCALE to sweep across when
        // looking for that one passage (see TimeToNextPeriapsis's caller in Node's
        // CircularizeBurn - the "Circularize at PE" button needs to work on an inbound SOI
        // capture trajectory, before any capture burn has happened). Same formula as
        // OrbitalPeriodFromStateVectors above, but taking the absolute value of the semi-major
        // axis (negative for a hyperbolic/unbound orbit - see OrbitalElementsFromStateVectors) so
        // the result stays finite and positive instead of NaN, plus a 2x safety margin since -
        // unlike a real period - there's no guarantee this "characteristic timescale" is itself
        // long enough to contain the passage being searched for.
        public static double SearchWindowFromStateVectors(Vector3d r0, Vector3d v0, double gravParameter)
        {
            double r0mag = r0.magnitude;
            double v0mag = v0.magnitude;
            double sma = 1.0 / (2.0 / r0mag - v0mag * v0mag / gravParameter);
            return 4.0 * Math.PI * Math.Sqrt(Math.Abs(sma * sma * sma) / gravParameter);
        }

        // Time (seconds from now) until the current orbit's next apoapsis passage - needed to
        // time the Circularize burn (standard practice: circularize by raising periapsis at
        // apoapsis). Found by propagating forward with KeplerPropagator and bisecting on the sign
        // of radial velocity (r.v: positive while outbound/before apoapsis, negative once inbound/
        // past it) rather than deriving it from a closed-form Kepler's-equation true/eccentric/
        // mean-anomaly chain - more propagator calls, but far fewer places for a quadrant/sign
        // mistake to hide.
        public static double TimeToNextApoapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double stepSize = period / samples;

            double prevDt = 0;
            double prevRadialVelocity = Vector3d.Dot(r0, v0);

            for (int i = 1; i <= samples; i++)
            {
                double dt = i * stepSize;
                KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                double radialVelocity = Vector3d.Dot(r, v);

                if (prevRadialVelocity > 0 && radialVelocity <= 0)
                {
                    double lo = prevDt, hi = dt;
                    for (int j = 0; j < 40; j++)
                    {
                        double mid = (lo + hi) / 2.0;
                        KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                        if (Vector3d.Dot(rm, vm) > 0) lo = mid; else hi = mid;
                    }
                    return (lo + hi) / 2.0;
                }

                prevDt = dt;
                prevRadialVelocity = radialVelocity;
            }

            // Radial velocity was already <= 0 from the very first sample - we're at/essentially
            // past apoapsis this instant. "Burn now" beats leaving a caller with a stale time.
            return 0;
        }

        // Mirror of TimeToNextApoapsis above, for the Node tab's "Circularize at PE" button -
        // same propagate-and-bisect approach, watching for the opposite sign flip (radial velocity
        // negative/inbound -> positive/outbound, i.e. periapsis passage).
        public static double TimeToNextPeriapsis(Vector3d r0, Vector3d v0, double gravParameter, double period)
        {
            const int samples = 24;
            double window = period;

            // Retries with a doubled window if no crossing turns up - needed for the
            // hyperbolic/unbound case (Circularize at PE during an SOI capture), where "period" is
            // really SearchWindowFromStateVectors's heuristic estimate rather than a guaranteed-
            // correct orbital period, so a single pass "not found" might just mean the window
            // undershot. A bound (elliptical) caller's real period always contains one, so this
            // loop is a no-op for that case - purely extra insurance for the hyperbolic one.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                double stepSize = window / samples;

                double prevDt = 0;
                double prevRadialVelocity = Vector3d.Dot(r0, v0);

                for (int i = 1; i <= samples; i++)
                {
                    double dt = i * stepSize;
                    KeplerPropagator.Propagate(r0, v0, gravParameter, dt, out Vector3d r, out Vector3d v);
                    double radialVelocity = Vector3d.Dot(r, v);

                    if (prevRadialVelocity < 0 && radialVelocity >= 0)
                    {
                        double lo = prevDt, hi = dt;
                        for (int j = 0; j < 40; j++)
                        {
                            double mid = (lo + hi) / 2.0;
                            KeplerPropagator.Propagate(r0, v0, gravParameter, mid, out Vector3d rm, out Vector3d vm);
                            if (Vector3d.Dot(rm, vm) < 0) lo = mid; else hi = mid;
                        }
                        return (lo + hi) / 2.0;
                    }

                    prevDt = dt;
                    prevRadialVelocity = radialVelocity;
                }

                window *= 2.0;
            }

            // Radial velocity was already >= 0 from the very first sample - we're at/essentially
            // past periapsis this instant. Same reasoning as TimeToNextApoapsis's own fallback.
            return 0;
        }
    }
}
