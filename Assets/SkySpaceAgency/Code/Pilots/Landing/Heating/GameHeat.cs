using System;
using System.Globalization;

namespace K2D2.Landing.Heating
{
    // The game's heat settings for the loaded campaign, as read by DescentContext.ReadGameHeat: the
    // difficulty options (KSP.Game.DifficultyOptionsUtils keys) and the cheat. null = not read.
    public struct GameHeatOptions
    {
        public bool? HeatEnabled;            // "HeatEnabled"
        public bool? PartDestruction;        // "HeatPartDestruction"
        public float? HeatScaling;           // "HeatScaling"
        public bool CheatNoThermodynamics;   // CheatSystem.GetDisableThermodynamics()
        public string ReadError;             // why nothing could be read (no session...)
    }

    // What the landing does with them. Read in the IL of Ksp2Redux.dll (26w42c and 26w46a, identical,
    // 2026-11-18):
    // - ThermalSimGroup.OnUpdate runs the whole thermal group (ThermalUpdateSystem, the auto-shutdown,
    //   the destruction) only when !CheatSystem.GetDisableThermodynamics() &&
    //   SessionManager.IsDifficultyOptionEnabled("HeatEnabled"): with heat off no temperature changes
    //   and nothing is destroyed;
    // - ThermalUpdateSystem.OnUpdate passes TryGetDifficultyOptionState<float>("HeatScaling") (1 when
    //   absent) as FinalizeJob.HeatScale, used in ThermalIntegrator.Evaluate as heatScale · flux.Source
    //   (solar + reentry + exhaust + other flux; not the air exchange nor the radiation): ThermalModel's
    //   HeatScale is that factor;
    // - FinalizeJob.IgnoreMaxTemp = !IsDifficultyOptionEnabled("HeatPartDestruction"): above MaxTemp
    //   it emits an OverheatEvent (ThermalPartDestructionSystem: PartComponent.DestroyPart) only when
    //   false. No engine reads its temperature otherwise (Module_Engine: the emissive glow only), so
    //   with destruction off a hot engine keeps working.
    // Hence: heat off -> nothing to plan, no guard; destruction off -> the heating is still predicted
    // and logged, a warning above 100 %, but it neither refuses nor costs Δv, no guard. An option
    // that cannot be read keeps the current behaviour (judged) and the log says so. Pure, tested in EditMode.
    public struct GameHeatPolicy
    {
        public bool Simulated;       // the game heats parts at all
        public bool Destroys;        // a part above MaxTemp is destroyed
        public double HeatScale;     // multiplies the flux source, as the game does
        public string Note;          // one line for the log

        public bool Judged => Simulated && Destroys;
        /// <summary>The plan's reason not to judge the heating (DescentInput.HeatIgnored); null when judged.</summary>
        public string IgnoredNote => Judged ? null : Note;
    }

    public static class GameHeat
    {
        public static GameHeatPolicy Decide(GameHeatOptions o)
        {
            var p = new GameHeatPolicy { Simulated = true, Destroys = true, HeatScale = 1 };
            string unread = "";
            if (o.ReadError != null)
                unread = $" (difficulty not read ({o.ReadError}): heat judged as before)";
            else
            {
                if (!o.HeatEnabled.HasValue)
                    unread += " (HeatEnabled not read: taken as on)";
                if (!o.PartDestruction.HasValue)
                    unread += " (HeatPartDestruction not read: taken as on)";
                if (!o.HeatScaling.HasValue)
                    unread += " (HeatScaling not read: 1)";
            }
            float scale = o.HeatScaling ?? 1f;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale < 0)
            {
                unread += string.Format(CultureInfo.InvariantCulture, " (HeatScaling {0} ignored: 1)", scale);
                scale = 1;
            }
            p.HeatScale = scale;
            string scaleText = string.Format(CultureInfo.InvariantCulture, "heat scaling {0:0.###}", p.HeatScale);

            if (o.CheatNoThermodynamics || o.HeatEnabled == false)
            {
                p.Simulated = false;
                string why = o.CheatNoThermodynamics ? "cheat Disable Thermodynamics on" : "difficulty HeatEnabled off";
                p.Note = $"game heat disabled, {why}: no heat limit on the plan (Δv only), heat guard off" + unread;
                return p;
            }
            if (o.PartDestruction == false)
            {
                p.Destroys = false;
                p.Note = $"game part destruction by heat off, difficulty HeatPartDestruction off: parts are not destroyed above their max temperature, " +
                    $"the heating is predicted and logged but neither refuses nor constrains the plan, heat guard off, {scaleText}" + unread;
                return p;
            }
            p.Note = $"game heat on, part destruction on, {scaleText}" + unread;
            return p;
        }

        public static string OverMaxWarning(string engine, double peak) => string.Format(CultureInfo.InvariantCulture,
            "Predicted heating {0:0} % on the engine {1}: above its max temperature, but this campaign's difficulty does not destroy parts by heat.",
            peak * 100, engine ?? "?");
    }
}
