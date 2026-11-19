using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// RETURN (docs/design/ui/return.png): booster recovery. There is no Return pilot yet (roadmap
    /// docs/superpowers/roadmap-booster-return-video-scripting.md, after the transfers, the K2-D2 pull
    /// requests and the new interface), so this page is the mockup's layout with every control
    /// disabled, a "Planned" badge and a message that says so. It has no settings: nothing here is
    /// saved, and nothing flies.
    /// </summary>
    public class ReturnTab : AppTab
    {
        public ReturnTab(AppTabContext context) : base(context) { }
        public override string Code => "return";
        public override string Label => "Return";
        public override string Badge => "Planned";

        public const string PlannedMessage = "Planned: no Return pilot yet, nothing here flies";
        public const string ActionNote = "Planned: booster recovery, after the transfers and rendezvous work";

        public static readonly string[] Phases = { "Separation", "Clearance", "Flip", "Boostback", "Entry burn", "Landing burn", "Landed" };

        public static readonly string[] Checks = { "Distance over 60 m", "Upper stage out of the plume", "Paths never closer than 40 m" };

        readonly List<VisualElement> _disabled = new();
        readonly List<VisualElement> _checkDots = new();

        public AppPill PlannedPill { get; private set; }
        public AppButton ReturnButton { get; private set; }
        public AppTile KeepTile { get; private set; }
        public AppTile SeparateTile { get; private set; }
        public AppTile BoostbackTile { get; private set; }
        public AppTile LandingTile { get; private set; }
        public Label StageTitle { get; private set; }
        public Label StageDetail { get; private set; }
        public Label FoundText { get; private set; }
        public Label PointTitle { get; private set; }
        public Label PointDetail { get; private set; }
        public ReturnProfileView ProfileView { get; private set; }
        public IReadOnlyList<VisualElement> DisabledControls => _disabled;

        protected override void OnBuild(AppPage page)
        {
            page.AddToClassList("ssa-return");

            // ---- Booster ----
            var booster = new AppCard("Booster");
            PlannedPill = new AppPill(StatusKind.Info, PlannedMessage);
            PlannedPill.AddToClassList("ssa-return__planned");
            booster.Add(PlannedPill);

            var stage = Ui.Box("ssa-return-box");
            StageTitle = Ui.Label("Booster stage", "ssa-return-box__title");
            StageDetail = Ui.Label("chosen from the vessel's stages", "ssa-return-box__detail");
            stage.Add(StageTitle);
            stage.Add(StageDetail);
            booster.Add(Disable(stage));

            var found = Ui.Box("ssa-return-found");
            found.Add(Ui.Box("ssa-dot"));
            FoundText = Ui.Label("Recovery computer: looked for at separation", "ssa-return-found__text");
            found.Add(FoundText);
            booster.Add(Disable(found));

            booster.Add(Ui.Label("Return point", "ssa-field__label"));
            var point = Ui.Box("ssa-return-box", "ssa-return-box--gold");
            PointTitle = Ui.Label("Landing pad or waypoint", "ssa-return-box__title");
            PointDetail = Ui.Label("latitude · longitude · distance from the pad", "ssa-return-box__detail");
            point.Add(PointTitle);
            point.Add(PointDetail);
            booster.Add(Disable(point));

            var buttons = new AppButtonRow();
            Disable(buttons.Add("Pick on map", null));
            Disable(buttons.Add("Coordinates", null));
            booster.Add(buttons);

            booster.Add(Disable(new AppSwitch("Start Return on taking control", false)));
            booster.Add(Disable(new AppSwitch("Record the booster", false)));
            booster.Add(Disable(new AppSwitch("Ditch at sea if short", false)));
            page.Plan.Add(booster);

            // ---- Return profile ----
            var profile = new AppCard("Return profile", "side view, downrange", sky: true);
            ProfileView = new ReturnProfileView();
            profile.Add(ProfileView);
            page.View.Add(profile);

            // ---- Recovery budget ----
            var budget = new AppCard("Recovery budget");
            var tiles = Ui.Box("ssa-tiles");
            KeepTile = new AppTile("Δv to keep", "—", "m/s", AppTile.Kind.Gold);
            SeparateTile = new AppTile("Separate at", "—", "T+");
            BoostbackTile = new AppTile("Boostback", "—", "m/s");
            LandingTile = new AppTile("Landing", "—", "m/s");
            tiles.Add(KeepTile);
            tiles.Add(SeparateTile);
            tiles.Add(BoostbackTile);
            tiles.Add(LandingTile);
            budget.Add(Disable(tiles));
            budget.Add(Ui.Label("CLEARANCE AT SEPARATION", "ssa-section-title"));
            foreach (string check in Checks)
            {
                var row = Ui.Box("ssa-return-check");
                var dot = Ui.Box("ssa-dot", "ssa-dot--neutral");
                row.Add(dot);
                row.Add(Ui.Label(check, "ssa-return-check__text"));
                budget.Add(row);
                _checkDots.Add(dot);
            }
            budget.Add(Ui.Box("ssa-grow"));
            budget.Add(new AppPill(StatusKind.Info, "Engines light only when all checks pass"));
            page.Side.Add(budget);

            page.SetTimeline(Phases, 0);
            page.Timeline.AddToClassList("ssa-return__timeline");
            page.Actions.SetNote(ActionNote);
            Disable(page.Actions.AddButton("Abort", AppButton.Kind.Abort));
            ReturnButton = Disable(page.Actions.AddButton("Return", AppButton.Kind.Main));
        }

        T Disable<T>(T e) where T : VisualElement
        {
            e.SetEnabled(false);
            e.AddToClassList("ssa-return--disabled");
            _disabled.Add(e);
            return e;
        }

        /// <summary>The mockup's values (return.png), for the captures: still disabled, still planned.</summary>
        public override void ShowSample()
        {
            StageTitle.text = "Stage 1 · booster";
            StageDetail.text = "3 engines · grid fins · legs";
            PointTitle.text = "KSC · Landing pad 1";
            PointDetail.text = "0.10° S · 74.56° W · 2.3 km from the pad";
            FoundText.text = "Recovery Computer found (SM)";
            KeepTile.SetValue("640");
            SeparateTile.SetValue("2:24");
            BoostbackTile.SetValue("310");
            LandingTile.SetValue("260");
            foreach (var d in _checkDots) d.RemoveFromClassList("ssa-dot--neutral");
            Page.SetTimeline(Phases, 1);
            // The capture shows the mockup's look: the main button enabled (it does nothing, there is no pilot).
            ReturnButton.SetEnabled(true);
        }
    }
}
