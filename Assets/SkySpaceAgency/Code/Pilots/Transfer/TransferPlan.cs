using System.Collections.Generic;
using KSP.Sim;

namespace K2D2.Transfer
{
    public enum ArrivalKind { Encounter, Orbit }

    // What the player asked for. Plain fields, so the request can be stored with the plan.
    public class TransferRequest
    {
        public int FromBody, ToBody;

        // The vessel's state relative to FromBody, in the inertial Zup frame.
        public Vector3d R0, V0;
        public double UT0;

        public ArrivalKind Arrival = ArrivalKind.Orbit;
        // NaN: the target's LowOrbitRadius, circular.
        public double ArrivalPeriapsisRadius = double.NaN;
        public double ArrivalApoapsisRadius = double.NaN;
        // NaN: free. Set by the approach correction in flight, not by a burn after the capture.
        public double ArrivalInclinationDeg = double.NaN;
        // ∞: no cap.
        public double MaxTripSeconds = double.PositiveInfinity;
    }

    // Injection: a burn toward a hyperbolic excess vector (or, inside one SOI, the transfer burn).
    // Escape: a coast milestone, the SOI exit (no Δv). Capture: a burn at the arrival periapsis.
    public enum StepKind { PlaneChange, OrbitChange, Injection, Escape, Correction, Capture }

    public class PlanStep
    {
        public StepKind Kind;
        public int Body;   // the body orbited at UT
        public double UT;
        // Inertial Δv, in Body's frame. Zero when the burn is closed-loop and its direction is only
        // known in flight: corrections (placeholders, Δv 0, solved by ArrivalTargeting) and captures
        // (DeltaVMagnitude holds the planned cost).
        public Vector3d DeltaV = Vector3d.zero;
        public double DeltaVMagnitude;
        public string Explanation = "";

        // What the step's leg aims at: the first body the leg arrives at, and the orbit wanted there
        // (NaN when free). Corrections aim here; a capture's target orbit is here.
        public int TargetBody = -1;
        public double TargetPeriapsisRadius = double.NaN;
        public double TargetApoapsisRadius = double.NaN;
        public double TargetInclinationDeg = double.NaN;

        // Multi-level plans are flown leg by leg: a new leg starts at each SOI change of the climb, at
        // each SOI entry of a direct descent, and after a staged capture. Every leg after the first is
        // an estimate, re-planned from the actual state when the vessel reaches it.
        public int LegIndex;
        public bool ReplanAtLegStart;
    }

    public class TransferPlan
    {
        public bool Ok;
        public string Refusal;

        public List<PlanStep> Steps = new List<PlanStep>(); // in UT order
        public double DepartureUT = double.NaN;  // the first burn
        public double ArrivalUT = double.NaN;    // the arrival periapsis at the target
        public double TotalDeltaV;               // the steps plus the reserve
        public double ReserveDeltaV;             // max(10, 2 % of the steps)
        public string CaseName = "";
        public TransferRequest Request;

        // Things the player must know before flying, such as an unplanned SOI on the way.
        public List<string> Warnings = new List<string>();

        // The excess velocity the climb must leave ExcessBody with (relative to it, in its parent's
        // frame), toward the transfer at the common ancestor. -1 / zero when there is no such climb.
        public int ExcessBody = -1;
        public Vector3d RequiredExcess = Vector3d.zero;

        public double TripSeconds => ArrivalUT - DepartureUT;
    }
}
