using System;
using System.Collections.Generic;
using System.Linq;
using SkySpaceAgency.Controller;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// What a tab of the new window can reach: the running plugin and its pilots in game, nothing in
    /// the editor (the EditMode harness builds every tab with Plugin == null, so a tab must build and
    /// tick without the game, from its settings alone).
    /// </summary>
    public sealed class AppTabContext
    {
        public SkySpaceAgencyPlugin Plugin;

        /// <summary>Opens the classic window (Settings, placeholder pages).</summary>
        public Action OpenClassicWindow;

        /// <summary>Applies a new interface scale (the window re-scales its panel).</summary>
        public Action<float> SetScale;

        public bool InGame => Plugin != null;

        public T Pilot<T>() where T : Pilot =>
            Plugin?.pilots_manager?.pilots?.OfType<T>().FirstOrDefault();

        public static AppTabContext Editor() => new AppTabContext();
    }

    /// <summary>
    /// A tab of the new window. Build makes its page once; Tick runs every frame while the window is
    /// open and the tab shown (in game only from the plugin; the harness may call it too, so no
    /// game call may throw when the game is absent). ShowSample fills the page with the mockup's
    /// values, for the captures and the layout tests (never called in game).
    /// </summary>
    public abstract class AppTab
    {
        protected AppTab(AppTabContext context) { Context = context ?? AppTabContext.Editor(); }

        public AppTabContext Context { get; }
        public abstract string Code { get; }
        public abstract string Label { get; }
        public virtual bool RightAligned => false;
        public virtual string Badge => null;

        public AppPage Page { get; private set; }

        public AppPage Build()
        {
            Page = new AppPage(Code);
            OnBuild(Page);
            return Page;
        }

        protected abstract void OnBuild(AppPage page);

        public virtual void Tick() { }

        /// <summary>True while this tab's pilot flies: the tab is lit, and the compact flight bar shows it.</summary>
        public virtual bool IsRunning => false;

        /// <summary>The pilot whose idle values this tab shows (stamped every frame the tab is shown, see Pilot.IsShown).</summary>
        public virtual SkySpaceAgency.Controller.Pilot ShownPilot => null;

        /// <summary>The compact flight bar's content while this tab's pilot flies (null: nothing to show).</summary>
        public virtual HudState Hud => null;

        public virtual void ShowSample() { }
    }

    /// <summary>What the compact flight bar shows for the running pilot (docs/design/ui/hud.png).</summary>
    public sealed class HudState
    {
        public string Pilot;           // "LAND"
        public string Phase;           // "Pre-brake"
        public IReadOnlyList<string> Phases;
        public int CurrentPhase;
        public List<(string label, string value)> Readouts = new();
        public string GaugeLabel;      // "HOTTEST PART"
        public float? Gauge;           // 0..1
        public string Alert;           // non-null: the bar turns coral ("Heat guard · thrusting retrograde")
        public string AlertDetail;     // "engine at 78 %"
        public Action Stop;
    }
}
