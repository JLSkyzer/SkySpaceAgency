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

        // UI assets. The main window's uxml is the mod's only Addressables entry; its address is its
        // project path, so it must differ from K2-D2's (Assets/UI/K2D2_UI/K2D2_Window.uxml): two
        // catalogs with one address give each mod whichever window was loaded first.
        public const string UiFolder = "Assets/UI/" + ModId + "_UI/";
        public const string MainWindowUxml = ModId + "_Window.uxml";
        public const string MainWindowAddress = UiFolder + MainWindowUxml;

        // Default shortcut of the main window in flight (rebindable in Redux's mod settings). K2-D2
        // uses Alt+O; Alt+Y is free in flight and on the map (Alt+S, Alt+W... are trim keys, Alt+K
        // is KERNEL, Alt+P is FlightPlanRedux: docs/superpowers/notes/2026-11-19-coexistence-with-k2d2.md).
        public static readonly SpaceWarp2.API.Input.KeyboardShortcut DefaultWindowShortcut =
            new SpaceWarp2.API.Input.KeyboardShortcut(UnityEngine.KeyCode.Y, UnityEngine.KeyCode.LeftAlt);

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
