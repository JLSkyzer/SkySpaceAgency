using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KSP.Game;
using KSP.Sim.impl;
using KTools;
using UnityEngine;

namespace SkySpaceAgency.Bridge
{
    // Fills a UniverseIdentity from the game (glue). Every item is read on its own inside a try: a missing
    // or destroyed object counts as missing (id 0), it never throws into the game's loop.
    public static class UniverseIdentityReader
    {
        /// <summary>The object's id (RuntimeHelpers.GetHashCode), 0 for null or a destroyed Unity object.</summary>
        public static int Id(object o)
        {
            if (o == null) return 0;
            if (o is UnityEngine.Object u && u == null) return 0;
            return RuntimeHelpers.GetHashCode(o);
        }

        /// <summary>The object, or null for null and for a destroyed Unity object (a plain `?.` does not see the latter).</summary>
        static T Alive<T>(T o) where T : class => o is UnityEngine.Object u && u == null ? null : o;

        static WeakReference Weak(object o) => o == null ? null : new WeakReference(o);

        static T Try<T>(Func<T> read, T fallback = default)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }

        public static UniverseIdentity Capture()
        {
            var id = new UniverseIdentity();
            var plugin = Alive(SkySpaceAgencyPlugin.Instance);
            var game = Alive(GeneralTools.Game);
            var pilotsManager = Try(() => plugin != null ? plugin.pilots_manager : null);
            var universe = Try(() => game != null ? Alive(game.UniverseModel) : null);
            var simulation = Try(() => game != null ? Alive(game.SpaceSimulation) : null);
            var messages = Try(() => game != null ? Alive(game.Messages) : null);
            var viewController = Try(() => game != null ? Alive(game.ViewController) : null);

            id.Plugin = Id(plugin); id.PluginRef = Weak(plugin);
            id.PilotsManager = Id(pilotsManager); id.PilotsManagerRef = Weak(pilotsManager);
            id.Game = Id(game); id.GameRef = Weak(game);
            id.Universe = Id(universe); id.UniverseRef = Weak(universe);
            id.Simulation = Id(simulation); id.SimulationRef = Weak(simulation);
            id.Messages = Id(messages); id.MessagesRef = Weak(messages);
            id.HasWindow = Try(() => plugin != null && plugin.HasAppWindow);
            // Scene objects only: FindObjectsOfTypeAll also returns assets and prefabs.
            id.PluginCount = Try(() =>
            {
                int n = 0;
                foreach (var p in Resources.FindObjectsOfTypeAll<SkySpaceAgencyPlugin>())
                    if (p != null && p.gameObject.scene.IsValid()) n++;
                return n;
            });

            var pilots = pilotsManager?.pilots;
            id.PilotCount = pilots?.Count ?? 0;
            var running = new List<string>();
            if (pilots != null)
                foreach (var p in pilots)
                    if (Try(() => p != null && p.isRunning)) running.Add(p.GetType().Name);
            id.RunningPilots = running;

            id.GameState = Try(() => game != null ? game.GlobalGameState?.GetState().ToString() : null);
            id.UniverseTime = Try(() => universe != null ? universe.UniverseTime : 0.0);
            id.Paused = Try(() => viewController != null && viewController.IsPaused);
            id.VesselCount = Try(() => universe != null ? universe.GetAllVessels()?.Count ?? 0 : 0);
            id.Phase = StageRecoveryAccess.Read().Phase.ToString();

            VesselComponent vessel = Try(() => viewController != null ? viewController.GetActiveSimVessel(false) : null);
            if (vessel != null)
            {
                id.ActiveVesselGuid = Try(() => vessel.GlobalId.ToString());
                id.ActiveVesselName = Try(() => vessel.Name);
                id.ActivePartCount = Try(() => vessel.SimulationObject?.PartOwner?.PartCount ?? 0);
            }
            return id;
        }
    }
}
