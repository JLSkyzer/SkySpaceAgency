using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// Size and place of the new window, in panel pixels. Sizes are the mockups' (1600 x 900) times 0.85.
    /// A saved rect is restored only if it is sane, and always kept inside the panel: a size saved on a
    /// bigger screen, or before the interface scale went up, never leaves the window off screen.
    /// </summary>
    public static class AppWindowGeometry
    {
        public static readonly Vector2 DefaultSize = new Vector2(1360, 765);
        public static readonly Vector2 MinSize = new Vector2(760, 480);
        public static readonly Vector2 DefaultPosition = new Vector2(60, 60);

        /// <summary>Below this width the three columns become two rows (the view under plan and telemetry).</summary>
        public const float NarrowWidth = 1100;

        static bool Sane(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>
        /// The rect to open with. A saved size that is not positive or not finite gives the default
        /// size; a saved position that is not finite or negative gives the default position.
        /// </summary>
        public static Rect Restore(Vector2 savedPosition, Vector2 savedSize, Vector2 panel)
        {
            Vector2 size = Sane(savedSize.x) && Sane(savedSize.y) && savedSize.x > 0 && savedSize.y > 0
                ? savedSize : DefaultSize;
            Vector2 pos = Sane(savedPosition.x) && Sane(savedPosition.y) && savedPosition.x >= 0 && savedPosition.y >= 0
                ? savedPosition : DefaultPosition;
            return Clamp(new Rect(pos, size), panel);
        }

        /// <summary>
        /// At least MinSize, at most the panel (when the panel is smaller than MinSize, the panel wins),
        /// and moved so it lies inside the panel. A panel of zero size leaves the rect as it is.
        /// </summary>
        public static Rect Clamp(Rect r, Vector2 panel)
        {
            if (!(panel.x > 0) || !(panel.y > 0))
                return r;
            float w = Mathf.Min(Mathf.Max(r.width, MinSize.x), panel.x);
            float h = Mathf.Min(Mathf.Max(r.height, MinSize.y), panel.y);
            float x = Mathf.Clamp(r.x, 0, panel.x - w);
            float y = Mathf.Clamp(r.y, 0, panel.y - h);
            return new Rect(Mathf.Round(x), Mathf.Round(y), Mathf.Floor(w), Mathf.Floor(h));
        }

        public static bool IsNarrow(float width) => width > 0 && width < NarrowWidth;

        /// <summary>True when the window moved or changed size by a whole pixel or more.</summary>
        public static bool ShouldSave(Rect saved, Rect now)
        {
            if (now.width <= 0 || now.height <= 0) return false;   // hidden (display: none)
            return Mathf.Abs(saved.x - now.x) >= 1 || Mathf.Abs(saved.y - now.y) >= 1
                || Mathf.Abs(saved.width - now.width) >= 1 || Mathf.Abs(saved.height - now.height) >= 1;
        }
    }
}
