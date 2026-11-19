using System.Collections.Generic;
using UnityEngine;

namespace SkySpaceAgency.AppUI
{
    public enum PhaseState { Done, Current, Pending }

    /// <summary>
    /// The horizontal phase timeline (mockups: gold dots for the phases flown, a ringed dot for the
    /// current one, dark dots for the ones to come, and a gold line up to the current phase).
    /// </summary>
    public class PhaseTimelineModel
    {
        public IReadOnlyList<string> Steps { get; }
        public int Current { get; }

        public PhaseTimelineModel(IReadOnlyList<string> steps, int current)
        {
            Steps = steps ?? new string[0];
            Current = Steps.Count == 0 ? 0 : Mathf.Clamp(current, 0, Steps.Count - 1);
        }

        public PhaseState StateOf(int i) =>
            i < Current ? PhaseState.Done : i == Current ? PhaseState.Current : PhaseState.Pending;

        /// <summary>The part of the line drawn gold: 0 at the first phase, 1 at the last. 0 with one phase or none.</summary>
        public float Progress => Steps.Count <= 1 ? 0 : (float)Current / (Steps.Count - 1);

        /// <summary>Where phase i sits along the line, from 0 to 1 (0.5 when there is only one).</summary>
        public float PositionOf(int i) => Steps.Count <= 1 ? 0.5f : (float)i / (Steps.Count - 1);
    }
}
