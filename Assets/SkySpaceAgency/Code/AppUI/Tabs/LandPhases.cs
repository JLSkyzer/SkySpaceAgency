using System.Collections.Generic;
using SkySpaceAgency.Landing;

namespace SkySpaceAgency.AppUI
{
    /// <summary>What kind of landing the Land tab's timeline shows (the run's, else the one the settings choose).</summary>
    public enum LandRunKind
    {
        /// <summary>Planned descent from an orbit (deorbit, pre-brake, coast: LandingPilotDescent).</summary>
        Descent,
        /// <summary>Already on a collision course: warp to the burn, brake, touch down.</summary>
        Collision,
        /// <summary>Vacuum precision landing (LandingPilotPrecision).</summary>
        Precision,
        /// <summary>Parachute precision return (LandingPilotReentry).</summary>
        Reentry,
        /// <summary>Parachute landing without engines (ParachuteDescent).</summary>
        Parachute,
        /// <summary>Boost-back (K2-D2 flavour only, never flown in this mod).</summary>
        BoostBack,
    }

    /// <summary>
    /// The Land tab's phase timeline: the steps of each kind of landing, and which one a
    /// LandingPilot.Mode is in. Pure, tested in EditMode.
    /// </summary>
    public static class LandPhases
    {
        public const string Deorbit = "Deorbit", PreBrake = "Pre-brake", Coast = "Atmospheric coast",
            Brake = "Brake", TouchDown = "Touch down", Warp = "Warp to burn", OrbitPrep = "Orbit prep",
            PlaneChange = "Plane change", MidCourse = "Mid-course", ReentryCoast = "Coast to jettison",
            Jettison = "Jettison", Parachutes = "Parachutes", BoostBackBurn = "Boost-back", Entry = "Entry guidance";

        /// <summary>The drop step's name: "Drop stage 1" for one lower stage, "Drop stages" for more.</summary>
        public static string DropStep(int drops) => drops <= 1 ? "Drop stage 1" : "Drop stages";

        public static LandRunKind KindOf(bool running, bool boostBackRun, bool reentryRun, bool precisionRun,
            bool deorbitRun, bool parachuteMode, bool parachuteRunning, bool precisionSetting, bool atmospheric)
        {
            if (running)
            {
                if (boostBackRun) return LandRunKind.BoostBack;
                if (reentryRun) return LandRunKind.Reentry;
                if (precisionRun) return LandRunKind.Precision;
                if (parachuteRunning) return LandRunKind.Parachute;
                return deorbitRun ? LandRunKind.Descent : LandRunKind.Collision;
            }
            if (atmospheric && parachuteMode)
                return precisionSetting ? LandRunKind.Reentry : LandRunKind.Parachute;
            if (!atmospheric && precisionSetting)
                return LandRunKind.Precision;
            return LandRunKind.Descent;
        }

        /// <summary>The steps of a landing; drops: lower stages dropped on the way (0: none).</summary>
        public static List<string> Steps(LandRunKind kind, bool atmospheric, int drops)
        {
            var s = new List<string>();
            void Braking()
            {
                if (drops > 0) s.Add(DropStep(drops));
                s.Add(Brake);
                s.Add(TouchDown);
            }
            switch (kind)
            {
                case LandRunKind.Descent:
                    s.Add(Deorbit);
                    if (atmospheric) { s.Add(PreBrake); s.Add(Coast); }
                    else s.Add(Warp);
                    Braking();
                    break;
                case LandRunKind.Collision:
                    s.Add(Warp);
                    Braking();
                    break;
                case LandRunKind.Precision:
                    s.Add(OrbitPrep); s.Add(PlaneChange); s.Add(Deorbit); s.Add(MidCourse); s.Add(Warp);
                    Braking();
                    break;
                case LandRunKind.Reentry:
                    s.Add(PlaneChange); s.Add(Deorbit); s.Add(MidCourse); s.Add(ReentryCoast); s.Add(Jettison); s.Add(Parachutes);
                    break;
                case LandRunKind.Parachute:
                    s.Add(Parachutes); s.Add(TouchDown);
                    break;
                case LandRunKind.BoostBack:
                    s.Add(BoostBackBurn); s.Add(Entry);
                    Braking();
                    break;
            }
            return s;
        }

        /// <summary>
        /// The step a mode is in (0 when off or not listed). dropping: a lower stage is still to drop
        /// during the brake (the drop step is lit until it is gone).
        /// </summary>
        public static int IndexOf(IReadOnlyList<string> steps, LandingPilot.Mode mode, bool dropping)
        {
            if (steps == null || steps.Count == 0) return 0;
            int Find(string name) { for (int i = 0; i < steps.Count; i++) if (steps[i] == name) return i; return -1; }
            int FindDrop() { for (int i = 0; i < steps.Count; i++) if (steps[i].StartsWith("Drop stage")) return i; return -1; }
            int i0;
            switch (mode)
            {
                case LandingPilot.Mode.Calibrate:
                case LandingPilot.Mode.OrbitPrep:
                    i0 = Find(OrbitPrep); if (i0 < 0) i0 = Find(PlaneChange); break;
                case LandingPilot.Mode.PlaneChange: i0 = Find(PlaneChange); break;
                case LandingPilot.Mode.Deorbit: i0 = Find(Deorbit); break;
                case LandingPilot.Mode.MidCourse: i0 = Find(MidCourse); break;
                case LandingPilot.Mode.PreBrake: i0 = Find(PreBrake); break;
                case LandingPilot.Mode.AeroCoast: i0 = Find(Coast); break;
                case LandingPilot.Mode.ReentryCoast: i0 = Find(ReentryCoast); break;
                case LandingPilot.Mode.Jettison: i0 = Find(Jettison); break;
                case LandingPilot.Mode.Parachute: i0 = Find(Parachutes); break;
                case LandingPilot.Mode.BoostBack: i0 = Find(BoostBackBurn); break;
                case LandingPilot.Mode.AtmoReentry: i0 = Find(Entry); break;
                case LandingPilot.Mode.TouchDown: i0 = Find(TouchDown); break;
                case LandingPilot.Mode.Brake:
                    i0 = dropping ? FindDrop() : -1;
                    if (i0 < 0) i0 = Find(Brake);
                    break;
                case LandingPilot.Mode.Pause:
                case LandingPilot.Mode.QuickWarp:
                case LandingPilot.Mode.RotationWarp:
                case LandingPilot.Mode.Waiting:
                    i0 = Find(Warp);
                    if (i0 < 0) i0 = Find(Coast);
                    if (i0 < 0) { int b = Find(Brake); i0 = b > 0 ? b - 1 : b; }
                    break;
                default: i0 = 0; break;
            }
            return i0 < 0 ? 0 : i0;
        }

        /// <summary>The compact bar's phase name, or the action bar's hint, for a step.</summary>
        public static string Hint(string step) => step switch
        {
            PreBrake => "The pre-brake keeps the engines under 70 % of their max temperature",
            Coast => "Atmospheric coast: surface retrograde, the heat guard thrusts above 75 %",
            Deorbit => "Deorbit burn: lowers the periapsis into the descent",
            Warp => "Warping to the braking burn",
            Brake => "Braking burn: stops above the ground",
            TouchDown => "Touch down: the fall speed follows the altitude",
            Parachutes => "Parachute descent: the game opens the armed parachutes",
            Jettison => "Jettisoning the service module",
            ReentryCoast => "Coasting to the service module jettison",
            _ => null,
        };
    }
}
