namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of Settings (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class SettingsTab : AppTab
    {
        public SettingsTab(AppContext context) : base(context) { }
        public override string Code => "settings";
        public override string Label => "Settings";
        public override bool RightAligned => true;
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Settings"));
        }
    }
}
