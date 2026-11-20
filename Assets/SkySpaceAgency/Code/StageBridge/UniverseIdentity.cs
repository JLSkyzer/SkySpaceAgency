using System;
using System.Collections.Generic;
using System.Globalization;

namespace SkySpaceAgency.Bridge
{
    /// <summary>
    /// What identifies the mod's world at one moment: object ids (RuntimeHelpers.GetHashCode; 0 = missing),
    /// the pilots, the active vessel, counts. Taken before and after a universe reset to see what survives.
    /// Pure data: UniverseIdentityReader fills it from the game.
    /// </summary>
    public sealed class UniverseIdentity
    {
        public int Plugin, PilotsManager, Game, Universe, Simulation, Messages;
        // Weak references to the same objects: an id is a hash code, which a new object could share with a
        // collected one. With them the comparison tells "same object" from "same hash". Null in the tests.
        public WeakReference PluginRef, PilotsManagerRef, GameRef, UniverseRef, SimulationRef, MessagesRef;
        public int PilotCount, ActivePartCount, VesselCount, PluginCount;
        IReadOnlyList<string> _running = Array.Empty<string>();
        public string ActiveVesselGuid, ActiveVesselName, GameState, Phase;
        public double UniverseTime;
        public bool Paused, HasWindow;

        /// <summary>Names of the pilots that were running. Never null.</summary>
        public IReadOnlyList<string> RunningPilots
        {
            get => _running;
            set => _running = value ?? Array.Empty<string>();
        }
    }

    public static class UniverseComparison
    {
        public static IReadOnlyList<string> Compare(UniverseIdentity before, UniverseIdentity after)
        {
            var lines = new List<string>();
            if (before == null)
            {
                lines.Add("no before snapshot (no reset seen before this load)");
                return lines;
            }
            if (after == null)
            {
                lines.Add("no after snapshot");
                return lines;
            }
            Obj(lines, "plugin", before.Plugin, after.Plugin, before.PluginRef, after.PluginRef);
            Obj(lines, "pilots manager", before.PilotsManager, after.PilotsManager, before.PilotsManagerRef, after.PilotsManagerRef);
            Obj(lines, "game instance", before.Game, after.Game, before.GameRef, after.GameRef);
            Obj(lines, "universe model", before.Universe, after.Universe, before.UniverseRef, after.UniverseRef);
            Obj(lines, "space simulation", before.Simulation, after.Simulation, before.SimulationRef, after.SimulationRef);
            Obj(lines, "message center", before.Messages, after.Messages, before.MessagesRef, after.MessagesRef);
            lines.Add("window " + (before.HasWindow == after.HasWindow ? "same" : $"changed {before.HasWindow} -> {after.HasWindow}"));
            lines.Add($"plugin objects in the scene {before.PluginCount} -> {after.PluginCount}");
            lines.Add(before.ActiveVesselGuid == after.ActiveVesselGuid
                ? $"active vessel guid same ({Show(after.ActiveVesselGuid)})"
                : $"active vessel guid changed {Show(before.ActiveVesselGuid)} -> {Show(after.ActiveVesselGuid)}");
            lines.Add(before.ActiveVesselName == after.ActiveVesselName
                ? $"active vessel name same ({Show(after.ActiveVesselName)})"
                : $"active vessel name changed {Show(before.ActiveVesselName)} -> {Show(after.ActiveVesselName)}");
            lines.Add($"vessels {before.VesselCount} -> {after.VesselCount}");
            lines.Add($"active vessel parts {before.ActivePartCount} -> {after.ActivePartCount}");
            lines.Add($"pilots {before.PilotCount} -> {after.PilotCount}, running [{string.Join(", ", before.RunningPilots)}] -> [{string.Join(", ", after.RunningPilots)}]");
            lines.Add($"game state {Show(before.GameState)} -> {Show(after.GameState)}, phase {Show(before.Phase)} -> {Show(after.Phase)}");
            lines.Add(string.Format(CultureInfo.InvariantCulture, "universe time {0:0.##} -> {1:0.##}", before.UniverseTime, after.UniverseTime));
            return lines;
        }

        static void Obj(List<string> lines, string what, int before, int after, WeakReference beforeRef, WeakReference afterRef)
        {
            if (before == 0 && after == 0) lines.Add(what + " missing before and after");
            else if (before != after) lines.Add($"{what} NEW ({before} -> {after})");
            else if (beforeRef == null || afterRef == null) lines.Add(what + " same");
            else if (beforeRef.Target != null && ReferenceEquals(beforeRef.Target, afterRef.Target)) lines.Add(what + " same");
            else lines.Add($"{what} NEW (same hash {after}, but not the same object: the old one is {(beforeRef.Target == null ? "gone" : "still alive")})");
        }

        static string Show(string s) => string.IsNullOrEmpty(s) ? "-" : s;

        /// <summary>One line with every field, for the state log.</summary>
        public static string Format(UniverseIdentity id)
        {
            if (id == null) return "no identity";
            return string.Format(CultureInfo.InvariantCulture,
                "plugin#{0} pilotsMgr#{1} game#{2} universe#{3} sim#{4} messages#{5} window {6} pluginObjects {7}; vessel {8} '{9}' parts {10}, vessels {11}; pilots {12} running [{13}]; state {14} phase {15} UT {16:0.##} paused {17}",
                id.Plugin, id.PilotsManager, id.Game, id.Universe, id.Simulation, id.Messages, id.HasWindow, id.PluginCount,
                Show(id.ActiveVesselGuid), Show(id.ActiveVesselName), id.ActivePartCount, id.VesselCount,
                id.PilotCount, string.Join(", ", id.RunningPilots), Show(id.GameState), Show(id.Phase), id.UniverseTime, id.Paused);
        }
    }
}
