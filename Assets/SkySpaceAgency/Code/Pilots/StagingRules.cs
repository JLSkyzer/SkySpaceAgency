using System;
using System.Collections.Generic;

namespace K2D2.Controller
{
    /// <summary>One propellant of one lit engine, as the auto-staging reads it.</summary>
    public struct EngineFuel<T>
    {
        public T Engine;
        /// <summary>The engine says it is starved: its propellants count as empty.</summary>
        public bool Starved;
        public double Stored;
        public double Capacity;
    }

    /// <summary>
    /// Auto-staging's fuel rule, from K2-D2 1.4.0 (IanMealworm), pure and tested in EditMode.
    /// The next stage fires when a propellant of a lit engine is empty. Two fixes over 1.3.0:
    /// - an engine already empty when a stage was fired, and kept on the vessel (not dropped with
    ///   its tanks), is left out afterwards; before, it fired every following stage one after the
    ///   other, parachutes included;
    /// - a propellant with no tank capacity says nothing (0/0 made the lowest level NaN, and the
    ///   vessel never staged again).
    /// The spent set belongs to one vessel and is cleared when the vessel changes.
    /// </summary>
    public sealed class StagingRules<T>
    {
        readonly HashSet<T> spent = new HashSet<T>();
        readonly List<T> empty = new List<T>();
        object vessel;

        public int SpentCount => spent.Count;

        public bool IsSpent(T engine) => spent.Contains(engine);

        /// <summary>The propellant's level in [0, 1], or null when it has no capacity at all.</summary>
        public static double? Level(double stored, double capacity, bool starved)
        {
            if (!(capacity > 0))
                return null;
            return (starved ? 0.0 : Math.Abs(stored)) / capacity;
        }

        /// <summary>The vessel the readings come from: another one clears the spent engines.</summary>
        public void SetVessel(object current)
        {
            if (ReferenceEquals(current, vessel))
                return;
            spent.Clear();
            vessel = current;
        }

        /// <summary>
        /// The lowest propellant level of the lit, not spent engines (1 when nothing is measured),
        /// and the engines found empty, kept for Staged.
        /// </summary>
        public double Lowest(IReadOnlyList<EngineFuel<T>> fuels)
        {
            empty.Clear();
            double lowest = 1;
            for (int i = 0; i < fuels.Count; i++)
            {
                EngineFuel<T> fuel = fuels[i];
                if (spent.Contains(fuel.Engine))
                    continue;
                double? level = Level(fuel.Stored, fuel.Capacity, fuel.Starved);
                if (level == null)
                    continue;
                lowest = Math.Min(lowest, level.Value);
                if (level.Value <= 0 && !empty.Contains(fuel.Engine))
                    empty.Add(fuel.Engine);
            }
            return lowest;
        }

        public static bool ShouldStage(double lowest) => lowest <= 0;

        /// <summary>A stage was fired after Lowest: the engines it found empty are spent.</summary>
        public void Staged()
        {
            foreach (T engine in empty)
                spent.Add(engine);
            empty.Clear();
        }
    }
}
