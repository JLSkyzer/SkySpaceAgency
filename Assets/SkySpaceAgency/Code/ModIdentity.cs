using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace SkySpaceAgency
{
    /// <summary>
    /// Every literal that names this mod: title, ids, settings file names, logger names, VAB file
    /// formats. Nothing else in the code or the uxml may spell the mod's name
    /// (test IdentityLiterals_OnlyInModIdentity), so switching between the SkySpaceAgency and the
    /// K2-D2 flavours changes this file only (plus the mod asset and the generated files).
    /// Settings keys are not identity: they stay as they are in both flavours.
    /// </summary>
    public static class ModIdentity
    {
        /// <summary>False in SkySpaceAgency, true on the K2-D2 flavour (branch pr/k2d2).</summary>
        public const bool IsK2D2 = false;

        public const string Title = "SkySpaceAgency";
        public const string ModId = "SkySpaceAgency";

        // Settings files, next to the mod's DLL. Their content (keys) is the same in both flavours.
        public const string SettingsPrefix = "skyspaceagency_";
        public const string SettingsFileName = SettingsPrefix + "settings.json";
        public const string LandingAtmoFileName = SettingsPrefix + "landing_atmo.json";
        public const string LandingVacFileName = SettingsPrefix + "landing_vac.json";

        // Windows and AppBar buttons.
        public const string WindowId = "SkySpaceAgency";
        public const string VabWindowId = "SkySpaceAgencyVabTools";
        public const string VabWindowTitle = Title + " — VAB tools";
        public const string ToolbarButtonPrefix = "BTN-SkySpaceAgency";
        public const string ToolbarFlightButtonId = ToolbarButtonPrefix + "Flight";
        public const string ToolbarOabButtonId = ToolbarButtonPrefix + "OAB";
        public const string ToolbarKscButtonId = ToolbarButtonPrefix + "KSC";

        // Texts shown in the main window, set from code (the uxml holds neutral defaults).
        public const string AboutTitle = Title + ". Based on K2-D2, the droid astropilot.";
        public const string AboutButtonTooltip = "About " + Title;

        // Logs: Space Warp loggers are named "<LogPrefix>.<area>".
        public const string LogPrefix = "SkySpaceAgency";
        public static string LoggerName(string area) => LogPrefix + "." + area;
        public static ILogger Logger(string area) => SpaceWarp2.API.Logging.LogManager.GetLogger(LoggerName(area));

        // VAB files: "<prefix>.VabExport" and "<prefix>.PartsDump". Files written by the other
        // flavour are read too (both mods share the format, only the prefix differs).
        public const string VabFormatPrefix = "SkySpaceAgency";
        public const string OtherFlavourVabFormatPrefix = "K2D2";
        public static readonly string[] AcceptedVabFormatPrefixes = { VabFormatPrefix, OtherFlavourVabFormatPrefix };

        /// <summary>True when <paramref name="format"/> is "&lt;prefix&gt;.&lt;kind&gt;" for either flavour's prefix.</summary>
        public static bool IsVabFormat(string format, string kind)
        {
            if (format == null) return false;
            foreach (string prefix in AcceptedVabFormatPrefixes)
                if (format == prefix + "." + kind)
                    return true;
            return false;
        }
    }
}
