using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SkySpaceAgency;
using SkySpaceAgency.AppUI;
using UnityEngine;

// Plan 2026-11-19-new-ui-tab-migration: nothing functional is lost. Every persisted settings key of the
// mod (a string literal given to a Setting / ClampSetting / EnumSetting outside the new UI's own code)
// has a control in the new window, i.e. was bound through AppBind when every tab was built. A key that
// has no control on purpose is listed below with its reason.
public class AppSettingsCoverageTests
{
    // Keys without a control in the new window, and why (each checked against the classic window).
    static readonly Dictionary<string, string> NoControl = new()
    {
        // Window state, not a player setting.
        ["main_window_pos"] = "classic window position",
        ["main_window_size"] = "classic window height",
        ["main_window_width"] = "classic window width",
        ["vab_window_pos"] = "VAB window position",
        ["main_page"] = "classic window's current tab",
        ["current_tab"] = "classic tab memory",
        // DronePilot is never added to the pilots (SkySpaceAgencyPlugin.OnInitialized): no classic control either.
        ["drone.show_details"] = "DronePilot, not enabled",
        ["drone.speed_limit"] = "DronePilot, not enabled",
        ["drone.wanted_altitude"] = "DronePilot, not enabled",
        ["drone.wanted_speed"] = "DronePilot, not enabled",
        ["speed.altitude_ratio"] = "DronePilot, not enabled",
        ["speed.kill_h_speed_ratio"] = "DronePilot, not enabled",
        // No control in the classic window either (LandingSettings, kept for the files).
        ["land.max_throttle_rate_per_sec"] = "no classic control",
    };

    // `new Setting<T>("key"`, `new ClampSetting<T>("key"`, `new EnumSetting<T>("key"`, the target-typed
    // `x = new("key", ...)` of the settings classes, and `Renamed("key", "old")`.
    static readonly Regex[] KeyPatterns =
    {
        new Regex(@"new\s+(?:Clamp|Enum)?Setting<[^>]+>\s*\(\s*""([^""]+)"""),
        new Regex(@"(?:Clamp|Enum)?Setting<[^>]+>\s+\w+\s*=\s*new\s*\(\s*""([^""]+)"""),
        new Regex(@"^\s*\w+\s*=\s*new\s*\(\s*""([a-z][a-z0-9_]*\.[A-Za-z0-9_.]+)""", RegexOptions.Multiline),
        new Regex(@"Renamed\(\s*""([^""]+)"""),
    };

    public static SortedSet<string> PersistedKeys()
    {
        string code = Path.Combine(Application.dataPath, ModIdentity.ModId, "Code");
        var keys = new SortedSet<string>();
        foreach (string file in Directory.GetFiles(code, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/AppUI/")) continue;   // the new UI's own keys are its own
            string text = File.ReadAllText(file);
            foreach (var re in KeyPatterns)
                foreach (Match m in re.Matches(text))
                    keys.Add(m.Groups[1].Value);
        }
        return keys;
    }

    [Test]
    public void TheScan_FindsTheKnownKeys()
    {
        var keys = PersistedKeys();
        Assert.Greater(keys.Count, 80, "the scan found too few keys");
        foreach (string k in new[] { "land.auto_warp", "land.touch_down_speed", "attitude.elevation", "staging.auto_staging" })
            CollectionAssert.Contains(keys, k);
    }

    [Test]
    public void EveryPersistedKey_HasAControl()
    {
        AppBind.Bound.Clear();
        using (AppUiHarness.Open("lift", new Rect(AppWindowGeometry.DefaultPosition, AppWindowGeometry.DefaultSize), new Vector2(1920, 1080))) { }
        var missing = PersistedKeys().Where(k => !AppBind.Bound.Contains(k) && !NoControl.ContainsKey(k)).ToList();
        Assert.IsEmpty(missing, $"{missing.Count} persisted key(s) without a control in the new window:\n" + string.Join("\n", missing));
    }
}
