using System;
using KSP.Sim;

namespace K2D2.Landing.Precision
{
    // TerminalGuidance's local frame at the target, in the body-relative inertial Zup frame:
    // Up = the target direction, East = the direction of growing longitude (LonSign · ẑ × Up,
    // as SurfaceGeometry.Direction), North = Up × (ẑ × Up), toward +z. Origin on the ground at
    // the target. With LonSign −1 the basis is left-handed: TerminalGuidance is isotropic in the
    // horizontal plane (no cross product), so any orthonormal basis does; at a pole, where
    // ẑ × Up vanishes, x̂ × Up stands in for it. Pure maths, tested in EditMode.
    public struct TargetFrame
    {
        public Vector3d East, North, Up;
        public Vector3d Origin;   // the target on the ground (Up · ground radius)

        public static TargetFrame At(Vector3d targetDir, double groundRadius, int lonSign)
        {
            Vector3d up = targetDir / targetDir.magnitude;
            Vector3d east = Vector3d.Cross(new Vector3d(0, 0, 1), up);
            if (east.magnitude < 1e-9)
                east = Vector3d.Cross(new Vector3d(1, 0, 0), up);
            east = east / east.magnitude;
            // North toward +z whatever the longitude sign (the latitude axis is checked to be z).
            Vector3d north = Vector3d.Cross(up, east);
            if (lonSign < 0)
                east = -1.0 * east;
            return new TargetFrame
            {
                East = east,
                North = north,
                Up = up,
                Origin = up * groundRadius,
            };
        }

        public Vector3d ToLocal(Vector3d zup) =>
            new Vector3d(Vector3d.Dot(zup, East), Vector3d.Dot(zup, North), Vector3d.Dot(zup, Up));

        public Vector3d FromLocal(Vector3d local) => East * local.x + North * local.y + Up * local.z;

        // The position (Zup, same orientation as the frame) relative to the target, in the frame:
        // x, y the horizontal offset, z the height above the target's ground.
        public Vector3d Offset(Vector3d positionZup) => ToLocal(positionZup - Origin);
    }
}
