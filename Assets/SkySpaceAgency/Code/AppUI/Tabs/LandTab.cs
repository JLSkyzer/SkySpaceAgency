namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Land tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class LandTab : AppTab
    {
        public LandTab(AppContext context) : base(context) { }
        public override string Code => "land";
        public override string Label => "Land";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Land"));
        }
    }
}
