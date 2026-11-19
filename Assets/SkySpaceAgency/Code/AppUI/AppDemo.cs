using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The pages of the new window's preview (step 1 of the Design plan). LIFT reproduces
    /// docs/design/ui/lift.png with static data; the other flight tabs say they come in step 2; SETTINGS
    /// holds the real interface scale. Nothing here flies: the pilots stay in the classic window.
    /// </summary>
    public static class AppDemo
    {
        public sealed class Hooks
        {
            public float Scale = UiScale.Default;
            public Action<float> SetScale;
            public Action OpenClassicWindow;
        }

        public static readonly string[] LiftPhases = { "Launch window", "Vertical climb", "Gravity turn", "Coast to Ap", "Circularize", "In orbit" };

        // Tabs of the mockups, in their order. Step 2 replaces each placeholder by the migrated tab.
        public static readonly (string code, string label)[] FlightTabs =
        {
            ("lift", "Lift"), ("orbit", "Orbit"), ("transfer", "Transfer"), ("rendezvous", "Rendezvous"),
            ("land", "Land"), ("return", "Return"), ("nodes", "Nodes"),
        };

        public static void Build(AppWindowView view, Hooks hooks)
        {
            hooks ??= new Hooks();
            view.Header.AddChip("", "Kerbal Explorer II", StatusKind.Ok);
            view.Header.AddChip("Kerbin ·", "Pad");
            view.Header.AddChip("Δv", "4 812 m/s");
            view.Header.AddChip("TWR", "1.46");
            view.Tabs.SetBadge("Preview");

            foreach (var (code, label) in FlightTabs)
                view.AddPage(code, label, code == "lift" ? Lift() : Placeholder(code, label, hooks));
            view.AddPage("settings", "Settings", Settings(hooks), right: true);
        }

        // ---- LIFT (docs/design/ui/lift.svg) ----

        public static AppPage Lift()
        {
            var page = new AppPage("lift");

            var plan = new AppCard("Mission plan");
            plan.Add(new AppSegmented(new[] { "Orbit", "Transfer", "Rendezvous" }, 0));
            var fields = Ui.Box("ssa-fields");
            fields.Add(new AppField("Target altitude", 100, "km", "0"));
            var inc = new AppField("Inclination", 0, "°", "0.0");
            inc.AddToClassList("ssa-field--last");
            fields.Add(inc);
            plan.Add(fields);
            plan.Add(new AppSlider("Gravity turn start", 0, 4.4f, 1.2f, v => Ui.Num(v, "0.0") + " km"));
            plan.Add(new AppSlider("Turn shape", 0, 100, 40, v => Ui.Num(v, "0") + " %"));
            plan.Add(new AppSlider("Max angle of attack", 0, 25, 5, v => Ui.Num(v, "0") + "°"));
            var gap = Ui.Box();
            gap.style.height = 14;
            plan.Add(gap);
            plan.Add(new AppSwitch("Auto staging", true));
            plan.Add(new AppSwitch("Warp to launch window", true));
            plan.Add(new AppSwitch("Plan booster return", false));
            page.Plan.Add(plan);

            var profile = new AppCard("Ascent profile", "altitude / downrange", sky: true);
            profile.Add(new AscentProfileView());
            page.View.Add(profile);

            var telemetry = new AppCard("Telemetry");
            var tiles = Ui.Box("ssa-tiles");
            tiles.Add(new AppTile("Altitude", "18.4", "km"));
            tiles.Add(new AppTile("Speed", "742", "m/s"));
            tiles.Add(new AppTile("Apoapsis", "41.2", "km", AppTile.Kind.Gold));
            tiles.Add(new AppTile("Time to Ap", "0:52"));
            telemetry.Add(tiles);
            telemetry.Add(Ui.Label("STAGES · Δv", "ssa-section-title"));
            telemetry.Add(new AppStageBar("S1 booster", "1 950 m/s", 190f / 336f, active: true));
            telemetry.Add(new AppStageBar("S2 upper", "2 300 m/s", 1f));
            telemetry.Add(new AppStageBar("S3 lander", "562 m/s", 82f / 336f));
            var push = Ui.Box("ssa-grow");
            telemetry.Add(push);
            telemetry.Add(new AppPill(StatusKind.Ok, "Gravity turn on track · AoA 2.1°"));
            page.Side.Add(telemetry);

            page.SetTimeline(LiftPhases, 2);
            page.Actions.SetNote("Next: coast at 41 km · stage 1 dry in 0:38");
            page.Actions.AddButton("Pause", AppButton.Kind.Plain);
            page.Actions.AddButton("Abort", AppButton.Kind.Abort);
            page.Actions.AddButton("Running…", AppButton.Kind.Main);
            return page;
        }

        // ---- Tabs still in the classic window ----

        static AppPage Placeholder(string code, string label, Hooks hooks)
        {
            var page = new AppPage(code);
            var plan = new AppCard(label);
            plan.Add(new AppPill(StatusKind.Info, label + " moves to this window in step 2 of the new interface."));
            plan.Add(Paragraph("Until then it flies from the classic window, which is unchanged: open it with Alt+Y or the app bar button."));
            page.Plan.Add(plan);

            var viewCard = new AppCard(label + " view", "coming in step 2", sky: true);
            page.View.Add(viewCard);

            var side = new AppCard("Status pills");
            side.Add(new AppPill(StatusKind.Ok, "OK: the pilot is on track"));
            side.Add(new AppPill(StatusKind.Info, "Info: a node or a plan step"));
            side.Add(new AppPill(StatusKind.Alert, "Alert: the pilot needs you"));
            side.Add(new AppPill(StatusKind.Neutral, "Neutral: waiting"));
            page.Side.Add(side);

            page.Actions.SetNote("This preview does not fly anything");
            page.Actions.AddButton("Classic window", AppButton.Kind.Plain, () => hooks.OpenClassicWindow?.Invoke());
            return page;
        }

        // ---- SETTINGS (docs/design/ui/settings.svg) ----

        public static AppPage Settings(Hooks hooks)
        {
            var page = new AppPage("settings");
            page.Plan.style.width = 255;
            page.Plan.style.minWidth = 220;
            var categories = new AppCard("Categories");
            var general = Ui.Box("ssa-category", "ssa-category--active");
            general.Add(Ui.Label("General", "ssa-category__label"));
            categories.Add(general);
            categories.Add(Paragraph("The other categories of the mockup (pilots, warp, recording, logs) come with the tabs that use them."));
            page.Plan.Add(categories);

            var settings = new AppCard("General");
            settings.Add(SettingRow("Open the window", Ui.Label("Alt + Y: the classic window (rebind it in Redux's mod settings)", "ssa-setting__text")));
            var scale = new AppSlider("", UiScale.Min * 100, UiScale.Max * 100, UiScale.Clamp(hooks.Scale) * 100,
                v => UiScale.Percent(v / 100));
            scale.name = "interface-scale";
            scale.changed += v =>
            {
                float s = UiScale.Clamp(v / 100);
                scale.Input.SetValueWithoutNotify(s * 100);
                hooks.Scale = s;
                hooks.SetScale?.Invoke(s);
            };
            settings.Add(SettingRow("Interface scale", scale));
            settings.Add(Paragraph("Multiplies the game's UI Scale, for this window only. The classic window follows the game's UI Scale."));
            page.View.Add(settings);

            var about = new AppCard("About");
            var logo = new AppLogo();
            logo.style.width = 108;
            logo.style.height = 108;
            logo.style.alignSelf = Align.Center;
            logo.style.marginTop = 8;
            about.Add(logo);
            about.Add(Ui.Label(ModIdentity.Title, "ssa-about__name"));
            about.Add(Ui.Label("new interface · preview", "ssa-about__line"));
            about.Add(Ui.Label("CC BY-SA 4.0", "ssa-about__licence"));
            about.Add(Ui.Label("Built on K2-D2 by Christophe Floutier", "ssa-about__line"));
            about.Add(Ui.Label("and its Redux port by IanMealworm", "ssa-about__line"));
            page.Side.Add(about);

            page.Actions.SetNote("Changes apply at once");
            page.Actions.AddButton("Reset", AppButton.Kind.Plain, () => scale.Value = 100);
            return page;
        }

        static Label Paragraph(string text) => Ui.Label(text, "ssa-paragraph");

        static VisualElement SettingRow(string label, VisualElement control)
        {
            var row = Ui.Box("ssa-setting");
            row.Add(Ui.Label(label, "ssa-setting__label"));
            control.AddToClassList("ssa-setting__control");
            row.Add(control);
            return row;
        }
    }
}
