using System;
using KSP.Sim;

namespace K2D2.Transfer
{
    // Steering the ascent into a target orbit plane (inertial Zup frame, body-relative r and v; east
    // is z × up, the way a positive rotation about z moves the ground; headings are compass degrees,
    // clockwise from north seen from outside the body).
    //
    // Velocity to gain: the thrust heads for the velocity still to be gained, from the current
    // horizontal velocity to v_o along the target plane. At liftoff that is the solver's azimuth
    // (the ground's speed is part of the velocity); later it takes out whatever lateral velocity the
    // ground or a heading error left. The vessel's position relative to the plane (the out-of-plane
    // angle ψ, which grows away from the node) tilts the target velocity back toward the plane by
    // −K ψ, faded as the speed builds up, because a lateral move is cheap early and becomes a plane
    // error late. The result is a change to the solver's azimuth, clamped.
    //
    // The ascent's inertial velocity is dominated by the ground's rotation at first: a correction
    // computed on the velocity's angle alone would swing by the full inclination at liftoff, which is
    // why the orbital speed and the azimuth are inputs.
    public static class PlaneSteering
    {
        public const double PositionGain = 8;
        const double MaxTilt = 0.3; // rad, the most the target velocity leans toward the plane
        const double Deg = Math.PI / 180;

        public static double HeadingCorrectionDeg(Vector3d r, Vector3d v, Vector3d targetPlaneNormal,
            double azimuthDeg, double orbitalSpeed, double maxCorrectionDeg = 10)
        {
            if (!Finite(r) || !Finite(v) || !Finite(targetPlaneNormal) || !Finite(azimuthDeg)
                || !(orbitalSpeed > 0) || double.IsInfinity(orbitalSpeed) || !(maxCorrectionDeg >= 0)
                || !(r.magnitude > 0) || !(targetPlaneNormal.magnitude > 0))
                return 0;

            Vector3d up = r.normalized;
            Vector3d n = targetPlaneNormal.normalized;
            Vector3d t = Vector3d.Cross(n, up); // along the plane, horizontal
            if (t.magnitude < 1e-9)
                return 0; // straight above the plane's pole
            t = t.normalized;
            Vector3d toward = Vector3d.Cross(up, t); // horizontal, toward the n side

            Vector3d vh = v - Vector3d.Dot(v, up) * up;
            double speed = vh.magnitude;
            double psi = Math.Asin(Math.Max(-1, Math.Min(1, Vector3d.Dot(up, n))));
            double fade = Math.Max(0, 1 - speed / orbitalSpeed);
            double tilt = Math.Max(-MaxTilt, Math.Min(MaxTilt, -PositionGain * psi * fade));

            Vector3d target = Math.Max(orbitalSpeed, speed) * (Math.Cos(tilt) * t + Math.Sin(tilt) * toward);
            Vector3d gain = target - vh;
            if (gain.magnitude < 1e-6 * orbitalSpeed)
                return 0;

            Vector3d east = new Vector3d(-up.y, up.x, 0);
            if (east.magnitude < 1e-9)
                return 0; // at a pole, no compass
            east = east.normalized;
            Vector3d north = Vector3d.Cross(up, east);
            double heading = Math.Atan2(Vector3d.Dot(gain, east), Vector3d.Dot(gain, north)) / Deg;

            double c = heading - azimuthDeg;
            c = (c % 360 + 540) % 360 - 180; // (-180, 180]
            return Math.Max(-maxCorrectionDeg, Math.Min(maxCorrectionDeg, c));
        }

        // Angle between the osculating plane (r × v) and the target plane, in degrees; 0 when either
        // is undefined.
        public static double PlaneErrorDeg(Vector3d r, Vector3d v, Vector3d targetPlaneNormal)
        {
            if (!Finite(r) || !Finite(v) || !Finite(targetPlaneNormal))
                return 0;
            Vector3d h = Vector3d.Cross(r, v);
            if (!(h.magnitude > 0) || !(targetPlaneNormal.magnitude > 0))
                return 0;
            return Math.Atan2(Vector3d.Cross(h, targetPlaneNormal).magnitude, Vector3d.Dot(h, targetPlaneNormal)) / Deg;
        }

        static bool Finite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);
        static bool Finite(Vector3d v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
    }
}
