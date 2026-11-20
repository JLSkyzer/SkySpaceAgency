using System;
using System.Collections.Generic;
using KTools;
using SkySpaceAgency.Bridge;
using SkySpaceAgency.Controller;
using SkySpaceAgency.Node;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// SETTINGS (docs/design/ui/settings.png): a list of categories on the left, the chosen category's
    /// settings in the middle, the About card on the right. Holds every setting outside the flight tabs
    /// (Reset All and Debug Mode, the Auto-staging toggle), the window's own settings (interface scale,
    /// small-window mode, compact flight bar), the burn warp mode (also in NODES > ADVANCED) and the shortcut. Inventory:
    /// docs/superpowers/notes/new-ui-inventory/settings.md.
    /// </summary>
    public class SettingsTab : AppTab
    {
        public SettingsTab(AppTabContext context) : base(context) { }
        public override string Code => "settings";
        public override string Label => "Settings";
        public override bool RightAligned => true;

        /// <summary>Seconds the Reset button waits for its confirming second click.</summary>
        public const float ResetConfirmSeconds = 4f;
        public const string ResetLabel = "Reset";
        public const string ResetConfirmLabel = "Reset all?";

        readonly Dictionary<string, VisualElement> _categoryItems = new();
        readonly Dictionary<string, AppCard> _panels = new();

        public string Category { get; private set; }
        public IReadOnlyDictionary<string, AppCard> Panels => _panels;

        public AppSlider ScaleSlider { get; private set; }
        public AppButton ResetButton { get; private set; }
        public AppButton DoneButton { get; private set; }
        public Label VersionLabel { get; private set; }
        public Label ShortcutLabel { get; private set; }
        public AppNote WarpNote { get; private set; }
        public AppSwitch StageBridgeSwitch { get; private set; }
        public AppButton StageBridgeButton { get; private set; }

        float _resetArmedUntil = -1;
        string _previousTab;
        AppWindowView _window;

        protected override void OnBuild(AppPage page)
        {
            page.AddToClassList("ssa-settings");

            var categories = new AppCard("Categories");
            foreach (var (code, label) in SettingsText.Categories)
            {
                var item = Ui.Box("ssa-category");
                item.name = "category-" + code;
                item.Add(Ui.Label(label, "ssa-category__label"));
                string c = code;
                item.AddManipulator(new Clickable(() => SelectCategory(c)));
                categories.Add(item);
                _categoryItems[code] = item;
            }
            page.Plan.Add(categories);

            AddPanel(page, "general", "General", General);
            AddPanel(page, "interface", "Interface", Interface);
            AddPanel(page, "staging", "Staging", Staging);
            AddPanel(page, "warp", "Warp", Warp);
            AddPanel(page, "logs", "Logs", Logs);
            AddPanel(page, "about", "About", AboutPanel);
            SelectCategory("general");

            page.Side.Add(AboutCard());

            page.Actions.SetNote("Changes apply at once; pilots read them at their next phase");
            ResetButton = page.Actions.AddButton(ResetLabel, AppButton.Kind.Plain, OnReset);
            DoneButton = page.Actions.AddButton("Done", AppButton.Kind.Main, OnDone);

            // Done goes back to the tab shown before Settings (the window is found once it is attached).
            page.RegisterCallback<AttachToPanelEvent>(_ => HookWindow());
        }

        void AddPanel(AppPage page, string code, string title, Action<AppCard> fill)
        {
            var card = new AppCard(title);
            card.name = "settings-" + code;
            card.AddToClassList("ssa-settings__panel");
            fill(card);
            page.View.Add(card);
            _panels[code] = card;
        }

        /// <summary>Shows one category's panel and lights its item (unknown code: nothing changes).</summary>
        public bool SelectCategory(string code)
        {
            if (code == null || !_panels.ContainsKey(code)) return false;
            Category = code;
            foreach (var kv in _panels)
                kv.Value.style.display = kv.Key == code ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var kv in _categoryItems)
                kv.Value.EnableInClassList("ssa-category--active", kv.Key == code);
            return true;
        }

        // ---- the categories ----

        void General(AppCard card)
        {
            ShortcutLabel = Ui.Label(ShortcutText(), "ssa-settings-key__text");
            var key = Ui.Box("ssa-settings-key");
            key.Add(ShortcutLabel);
            card.Add(Row("Open the window", key));
            card.Add(new AppNote("Opens and closes this window in flight and on the map. Rebind it in Redux's mod settings "
                + "(this mod's Space Warp configuration, section Keybinds). The app bar button opens it too."));
            card.Add(AppBind.Switch("Compact flight bar while a pilot runs", AppSettings.CompactBar));
            card.Add(new AppNote("While a pilot flies and this window is closed, a small bar shows its phase, its readouts and a Stop button."));
        }

        void Interface(AppCard card)
        {
            var scale = AppSettings.Scale;
            ScaleSlider = new AppSlider("", UiScale.Min * 100, UiScale.Max * 100, UiScale.Clamp(scale.V) * 100,
                v => UiScale.Percent(v / 100));
            ScaleSlider.name = "interface-scale";
            AppBind.Record(scale.key);
            ScaleSlider.changed += SetScalePercent;
            scale.listeners += v => ScaleSlider.SetValueWithoutNotify(UiScale.Clamp(v) * 100);
            card.Add(Row("Interface scale", ScaleSlider));
            card.Add(new AppNote("Multiplies the game's UI Scale, for this window and the compact flight bar."));

            card.Add(Row("Small window", AppBind.Segmented(AppSettings.SmallWindow,
                new List<SmallWindowMode> { SmallWindowMode.Scale, SmallWindowMode.Reflow },
                new List<string> { "Scale", "Reflow" })));
            card.Add(new AppNote("When the window is smaller than the layout of the mockups: Scale keeps the three columns, the timeline "
                + "and the action bar and shrinks everything to fit, with no scrolling (down to half size); Reflow re-flows the columns "
                + "(the view goes under the plan and the telemetry below 1100 px wide) and the page scrolls."));
        }

        /// <summary>What the scale slider does: snaps to 5 %, then applies through the window (or the setting alone in the editor).</summary>
        public void SetScalePercent(float percent)
        {
            float s = UiScale.Clamp(percent / 100);
            ScaleSlider.SetValueWithoutNotify(s * 100);
            if (Context.SetScale != null) Context.SetScale(s);
            else AppSettings.Scale.V = s;
        }

        void Staging(AppCard card)
        {
            card.Add(AppBind.Switch("Auto staging", StagingSettings.auto_staging));
            card.Add(new AppNote("Fires the next stage as soon as a lit engine runs out of propellant (the classic window's title bar toggle). "
                + "A landing with a chosen stage suspends it and drops its stages itself."));
            card.Add(AppBind.Field("Freeze the pilots after staging", StagingSettings.freeze_duration, "s"));
            card.Add(new AppNote("Staging counts as in progress this long (game seconds) after each stage, and the pilots wait for it."));
        }

        void Warp(AppCard card)
        {
            var values = new List<BurnWarpMode> { BurnWarpMode.Off, BurnWarpMode.Physics, BurnWarpMode.Rails };
            var labels = new List<string>(BurnWarpModes.Labels.Split(';'));
            while (labels.Count < values.Count) labels.Add(values[labels.Count].ToString());
            card.Add(Row("Warp during burns", AppBind.Dropdown("", BurnWarpSettings.mode, values, labels)));
            WarpNote = new AppNote();
            card.Add(WarpNote);
            WarpNote.Set(WarpText(BurnWarpSettings.mode.V));
            BurnWarpSettings.mode.listeners += v => WarpNote.Set(WarpText(v));
            card.Add(new AppNote("Used by every closed-loop burn (Node, Orbit, Transfer, Rendezvous). Also in NODES > ADVANCED."));
        }

        static string WarpText(BurnWarpMode m) => "Now: " + BurnWarpModes.Describe(m) + ".";

        void Logs(AppCard card)
        {
            card.Add(AppBind.Switch("Debug mode", SkySpaceAgencySettings.debug_mode));
            card.Add(new AppNote("Turns on the pilots kept for debugging and the pilots' extra diagnostics (more lines in the "
                + "game's Player.log, debug readouts). The pilots' usual log lines are always written."));

            // The bridge to Redux's Stage Recovery (plan 2026-11-20-stage-recovery-bridge): invisible work for the booster return.
            StageBridgeSwitch = AppBind.Switch("Stage bridge: allow the calls", StageBridgeSettings.Enabled);
            card.Add(StageBridgeSwitch);
            card.Add(new AppNote("Work in progress for the booster return. Off: nothing calls Redux's stage recovery. The button below only reads "
                + "Redux's state and the mod's own state and writes them to Player.log ([StageBridge] lines)."));
            StageBridgeButton = new AppButton("Stage bridge: log state", AppButton.Kind.Plain, () => StageBridge.LogState("button"));
            card.Add(StageBridgeButton);
        }

        void AboutPanel(AppCard card)
        {
            card.Add(Ui.Label(ModIdentity.AboutTitle, "ssa-settings-about__title"));
            card.Add(Ui.Label("CREDITS", "ssa-section-title"));
            foreach (var (who, what) in SettingsText.Credits)
            {
                var row = Ui.Box("ssa-settings-credit");
                row.Add(Ui.Label(who, "ssa-settings-credit__who"));
                row.Add(Ui.Label(what, "ssa-settings-credit__what"));
                card.Add(row);
            }
            card.Add(Ui.Label("THANKS", "ssa-section-title"));
            var thanks = Ui.Label(SettingsText.Thanks, "ssa-settings-thanks");
            thanks.enableRichText = true;
            card.Add(thanks);
            card.Add(new AppNote(SettingsText.Licence + ": every change stays under the same licence, and the credits above are never removed. "
                + "Reset (bottom right, click twice) puts every setting of the mod back to its default, the Landing profiles included."));
        }

        AppCard AboutCard()
        {
            var about = new AppCard("About");
            about.AddToClassList("ssa-settings-aboutcard");
            var logo = new AppLogo();
            logo.AddToClassList("ssa-settings-logo");
            about.Add(logo);
            about.Add(Ui.Label(ModIdentity.Title, "ssa-about__name"));
            VersionLabel = Ui.Label(VersionText(Context), "ssa-about__line");
            VersionLabel.AddToClassList("ssa-settings-version");
            about.Add(VersionLabel);
            about.Add(Ui.Label(SettingsText.Licence, "ssa-about__licence"));
            about.Add(Ui.Label("Built on K2-D2 by Christophe Floutier", "ssa-about__line"));
            about.Add(Ui.Label("and its Redux port by IanMealworm", "ssa-about__line"));
            var more = Ui.Label("All credits: About", "ssa-about__line");
            more.AddToClassList("ssa-settings-link");
            more.AddManipulator(new Clickable(() => SelectCategory("about")));
            about.Add(more);
            about.Add(Ui.Box("ssa-grow"));
            about.Add(new AppPill(StatusKind.Info, "Landing settings are saved per profile (vacuum / atmosphere)"));
            return about;
        }

        static VisualElement Row(string label, VisualElement control)
        {
            var row = Ui.Box("ssa-setting");
            row.Add(Ui.Label(label, "ssa-setting__label"));
            control.AddToClassList("ssa-setting__control");
            row.Add(control);
            return row;
        }

        /// <summary>The plugin's version (Space Warp metadata) in game, a development note in the editor.</summary>
        public static string VersionText(AppTabContext context)
        {
            try
            {
                if (context?.Plugin != null)
                {
                    string v = context.Plugin.SWMetadata.SWInfo.Version;
                    if (!string.IsNullOrEmpty(v)) return "version " + v;
                }
            }
            catch (Exception) { }
            return "development build · editor";
        }

        public static string ShortcutText()
        {
            string s = null;
            try { s = SettingsText.Shortcut(ModIdentity.DefaultWindowShortcut.ToString()); }
            catch (Exception) { }
            return s ?? "the default shortcut";
        }

        // ---- actions ----

        public bool ResetArmed => _resetArmedUntil >= 0;

        /// <summary>First click arms, the second (within ResetConfirmSeconds) resets, as the classic About's Reset All.</summary>
        void OnReset()
        {
            float now = Time.realtimeSinceStartup;
            if (!ResetArmed || now > _resetArmedUntil)
            {
                _resetArmedUntil = now + ResetConfirmSeconds;
                ResetButton.Text = ResetConfirmLabel;
                Page.Actions.SetNote("Click Reset again to put every setting back to its default (Landing profiles included)");
                return;
            }
            Disarm();
            ResetAll();
            Page.Actions.SetNote("Every setting is back to its default");
        }

        void Disarm()
        {
            _resetArmedUntil = -1;
            if (ResetButton != null) ResetButton.Text = ResetLabel;
        }

        /// <summary>The classic About tab's Reset All: the main settings file and both Landing profiles.</summary>
        public static void ResetAll()
        {
            SettingsFile.Instance.Reset();
            SettingsFile.Get("land_atmo")?.Reset();
            SettingsFile.Get("land_vac")?.Reset();
        }

        void HookWindow()
        {
            if (_window != null) return;
            _window = Page?.GetFirstAncestorOfType<AppWindowView>();
            if (_window == null) return;
            _window.Tabs.selected += code => { if (code != Code) _previousTab = code; else Disarm(); };
        }

        /// <summary>The tab Done goes back to: the one shown before Settings, else the first flight tab.</summary>
        public string DoneTarget
        {
            get
            {
                if (!string.IsNullOrEmpty(_previousTab)) return _previousTab;
                if (_window != null)
                    foreach (var t in _window.TabList)
                        if (t.Code != Code) return t.Code;
                return null;
            }
        }

        void OnDone()
        {
            HookWindow();
            Disarm();
            string target = DoneTarget;
            if (_window != null && target != null) _window.Select(target);
        }

        public override void Tick()
        {
            if (ResetArmed && Time.realtimeSinceStartup > _resetArmedUntil)
            {
                Disarm();
                Page.Actions.SetNote("Changes apply at once; pilots read them at their next phase");
            }
        }

        public override void ShowSample()
        {
            SelectCategory("general");
        }
    }
}
