namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Transfer tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class TransferTab : AppTab
    {
        public TransferTab(AppContext context) : base(context) { }
        public override string Code => "transfer";
        public override string Label => "Transfer";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Transfer"));
        }
    }
}
