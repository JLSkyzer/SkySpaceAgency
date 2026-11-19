namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Nodes tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class NodesTab : AppTab
    {
        public NodesTab(AppContext context) : base(context) { }
        public override string Code => "nodes";
        public override string Label => "Nodes";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Nodes"));
        }
    }
}
