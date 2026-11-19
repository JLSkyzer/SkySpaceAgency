using System.Globalization;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The interface scale of the new window, on top of the game's UI Scale. UITK for KSP2 sets each
    /// window's PanelSettings.scale to the game's UI scale (WindowOptions.UseStockScale) and writes it
    /// again on every resolution or UI-scale change; ours multiplies it (AppWindow re-applies it when
    /// UITK has rewritten it). Scaling the panel keeps pointer, drag, resize and screen bounds in one unit.
    /// </summary>
    public static class UiScale
    {
        public const float Min = 0.75f;
        public const float Max = 1.5f;
        public const float Step = 0.05f;
        public const float Default = 1f;

        /// <summary>The player's scale within [Min, Max], on a 5 % step. NaN, infinite or not positive: Default.</summary>
        public static float Clamp(float user)
        {
            if (float.IsNaN(user) || float.IsInfinity(user) || user <= 0)
                return Default;
            double snapped = System.Math.Round(System.Math.Round(user / Step) * Step, 2);
            return Mathf.Clamp((float)snapped, Min, Max);
        }

        /// <summary>The panel scale: the game's UI scale times ours. A missing stock scale counts as 1.</summary>
        public static float Combine(float stock, float user)
        {
            if (float.IsNaN(stock) || float.IsInfinity(stock) || stock <= 0) stock = 1;
            return stock * Clamp(user);
        }

        public static bool NeedsApply(float current, float desired) => Mathf.Abs(current - desired) > 1e-4f;

        /// <summary>"110 %", as the Settings mockup shows it.</summary>
        public static string Percent(float user) =>
            Mathf.RoundToInt(Clamp(user) * 100).ToString(CultureInfo.InvariantCulture) + " %";
    }
}
