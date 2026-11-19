using System.Collections.Generic;
using SkySpaceAgency.Controller;
using SkySpaceAgency.OrbitPlanning;
using SsaUI;
using SsaUI.Tabs;
using KSP.UI.Binding;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.UI
{
    /// <summary>
    /// Controller for the SkySpaceAgencyWindow UI.
    /// </summary>
    public class SkySpaceAgencyWindow : MonoBehaviour
    {
        // The PanelRenderer component of the window game object. UitkForKsp2.API.Window.Create(...)
        // in SkySpaceAgencyPlugin.cs returns a PanelRenderer (UIDocument's successor under Redux), which
        // doesn't expose .rootVisualElement directly - the root VisualElement is obtained via
        // RegisterUIReloadCallback instead, which fires once immediately (the UI is already loaded
        // synchronously by the time Create returns) and again on any later live UI reload. Same
        // pattern KerbalAutopilot's MainAppWindow.cs uses for this Redux API.
        private PanelRenderer _panel;

        // The elements of the window that we need to access
        private VisualElement _rootElement;

        // Guards the one-time wiring in OnUiReload against running twice if the callback fires again later
        // (e.g. a live UI reload during development) - re-binding would stack duplicate event handlers.
        private bool _bound;

        // Saves the size picked with Redux's resize handle, and applies it when the window opens.
        private WindowSizeSaver _sizeSaver;

        // The backing field for the IsWindowOpen property
        private bool _isWindowOpen;

        /// <summary>
        /// The state of the window. Setting this value will open or close the window.
        /// </summary>
        public bool IsWindowOpen
        {
            get => _isWindowOpen;
            set
            {
                _isWindowOpen = value;

                // Set the display style of the root element to show or hide the window. Null-conditional
                // because this can in principle be set before OnUiReload has run (it shouldn't happen in
                // practice - see OnEnable - but failing silently beats an NRE if it ever does).
                _rootElement?.Show(value);
                // Alternatively, you can deactivate the window game object to close the window and stop it from updating,
                // which is useful if you perform expensive operations in the window update loop. However, this will also
                // mean you will have to re-register any event handlers on the window elements when re-enabled in OnEnable.
                // gameObject.SetActive(value);

                // Update the Flight AppBar button state
                GameObject.Find(SkySpaceAgencyPlugin.ToolbarFlightButtonID)
                    ?.GetComponent<UIValue_WriteBool_Toggle>()
                    ?.SetValue(value);

                // Update the OAB AppBar button state
                // GameObject.Find(SkySpaceAgencyPlugin.ToolbarOabButtonID)
                //     ?.GetComponent<UIValue_WriteBool_Toggle>()
                //     ?.SetValue(value);
            }
        }

        TabbedPage tab_page;

        List<SsaPage> all_panels = new();

        // Which tab the info button should return to when clicked again from About - see the
        // info_button click handler in OnUiReload below.
        string _lastTabBeforeAbout = "node";

        /// <summary>
        /// Runs when the window is first created, and every time the window is re-enabled. Gets the
        /// PanelRenderer and registers for its UI-ready callback - see the _panel field's comment.
        ///
        /// Unity swallows exceptions thrown inside MonoBehaviour lifecycle methods like OnEnable (it
        /// logs them but doesn't propagate them or stop the caller), so a failed Q&lt;T&gt;() lookup
        /// inside OnUiReload below (e.g. a UXML element name mismatch) would otherwise fail silently
        /// with no obvious error. The logging below exists to surface exactly which lookup failed.
        /// </summary>
        private void OnEnable()
        {
            L.Log("MainWindow.OnEnable running");

            _panel = GetComponent<PanelRenderer>();
            if (_panel == null)
            {
                L.Log("MainWindow.OnEnable: GetComponent<PanelRenderer>() returned null - the window's " +
                      "GameObject doesn't have a PanelRenderer component. This should not happen given how " +
                      "Window.Create() is used in SkySpaceAgencyPlugin.cs - if you see this, something upstream " +
                      "changed how the window GameObject is constructed.");
                return;
            }

            _panel.RegisterUIReloadCallback(OnUiReload);

            // RegisterUIReloadCallback should invoke OnUiReload synchronously here; log explicitly
            // if it didn't, rather than leaving it to be inferred from nothing happening.
            if (_rootElement == null)
            {
                L.Log("MainWindow.OnEnable: RegisterUIReloadCallback did not invoke OnUiReload immediately " +
                      "(_rootElement is still null right after registering). Waiting for a later UI reload " +
                      "to fire it instead.");
            }
        }

        /// <summary>
        /// Fires once immediately when OnEnable registers it (the UI is already loaded synchronously by
        /// then) and again on any later live UI reload. All the wiring that needs the real root
        /// VisualElement lives here instead of OnEnable for that reason.
        /// </summary>
        private void OnUiReload(PanelRenderer panel, VisualElement root)
        {
            L.Log("MainWindow.OnUiReload running");

            // Since we're cloning the UXML tree from a VisualTreeAsset, the actual root element is a TemplateContainer,
            // so we need to get the first child of the TemplateContainer to get our actual root VisualElement.
            _rootElement = root[0];

            IsWindowOpen = false;

            // Only wire up click handlers/callbacks once - this can fire again on a later live UI reload,
            // and re-running the binding below every time would stack duplicate event subscriptions on top
            // of whatever's still attached from the previous load.
            if (_bound) return;
            _bound = true;

            // From here down: every Q<T>() lookup is explicitly null-checked and logged by name before
            // use, rather than trusting it and letting a bad lookup throw an NRE that Unity would
            // otherwise swallow silently (see the OnEnable doc comment above). If the window fails to
            // wire up, whichever element name gets logged here as missing is the one to check against
            // SkySpaceAgency_Window.uxml.

            // Get the close button from the window
            var closeButton = _rootElement.Q<Button>("close-button");
            if (closeButton == null)
            {
                L.Log("MainWindow.OnUiReload: Q<Button>(\"close-button\") returned null - stopping here, " +
                      "the rest of the window's controls will not be wired up.");
                return;
            }
            // Add a click event handler to the close button
            closeButton.clicked += () => IsWindowOpen = false;

            // The window title comes from ModIdentity (the uxml holds a neutral default). AppShell's
            // Title is set by reflection: the type lives in Redux's controls assembly, which this
            // assembly does not reference.
            var shell = _rootElement.Q("MainFrame") ?? _rootElement;
            var titleProperty = shell.GetType().GetProperty("Title");
            if (titleProperty != null && titleProperty.PropertyType == typeof(string))
                titleProperty.SetValue(shell, ModIdentity.Title);
            else
                L.Log($"MainWindow.OnUiReload: {shell.GetType().Name} has no Title property, the window title stays the uxml default.");

            // list all pilot panel
            all_panels.Clear();
            foreach(var pilot in SkySpaceAgencyPlugin.Instance.pilots_manager.pilots)
            {
                var panel_ = pilot.page;
                if (panel_ != null)
                    all_panels.Add(panel_);
            }

            all_panels.Add(new OrbitUI());
            all_panels.Add(new AboutUI());

            tab_page = _rootElement.Q<TabbedPage>();
            if (tab_page == null)
            {
                L.Log("MainWindow.OnUiReload: Q<TabbedPage>() returned null - the custom <k2-ui-tabs--tabbed-page> " +
                      "(or however it's tagged in SkySpaceAgency_Window.uxml) element wasn't found or didn't instantiate " +
                      "as a TabbedPage. Stopping here, the rest of the window's controls will not be wired up.");
                return;
            }
            tab_page.Init(all_panels);
            // save the current_tab to settings
            tab_page.Bind("main_page", "node");

            // Renamed from "title-bar" when the window chrome switched from a hand-built header to
            // UitkForKsp2.Controls.AppShell (which owns its own header - icon/title/close - and has no
            // slot for extra buttons). The settings and staging toggles moved into this new row instead.
            var title_bar = _rootElement.Q("toolbar");
            if (title_bar == null)
            {
                L.Log("MainWindow.OnUiReload: Q(\"toolbar\") returned null - stopping here, the settings/" +
                      "staging toggle buttons will not be wired up.");
                return;
            }

            // The settings gear/page is gone - this is now a plain "i" button that jumps straight to
            // the About tab via TabbedPage.Select(), instead of toggling the old shared
            // GlobalSetting.settings_visible flag. That flag itself is left in place (see
            // GlobalSettings.cs/SsaPage.cs) since every tab's generic onSettingsChanged handler is
            // harmless as long as nothing ever sets it back to true again - not worth the extra risk
            // of ripping out for something that wasn't actually part of this ask.
            var info_button = title_bar.Q<Button>("info-toggle");
            var staging_toggle = title_bar.Q<ToggleButton>("staging-toggle");
            if (info_button != null)
                info_button.tooltip = ModIdentity.AboutButtonTooltip;
            if (info_button == null)
            {
                L.Log("MainWindow.OnUiReload: title_bar.Q<Button>(\"info-toggle\") returned null.");
            }
            if (staging_toggle == null)
            {
                L.Log("MainWindow.OnUiReload: title_bar.Q<ToggleButton>(\"staging-toggle\") returned null.");
            }

            // Toggle, not one-way: clicking it while already on About goes back to whichever tab
            // was open before, instead of leaving the only way out being to click another tab
            // button by hand. "node" as the fallback matches tab_page.Bind's own default below.
            info_button?.RegisterCallback<ClickEvent>(evt =>
            {
                if (tab_page.CurrentTabCode == "about")
                {
                    tab_page.Select(_lastTabBeforeAbout);
                }
                else
                {
                    _lastTabBeforeAbout = tab_page.CurrentTabCode;
                    tab_page.Select("about");
                }
            });
            // This used to write to StagingPilot.Instance.Enabled, which only gates BaseController.isActive
            // (tab visibility/availability) - nothing in StagingPilot.Update()/CheckStaging() ever reads it,
            // so toggling it had no effect on whether auto-staging actually ran. The setting that
            // CheckStaging() actually checks is StagingSettings.auto_staging, which had no UI control at
            // all - binding this already-existing title-bar toggle to it directly is both the fix and the
            // toggle's original apparent intent.
            staging_toggle?.Bind(StagingSettings.auto_staging);

            // Back to the new window (the default since the tab migration): turns the Classic UI setting off.
            var new_ui_button = title_bar.Q<Button>("new-ui-button");
            if (new_ui_button == null)
                L.Log("MainWindow.OnUiReload: title_bar.Q<Button>(\"new-ui-button\") returned null.");
            else
                new_ui_button.clicked += () => SkySpaceAgencyPlugin.Instance?.SwitchUi(false);

            _rootElement.Query<IntegerField>().ForEach(field => field.DisableGameInputOnFocus());
            _rootElement.Query<FloatField>().ForEach(field => field.DisableGameInputOnFocus());
            _rootElement.Query<RepeatButton>().ForEach(field => field.DisableGameInputOnFocus());

            _rootElement.AddManipulator(new DragManipulator(false, "main_window_pos"));

            // Resizing is Redux's own WindowResizeHandle, set up from SkySpaceAgency_Window.uxml. It keeps
            // nothing between sessions: the size is saved here. Minimum as in the uxml.
            var main_frame = _rootElement.Q("MainFrame") ?? _rootElement;
            _sizeSaver = new WindowSizeSaver(main_frame, "main_window_size", "main_window_width", new Vector2(300, 250));

            L.Log("MainWindow.OnUiReload finished wiring successfully");
        }

        void Update()
        {
            // tab_page isn't wired up until OnUiReload has run - guard rather than NRE on an early frame.
            tab_page?.Update();
        }
    }
}
