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

        public double TripSeconds => ArrivalUT - DepartureUT;
    }
}
