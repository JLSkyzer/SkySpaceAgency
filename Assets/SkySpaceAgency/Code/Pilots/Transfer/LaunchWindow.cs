using System;
using KSP.Sim;

namespace K2D2.Transfer
{
    // Which crossing of the target plane a launch uses: northbound, southbound, or either (the plane
    // is out of reach, or every moment is as good as another).
    public enum PlanePass { Ascending, Descending, Any }

    public class LaunchWindowResult
    {
        public bool Ok;
        public string Refusal;

        public double LaunchUT;     // liftoff; the ascent lead is already taken into account
        public double AzimuthDeg;   // surface heading at liftoff (compass, 0 = north, 90 = east)
        public PlanePass Pass;

        // The residual when the plane cannot be reached from the site (its latitude is above the
        // plane's inclination): the angle between the plane reached and the target one, and the
        // burn that removes it in the circular orbit.
        public double PlaneErrorDeg;
        public double PlaneChangeDeltaV;
        public Vector3d ReachedPlaneNormal = new Vector3d(0, 0, 1);
    }

    // When to lift off, and on which heading, to reach a target orbit plane (inertial Zup frame,
    // z = rotation axis; east is the direction a positive rotation about z moves the ground).
    //
    // The model is the rotating frame: the vessel climbs for ascentLeadSeconds and goes
    // ascentDownrangeRad downrange along its surface heading, while the body turns under it. The
    // launch time is the one that puts that insertion point in the target plane. The inertial heading
    // there is the plane's (sin β_i = cos i / cos φ); the surface heading at liftoff removes the
    // ground's speed from it: atan2(v_o sin β_i − ω R cos φ, v_o cos β_i), v_o = sqrt(μ / orbitRadius).
    // The earliest pass wins. When the latitude is above the inclination, the launch is due east (or
    // west) under the plane's vertex, and the residual is reported. Pure maths, never NaN.
    public static class LaunchWindow
    {
        public const string BadInput = "Launch window: invalid body, site or orbit.";
        public const string NoPlane = "Launch window: no target plane.";
        public const string AtPole = "Launch window: the launch site is at a pole.";
        public const string NoPass = "No launch window within the horizon.";

        // Below this out-of-plane angle of the insertion point, launching now is as good as a pass.
        public const double NowToleranceDeg = 0.01;

        const double Deg = Math.PI / 180;
        const double TwoPi = 2 * Math.PI;

        public static LaunchWindowResult Find(double bodyRadius, double mu, Vector3d bodyAngularVelocity,
            Vector3d siteAtUT0, double ut0, Vector3d targetPlaneNormal, double orbitRadius,
            double ascentLeadSeconds, double ascentDownrangeRad, double horizonSeconds)
        {
            double start = Finite(ut0) ? ut0 : 0;
            if (!Finite(ut0) || !(bodyRadius > 0) || !(mu > 0) || !(orbitRadius > 0) || !Finite(bodyRadius)
                || !Finite(mu) || !Finite(orbitRadius) || !Finite(siteAtUT0) || !(siteAtUT0.magnitude > 0)
                || !Finite(bodyAngularVelocity))
                return Refuse(BadInput, start);
            if (!Finite(targetPlaneNormal) || !(targetPlaneNormal.magnitude > 0))
                return Refuse(NoPlane, start);

            Vector3d n = targetPlaneNormal.normalized;
            Vector3d up = siteAtUT0.normalized;
            double cosPhi = Math.Sqrt(up.x * up.x + up.y * up.y);
            if (cosPhi < 1e-9)
                return Refuse(AtPole, start);

            double omega = bodyAngularVelocity.z;
            double lead = Finite(ascentLeadSeconds) && ascentLeadSeconds > 0 ? ascentLeadSeconds : 0;
            double downrange = Finite(ascentDownrangeRad) ? ascentDownrangeRad : 0;
            double horizon = horizonSeconds > 0 ? horizonSeconds : double.PositiveInfinity;
            double vo = Math.Sqrt(mu / orbitRadius);
            double ground = omega * bodyRadius * cosPhi;

            LaunchWindowResult best = null;
            foreach (var pass in new[] { PlanePass.Ascending, PlanePass.Descending })
            {
                var c = Candidate(pass, up, cosPhi, n, omega, lead, downrange, vo, ground, start);
                if (c == null)
                    continue;
                if (best == null || c.LaunchUT < best.LaunchUT - 1e-6
                    || (Math.Abs(c.LaunchUT - best.LaunchUT) <= 1e-6 && c.PlaneErrorDeg < best.PlaneErrorDeg - 1e-9))
                    best = c;
                if (c.Pass == PlanePass.Any)
                    break; // the two passes are one
            }
            if (best == null || best.LaunchUT - start > horizon)
                return Refuse(NoPass, start);
            best.Ok = true;
            return best;
        }

