using System;
using K2D2.OrbitPlanning;
using K2D2.Transfer;
using KSP.Sim;

namespace K2D2.Node
{
    /// <summary>
    /// What a closed-loop burn (TargetOrbitBurn) steers along: the velocity change still to make
    /// from the current state, re-evaluated every frame until it vanishes. All vectors are in the
    /// Zup frame of the vessel's reference body at ut (r and v are read at the same moment). Pure
    /// code, tested in EditMode. NaN components when no answer exists from this state.
    /// </summary>
    public interface IBurnGuidance
    {
        Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu);

        /// <summary>The goal, for the logs ("asymptote v∞ 1012.3 m/s").</summary>
        string Describe();
    }

    /// <summary>
    /// A guidance with something fixed at ignition: TargetOrbitBurn calls FixAtIgnition once per
    /// run, with the state at the moment the burn starts.
    /// </summary>
    public interface IFixedAtIgnition
    {
        void FixAtIgnition(Vector3d r, Vector3d v, double ut, double mu);
    }

    /// <summary>
    /// Toward a target orbit (apsis radii and plane): OrbitGuidance.DeltaVToOrbit. The sign of the
    /// target radial speed follows the current state until ignition, then stays the one of the
    /// ignition state, so the target does not flip side when the radial speed crosses 0 mid-burn
    /// (TargetOrbitBurn's behaviour before this interface).
    /// </summary>
    public sealed class OrbitTargetGuidance : IBurnGuidance, IFixedAtIgnition
    {
        const double Rad2Deg = 180 / Math.PI;

        public TargetOrbit Target { get; }

        /// <summary>0 until ignition (from the current state), then ±1.</summary>
        public int RadialSign { get; private set; }

        public OrbitTargetGuidance(TargetOrbit target)
        {
            Target = target;
        }

        public Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu) =>
            OrbitGuidance.DeltaVToOrbit(r, v, mu, Target, RadialSign);

        public void FixAtIgnition(Vector3d r, Vector3d v, double ut, double mu) =>
            RadialSign = OrbitGuidance.RadialSign(r, v, Target.Ra, Target.Rp);

        public string Describe() => FormattableString.Invariant(
            $"target orbit ra {Target.Ra / 1000:0.0} km rp {Target.Rp / 1000:0.0} km (radii) inc {Target.InclinationRad * Rad2Deg:0.00}°");
    }

    /// <summary>
    /// Toward a hyperbola leaving along a given excess velocity v∞ (an SOI escape): the velocity at
    /// the current r whose outgoing asymptote is vInf (Hyperbola.VelocityForAsymptote), on the
    /// branch closest to the current velocity, minus v. vInf must be in the Zup frame the burn is
    /// flown in. That frame is fixed for one universe load (Task 9 review) but not across reloads,
    /// so the transfer pilot builds vInf from a fresh snapshot just before the burn.
    /// </summary>
    public sealed class AsymptoteGuidance : IBurnGuidance
    {
        static readonly Vector3d NaN = new Vector3d(double.NaN, double.NaN, double.NaN);

        public Vector3d VInf { get; }

        public AsymptoteGuidance(Vector3d vInf)
        {
            VInf = vInf;
        }

        public Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu)
        {
            if (!Hyperbola.VelocityForAsymptote(r, VInf, mu, out Vector3d vA, out Vector3d vB))
                return NaN;
            Vector3d toA = vA - v;
            Vector3d toB = vB - v;
            return toA.sqrMagnitude <= toB.sqrMagnitude ? toA : toB;
        }

        public string Describe() => FormattableString.Invariant(
            $"asymptote v∞ {VInf.magnitude:0.0} m/s ({VInf.x:0.0}, {VInf.y:0.0}, {VInf.z:0.0})");
    }

    /// <summary>
    /// A fixed impulse deltaV, fixed in inertial space: the Δv to go is (v0 + deltaV) − v, where
    /// v0 is the velocity at ignition (TargetOrbitBurn calls FixAtIgnition, which replaces any v0
    /// given here by the velocity measured then). Built without v0, the guidance returns deltaV
    /// itself until ignition: the direction to align with while waiting, whatever the velocity
    /// does on the coast.
    /// </summary>
    public sealed class FixedImpulseGuidance : IBurnGuidance, IFixedAtIgnition
    {
        public Vector3d DeltaV { get; }
        Vector3d v0;
        bool fixedV0;

        public FixedImpulseGuidance(Vector3d deltaV)
        {
            DeltaV = deltaV;
        }

        public FixedImpulseGuidance(Vector3d deltaV, Vector3d v0)
        {
            DeltaV = deltaV;
            this.v0 = v0;
            fixedV0 = true;
        }

        public Vector3d DeltaVToGo(Vector3d r, Vector3d v, double ut, double mu) =>
            fixedV0 ? v0 + DeltaV - v : DeltaV;

        public void FixAtIgnition(Vector3d r, Vector3d v, double ut, double mu)
        {
            v0 = v;
            fixedV0 = true;
        }

        public string Describe() => FormattableString.Invariant(
            $"fixed impulse {DeltaV.magnitude:0.0} m/s ({DeltaV.x:0.0}, {DeltaV.y:0.0}, {DeltaV.z:0.0})");
    }
}
