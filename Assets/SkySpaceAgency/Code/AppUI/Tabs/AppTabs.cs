using System.Collections.Generic;

namespace SkySpaceAgency.AppUI
{
    /// <summary>The tabs of the new window, in the mockups' order (SETTINGS last, on the right).</summary>
    public static class AppTabs
    {
        public static List<AppTab> Create(AppContext context) => new List<AppTab>
        {
            new LiftTab(context),
            new OrbitTab(context),
            new TransferTab(context),
            new RendezvousTab(context),
            new LandTab(context),
            new ReturnTab(context),
            new NodesTab(context),
            new SettingsTab(context),
        };
    }
}
