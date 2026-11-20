using KTools;

namespace SkySpaceAgency.Bridge
{
    public static class StageBridgeSettings
    {
        static Setting<bool> _enabled;

        /// <summary>Lets the bridge call Redux's StartReplay, CompleteActiveLanding and ReturnToMission. Off by default; nothing calls them yet.</summary>
        public static Setting<bool> Enabled => _enabled ??= new Setting<bool>("stage_bridge_enabled", false);
    }
}
