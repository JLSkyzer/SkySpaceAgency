namespace SkySpaceAgency.Controller
{
    // A pilot is a complex controller. Its tab in the window (Code/AppUI/Tabs) reads it through
    // AppTabContext.Pilot<T>().
    public class Pilot : ComplexController
    {
        // The frame the window last showed this pilot's tab (AppWindowView.Tick). The pilots that compute
        // their idle values only while their tab is shown do it then.
        int _shown_frame = -10;

        public void MarkShownInNewUi() => _shown_frame = UnityEngine.Time.frameCount;

        /// <summary>The window shows this pilot's tab (this frame or one of the last two).</summary>
        public bool IsShown => UnityEngine.Time.frameCount - _shown_frame <= 2;
    }
}
