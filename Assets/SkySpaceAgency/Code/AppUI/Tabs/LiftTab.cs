namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB, replaced by the migration of the Lift tab (plan 2026-11-19-new-ui-tab-migration).</summary>
    public class LiftTab : AppTab
    {
        public LiftTab(AppContext context) : base(context) { }
        public override string Code => "lift";
        public override string Label => "Lift";
        protected override void OnBuild(AppPage page)
        {
            page.Plan.Add(new AppCard("Lift"));
        }
    }
}
