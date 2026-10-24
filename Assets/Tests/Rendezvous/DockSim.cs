using System;
using K2D2.Rendezvous;
using KSP.Sim;

// A test double for DockingGuidance. What it models, and what it leaves out:
// - Our vessel is a point at its port face: a double integrator (x'' = a).
//   - RCS: a stand-in for the translation controller, a = (wanted - v) / Tau, clamped to
//     |a| <= AccelLimit (the thrust limit), applied every step.
//   - Main engine: pulsed burns. When the velocity error exceeds Deadband, the vessel turns for
//     TurnSeconds (no thrust, its port axis pointed off the target axis), burns at MainAccel along
//     the error captured at the start of the turn until that velocity is reached, then turns back
//     for TurnSeconds (no thrust). The guidance sees the misaligned port axis meanwhile.
// - The target is a rigid body whose centre stays at the origin, unaccelerated. Its port face sits
//   PortOffset from the centre along the port axis. The port axis rotates at SpinDegPerSec about
//   a fixed axis perpendicular to it (the "rotating target axis").
// - Our attitude: our port axis turns toward the commanded forward at TurnRateDeg per second.
//   Roll is copied from the command at once.
// - Not modelled: orbital relative motion (the Clohessy-Wiltshire terms are about 1e-4 m/s² at
//   50 m in a low orbit, far below the thrust), sensor noise, plume impingement, our own rotation
//   moving our port, and the game's magnetic capture.
public class DockSim
{
    public const double Deg = Math.PI / 180;

    public double Dt = 0.1;
    public double AccelLimit = 0.2;
    public double Tau = 1.0;
    public double TurnRateDeg = 5;
    public double PortOffset = 3;
    public double SpinDegPerSec;
    public Vector3d SpinAxis = new Vector3d(0, 1, 0);
    public Vector3d Axis0 = new Vector3d(0, 0, 1);

    public bool MainEngine;
    public double Deadband = 0.1;
    public double TurnSeconds = 6;
    public double MainAccel = 1.0;

    public Vector3d Pos;       // inertial, our port face
    public Vector3d Vel;       // inertial
    public Vector3d OurAxis;   // our port's outward axis
    public double T;

    // a velocity kick added once (by a test) to our inertial velocity
    public Vector3d PendingKick;

    enum Burn { None, Turning, Burning, TurningBack }
    Burn burn;
    double burnTimer;
    Vector3d burnTarget, burnDir;

    public Vector3d Axis => Rotate(Axis0, SpinAxis.normalized, SpinDegPerSec * Deg * T);
    Vector3d Omega => SpinAxis.normalized * (SpinDegPerSec * Deg);
    public Vector3d Face => PortOffset * Axis;
    public Vector3d FaceVel => Vector3d.Cross(Omega, Face);

    public static Vector3d Rotate(Vector3d v, Vector3d k, double angle)
    {
        return v * Math.Cos(angle) + Vector3d.Cross(k, v) * Math.Sin(angle)
            + k * (Vector3d.Dot(k, v) * (1 - Math.Cos(angle)));
    }

    public DockState State()
    {
        var a = Axis;
        return new DockState
        {
            RelPos = Pos - Face,
            RelVel = Vel - FaceVel,
            TargetAxis = a,
            TargetAxisRate = Vector3d.Cross(Omega, a),
            Time = T,
            TargetCenter = -Face,
            OurAxis = OurAxis,
        };
    }

    // Truth, in the target's rotating frame: axial distance, lateral error, closing speed.
    public void Truth(out double axial, out double lateral, out double closing)
    {
        var a = Axis;
        var r = Pos - Face;
        var vRot = Vel - FaceVel - Vector3d.Cross(Omega, r);
        axial = Vector3d.Dot(r, a);
        lateral = (r - axial * a).magnitude;
        closing = -Vector3d.Dot(vRot, a);
    }

    public double FwdDot => Vector3d.Dot(OurAxis, -Axis);

    public void Apply(DockCommand c)
    {
        if (PendingKick.sqrMagnitude > 0)
        {
            Vel += PendingKick;
            PendingKick = Vector3d.zero;
        }

        var wantedInertial = c.WantedRelVel + FaceVel;
        Vector3d acc = Vector3d.zero;
        if (!MainEngine)
        {
            acc = (wantedInertial - Vel) / Tau;
            if (acc.magnitude > AccelLimit)
                acc = acc.normalized * AccelLimit;
            TurnToward(c.Forward);
        }
        else
        {
            switch (burn)
            {
                case Burn.None:
                    TurnToward(c.Forward);
                    if ((wantedInertial - Vel).magnitude > Deadband)
                    {
                        burnTarget = wantedInertial;
                        burnDir = (wantedInertial - Vel).normalized;
                        burn = Burn.Turning;
                        burnTimer = 0;
                    }
                    break;
                case Burn.Turning:
                    OurAxis = burnDir;
                    burnTimer += Dt;
                    if (burnTimer >= TurnSeconds) burn = Burn.Burning;
                    break;
                case Burn.Burning:
                    OurAxis = burnDir;
                    double left = Vector3d.Dot(burnTarget - Vel, burnDir);
                    if (left <= 1e-6)
                    {
                        burn = Burn.TurningBack;
                        burnTimer = 0;
                        break;
                    }
                    acc = burnDir * Math.Min(MainAccel, left / Dt);
                    break;
                case Burn.TurningBack:
                    TurnToward(c.Forward);
                    burnTimer += Dt;
                    if (burnTimer >= TurnSeconds) burn = Burn.None;
                    break;
            }
        }

        Pos += Vel * Dt + 0.5 * acc * Dt * Dt;
        Vel += acc * Dt;
        T += Dt;
    }

    void TurnToward(Vector3d forward)
    {
        if (forward.sqrMagnitude < 1e-12) return;
        var f = forward.normalized;
        if (OurAxis.sqrMagnitude < 1e-12) { OurAxis = f; return; }
        double angle = Math.Acos(Math.Max(-1, Math.Min(1, Vector3d.Dot(OurAxis, f))));
        double step = TurnRateDeg * Deg * Dt;
        if (angle <= step) { OurAxis = f; return; }
        var k = Vector3d.Cross(OurAxis, f);
        if (k.sqrMagnitude < 1e-12) k = Math.Abs(OurAxis.x) < 0.9 ? Vector3d.Cross(OurAxis, new Vector3d(1, 0, 0)) : Vector3d.Cross(OurAxis, new Vector3d(0, 1, 0));
        OurAxis = Rotate(OurAxis, k.normalized, step).normalized;
    }
}
