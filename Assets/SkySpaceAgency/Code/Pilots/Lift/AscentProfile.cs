namespace SkySpaceAgency.Lift
{
    // Player-facing choice of ascent technique, picked in the LIFT tab (the Guided / Gravity
    // turn choice, see LiftTab) and read by
    // LiftPilot.ascent to decide which ExecuteController actually flies the Ascent phase -
    // Ascent.cs (Guided) or ReactiveAscent.cs (GravityTurn). Both finish under the exact same
    // condition (remaining apoapsis within end_ascent_error - see LiftSettings.end_ascent_error),
    // so Coasting/Adjust/FinalCircularize never need to know which one just ran.
    public enum AscentProfile
    {
        // The original K2D2 behavior: pitch follows a fixed altitude-based curve
        // (LiftAscentPath.compute_elevation), then Adjust's own fine-tuning burn mops up whatever
        // that fixed curve didn't quite land on.
        Guided,

        // New: rises vertically, applies a short one-time pitch-over kick, then holds prograde
        // while modulating throttle to hold a target time-to-apoapsis - the classic "gravity
        // turn" technique. See ReactiveAscent.cs for the implementation and its own class comment
        // for why this is an original implementation of that general technique, not a port of any
        // third-party mod.
        GravityTurn
    }
}
