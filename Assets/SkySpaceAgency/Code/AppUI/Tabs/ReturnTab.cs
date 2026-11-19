namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Return tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class ReturnTab : AppTab
    {
        public ReturnTab(AppContext context) : base(context) { }
        public override string Code => "return";
        public override string Label => "Return";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Return"));
        }
    }
}
