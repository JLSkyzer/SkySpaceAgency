using System;
using System.Collections.Generic;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The texts of the Settings tab that come from code or from the credits (pure, tested): the
    /// shortcut as the mockup writes it, and the credits of the classic About tab, README and NOTICE.
    /// A credit is never removed from here (CC BY-SA 4.0 attribution).
    /// </summary>
    public static class SettingsText
    {
        /// <summary>The categories of the left list, in order (codes are the panels' names).</summary>
        public static readonly (string code, string label)[] Categories =
        {
            ("general", "General"),
            ("interface", "Interface"),
            ("staging", "Staging"),
            ("warp", "Warp"),
            ("logs", "Logs"),
            ("about", "About"),
        };

        /// <summary>
        /// "Alt + Y" from a shortcut's text ("Y + LeftAlt", the main key first, then the modifiers).
        /// Modifiers come first, left/right variants lose their side. Empty or a type name: null.
        /// </summary>
        public static string Shortcut(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.IndexOf("Shortcut", StringComparison.Ordinal) >= 0)
                return null;
            var keys = new List<string>();
            var mods = new List<string>();
            foreach (string part in raw.Split('+'))
            {
                string k = part.Trim();
                if (k.Length == 0) continue;
                string mod = Modifier(k);
                if (mod != null) { if (!mods.Contains(mod)) mods.Add(mod); }
                else keys.Add(KeyName(k));
            }
            if (keys.Count == 0 && mods.Count == 0) return null;
            mods.AddRange(keys);
            return string.Join(" + ", mods);
        }

        static string Modifier(string k)
        {
            switch (k)
            {
                case "LeftAlt": case "RightAlt": case "AltGr": case "Alt": return "Alt";
                case "LeftControl": case "RightControl": case "Control": case "Ctrl": return "Ctrl";
                case "LeftShift": case "RightShift": case "Shift": return "Shift";
                case "LeftCommand": case "RightCommand": case "LeftApple": case "RightApple": return "Cmd";
                default: return null;
            }
        }

        static string KeyName(string k)
        {
            if (k.StartsWith("Alpha", StringComparison.Ordinal) && k.Length == 6) return k.Substring(5);
            if (k.StartsWith("Keypad", StringComparison.Ordinal) && k.Length > 6) return "Num " + k.Substring(6);
            return k;
        }

        /// <summary>The credits (README, NOTICE, classic About): who, and for what.</summary>
        public static readonly (string who, string what)[] Credits =
        {
            ("Christophe Floutier (cfloutier)", "the original K2-D2 mod, the droid astropilot, for SpaceWarp1"),
            ("@IanMealworm", "the Redux port (K2D2Redux), the base of this remix, and K2-D2 1.4.0 / 1.4.1 ported here"),
            ("LeoMarinDev", "the much smaller download (K2D2Redux PR #2) and the window drag fixes (K2-D2 1.4.x)"),
            ("Lexi (KSP2Community/K2D2Redux)", "the initial Redux port, re-targeted at Redux's API"),
            ("Mole", "the original Circularize work"),
            ("@schlosrat", "original testing and code help, especially node creation; author of Flight Plan"),
            ("Opus", "named the mod"),
            ("cheese3660", "SpaceWarp and AutoBurn, which the original mod was built on"),
            ("Halbann", "LazyOrbit, which the original mod's first steps were based on"),
            ("KSP2Community", "the Redux modding framework and the Redux.Template project scaffold"),
        };

        /// <summary>The classic About tab's thanks, word for word (rich text).</summary>
        public const string Thanks = @"Thanks to <b>KSP2 Modding Society</b> for the huge help and support.

Especially <b>@Munix</b> for the UITK for KSP2 mod and It's great project template.
<b>@schlosrat</b> for the Flight Plan Mod and for the communicative enthusiasm.
<b>@DunaColonist</b> for it's HUD display that help me a lot for debugging dock pilot.
and <b>@Cheese</b> of course for creating the Modding society itself.";

        public const string Licence = "CC BY-SA 4.0";
    }
}
