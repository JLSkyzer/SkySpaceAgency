using SsaUI.Tabs;


namespace SkySpaceAgency.Controller
{

    // a pilot is a complex controller that can have ui page
    public class Pilot : ComplexController
    {
        // The frame the new window last showed this pilot's tab (AppWindowView.Tick). The pilots that
        // compute their idle values only while their classic page is visible also do it then.
        int _shown_in_new_ui_frame = -10;

        public void MarkShownInNewUi() => _shown_in_new_ui_frame = UnityEngine.Time.frameCount;

        /// <summary>The classic page is visible, or the new window shows this pilot's tab (this frame or the last two).</summary>
        public bool IsShown => (page != null && page.isVisible) || UnityEngine.Time.frameCount - _shown_in_new_ui_frame <= 2;

        public override bool isRunning
        {
            get => base.isRunning;

            set
            {
                if (value == base.isRunning)
                    return;

                base.isRunning = value;
                // update the panel
                if (_page != null)
                    _page.isRunning = isRunning;
            }
        }

        // MUST be build in constructor if an UI is needed
        protected SsaPage _page = null;

        public virtual SsaPage page
        {
            get => _page;
        }
    }

}