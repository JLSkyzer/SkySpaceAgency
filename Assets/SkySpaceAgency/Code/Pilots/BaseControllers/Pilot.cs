using SsaUI.Tabs;


namespace SkySpaceAgency.Controller
{

    // a pilot is a complex controller that can have ui page
    public class Pilot : ComplexController
    {
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