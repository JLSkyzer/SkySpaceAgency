namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Rendezvous tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class RendezvousTab : AppTab
    {
        public RendezvousTab(AppContext context) : base(context) { }
        public override string Code => "rendezvous";
        public override string Label => "Rendezvous";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Rendezvous"));
        }
    }
}
