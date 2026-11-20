using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    /// <summary>What the new window does when it is smaller than the layout of the mockups.</summary>
    public enum SmallWindowMode
    {
        /// <summary>The columns re-flow (the view goes under the plan and telemetry below 1100 px) and the page scrolls.</summary>
        Reflow,
        /// <summary>The layout of the default size is kept (three columns, timeline, action bar) and shrunk to fit, with no scrolling.</summary>
        Scale,
    }

    /// <summary>
    /// The shrink factor of the "Scale" small-window mode. The content is laid out at a logical size
    /// (the window's inner size divided by the factor, never smaller than the reference) and drawn
    /// with a transform of that factor, so it exactly fills the window. Only the content is scaled:
    /// the window itself keeps its real size, which is what the resize handle, the drag clamp and the
    /// saved geometry read.
    /// </summary>
    public static class SmallWindowScale
    {
        /// <summary>
        /// The inner size the mockups' layout fits without scrolling: the default window size
        /// (1360 x 765) minus the frame (2 px border, 17 px side padding, 5 px top and 22 px bottom).
        /// </summary>
        public static readonly Vector2 ReferenceInner = new Vector2(1322, 734);

        /// <summary>Below this the text would be too small to read (the minimum window, 760 x 480, gives about 0.56).</summary>
        public const float Floor = 0.5f;

        /// <summary>
        /// 1 when the window is as big as the reference or bigger (no enlarging: the interface scale
        /// does that), else the smaller of the two ratios, not below Floor. A size that is not
        /// positive or not finite (hidden window) gives 1.
        /// </summary>
        public static float Factor(Vector2 inner)
        {
            if (!(inner.x > 0) || !(inner.y > 0) || float.IsInfinity(inner.x) || float.IsInfinity(inner.y))
                return 1;
            float s = Mathf.Min(inner.x / ReferenceInner.x, inner.y / ReferenceInner.y);
            return Mathf.Clamp(s, Floor, 1f);
        }

        /// <summary>The size the content is laid out at: the inner size divided by the factor.</summary>
        public static Vector2 LogicalSize(Vector2 inner, float factor)
        {
            if (!(factor > 0)) factor = 1;
            return new Vector2(inner.x / factor, inner.y / factor);
        }
    }
}