        static LaunchWindowResult Candidate(PlanePass pass, Vector3d up, double cosPhi, Vector3d n, double omega,
            double lead, double downrange, double vo, double ground, double ut0)
        {
            // Inertial heading at the site; out of reach, due east (west for a retrograde plane).
            double sinBi = n.z / cosPhi;
            bool reachable = Math.Abs(sinBi) <= 1;
            double bi = reachable ? Math.Asin(sinBi) : (n.z >= 0 ? Math.PI / 2 : -Math.PI / 2);
            if (!reachable)
                pass = PlanePass.Any;
            else if (pass == PlanePass.Descending)
                bi = Math.PI - bi;
            double bs = Math.Atan2(vo * Math.Sin(bi) - ground, vo * Math.Cos(bi));

            // The insertion point in body-fixed axes (oriented as at ut0): downrange along the track.
            Frame(up, out var east, out var north);
            Vector3d track = Math.Sin(bs) * east + Math.Cos(bs) * north;
            Vector3d q0 = (Math.Cos(downrange) * up + Math.Sin(downrange) * track).normalized;

            double rho = Math.Sqrt(q0.x * q0.x + q0.y * q0.y);
            double nh = Math.Sqrt(n.x * n.x + n.y * n.y);
            double lambda0 = Math.Atan2(q0.y, q0.x);

            // Launching now, if the insertion point is (nearly) in the plane already, or if waiting
            // cannot change anything (equatorial plane, a body that does not turn).
            Vector3d qNow = Turn(q0, omega * lead);
            bool degenerate = rho * nh < 1e-12 || Math.Abs(omega) < 1e-15;
            double dt;
            if (degenerate || (reachable && Math.Abs(Math.Asin(Clamp(Vector3d.Dot(qNow, n)))) < NowToleranceDeg * Deg))
            {
                dt = 0;
                if (degenerate)
                    pass = PlanePass.Any;
            }
            else
            {
                // The longitude the insertion point must reach: a crossing of its latitude circle with
                // the plane, or the plane's vertex when they do not meet.
                double alpha = Math.Atan2(n.y, n.x);
                double cos = -n.z * q0.z / (rho * nh);
                double lambda;
                if (Math.Abs(cos) <= 1 && pass != PlanePass.Any)
                {
                    double a = Math.Acos(cos);
                    double l1 = alpha + a, l2 = alpha - a;
                    bool firstNorthbound = Northbound(n, rho, q0.z, l1);
                    lambda = (pass == PlanePass.Ascending) == firstNorthbound ? l1 : l2;
                }
                else
                {
                    lambda = cos >= 0 ? alpha : alpha + Math.PI;
                    pass = PlanePass.Any;
                }
                double s = omega > 0 ? 1 : -1;
                dt = Wrap(s * (lambda - lambda0) - Math.Abs(omega) * lead) / Math.Abs(omega);
            }

            // The plane actually reached from the insertion point.
            Vector3d q = Turn(q0, omega * (dt + lead));
            double cosPhiQ = Math.Sqrt(q.x * q.x + q.y * q.y);
            Vector3d reached;
            Vector3d np = n - Vector3d.Dot(n, q) * q;
            if (np.magnitude > 1e-12 && Math.Abs(np.normalized.z) <= cosPhiQ + 1e-12)
                reached = np.normalized;
            else
            {
                Frame(q, out _, out var northQ);
                reached = (n.z >= 0 ? 1 : -1) * northQ; // due east (west) under the vertex
            }
            double error = Math.Atan2(Vector3d.Cross(reached, n).magnitude, Vector3d.Dot(reached, n));

            return new LaunchWindowResult
            {
                LaunchUT = ut0 + dt,
                AzimuthDeg = Compass(bs),
                Pass = pass,
                PlaneErrorDeg = error / Deg,
                PlaneChangeDeltaV = 2 * vo * Math.Sin(error / 2),
                ReachedPlaneNormal = reached,
            };
        }

        // East and north at a unit position (z = rotation axis).
        static void Frame(Vector3d up, out Vector3d east, out Vector3d north)
        {
            east = new Vector3d(-up.y, up.x, 0).normalized;
            north = Vector3d.Cross(up, east);
        }

        // Does a prograde orbit of normal n go north at longitude lambda of the circle (rho, z)?
        static bool Northbound(Vector3d n, double rho, double z, double lambda)
        {
            var p = new Vector3d(rho * Math.Cos(lambda), rho * Math.Sin(lambda), z);
            return Vector3d.Cross(n, p).z > 0;
        }

        static Vector3d Turn(Vector3d p, double angle)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            return new Vector3d(c * p.x - s * p.y, s * p.x + c * p.y, p.z);
        }

        static double Compass(double headingRad)
        {
            double d = headingRad / Deg % 360;
            if (d < 0)
                d += 360;
            return d >= 360 ? 0 : d;
        }

        static double Wrap(double a)
        {
            a %= TwoPi;
            if (a < 0)
                a += TwoPi;
            return a >= TwoPi ? 0 : a;
        }

        static double Clamp(double x) => Math.Max(-1, Math.Min(1, x));
        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        static bool Finite(Vector3d v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        static LaunchWindowResult Refuse(string why, double ut0) => new LaunchWindowResult
        {
            Ok = false,
            Refusal = why,
            LaunchUT = ut0,
            AzimuthDeg = 90,
            Pass = PlanePass.Any,
        };
    }
}
