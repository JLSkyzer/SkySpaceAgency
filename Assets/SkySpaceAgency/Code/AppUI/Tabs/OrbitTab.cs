namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Orbit tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class OrbitTab : AppTab
    {
        public OrbitTab(AppContext context) : base(context) { }
        public override string Code => "orbit";
        public override string Label => "Orbit";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Orbit"));
        }
    }
}
