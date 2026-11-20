
using KTools;
// using KTools.UI;
using UnityEngine.UIElements;

namespace SkySpaceAgency.Node
{
    public class NodeExSettings
    {
        public Setting<bool> auto_warp = new Setting<bool>("node_ex.auto_warp", true);

        public Setting<bool> pause_on_end = new Setting<bool>("node_ex.pause_on_end", false);

        public enum StartMode { precise, constant, half_duration }
        private static string[] StartMode_Labels = { "T0", "before", "mid-duration" };

        public EnumSetting<StartMode> start_mode = new EnumSetting<StartMode>("node_ex.start_mode", StartMode.precise);

        public Setting<float> start_before = new Setting<float>("node_ex.start_before", 1);
    }
}

