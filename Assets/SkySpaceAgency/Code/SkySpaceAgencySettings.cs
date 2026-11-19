using KTools;

namespace SkySpaceAgency
{
    public class SkySpaceAgencySettings
    {
        public static Setting<bool> debug_mode = new Setting<bool>("debug_mode", false);

        public static Setting<string> current_tab = new Setting<string>("current_tab", "");

        // empty path will not be saved 
        // but the setting can be binded with VisualElement
        public static Setting<bool> settings_visible = new Setting<bool>("", false);
    }
}
