

using SsaUI;
using SsaUI.Tabs;
using KTools;
using UnityEngine.UIElements;




namespace SkySpaceAgency.UI
{
    public class AboutUI : SsaPage
    {
        public AboutUI()
        {
            code = "about";
        }

        public override bool onInit()
        {
            // About no longer gets its own tab button in the bar - it's reached only via the new
            // info button (SkySpaceAgencyWindow.cs: tab_page.Select("about")), same mechanism TabbedPage
            // already exposes for switching tabs directly regardless of button visibility. Setting
            // enabled=false here hides the button the same way the old
            // GlobalSetting.settings_visible-driven onSettingsChanged used to, just unconditionally
            // instead of toggling with a shared flag nothing sets to true anymore.
            enabled = false;

            // The mod's name comes from ModIdentity; the uxml holds a neutral default.
            Label label = panel.Q<Label>("Title");
            if (label != null)
                label.text = ModIdentity.AboutTitle;

            label = panel.Q<Label>("version");
            label.text = SkySpaceAgencyPlugin.Instance.SWMetadata.SWInfo.Version;

            label = panel.Q<Label>("thanks");
            label.text = @"Thanks to <b>KSP2 Modding Society</b> for the huge help and support. 

Especially <b>@Munix</b> for the UITK for KSP2 mod and It's great project template.
<b>@schlosrat</b> for the Flight Plan Mod and for the communicative enthusiasm.
<b>@DunaColonist</b> for it's HUD display that help me a lot for debugging dock pilot.
and <b>@Cheese</b> of course for creating the Modding society itself.";
    
            panel.Q<Button>("reset_all").listenClick(() =>
            {
                // The Landing profiles keep their settings in files of their own (K2-D2 1.4.0).
                SettingsFile.Instance.Reset();
                SettingsFile.Get("land_atmo")?.Reset();
                SettingsFile.Get("land_vac")?.Reset();
            });
            panel.Q<ToggleButton>("debug").Bind(SkySpaceAgencySettings.debug_mode);

            return true;
        }
    }
}