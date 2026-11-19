using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>The horizontal phase timeline under the columns (PhaseTimelineModel gives the states).</summary>
    public class AppTimeline : VisualElement
    {
        public const string UssClass = "ssa-timeline";
        public const string DotClass = "ssa-timeline__dot";
        readonly VisualElement _lane, _progress;

        public PhaseTimelineModel Model { get; private set; } = new PhaseTimelineModel(null, 0);
        public List<VisualElement> Dots { get; } = new();
        public List<Label> Labels { get; } = new();

        public AppTimeline()
        {
            AddToClassList(UssClass);
            name = "timeline";
            _lane = Ui.Box("ssa-timeline__lane");
            _lane.Add(Ui.Box("ssa-timeline__track"));
            _progress = Ui.Box("ssa-timeline__progress");
            SsaPalette.SetBackground(_progress, SsaPalette.GoldGradient);
            _lane.Add(_progress);
            Add(_lane);
        }

        public static string DotClassFor(PhaseState s) => DotClass + "--" + s.ToString().ToLowerInvariant();

        public void SetSteps(IReadOnlyList<string> steps, int current)
        {
            Model = new PhaseTimelineModel(steps, current);
            foreach (var d in Dots) d.RemoveFromHierarchy();
            foreach (var l in Labels) l.parent?.RemoveFromHierarchy();
            Dots.Clear();
            Labels.Clear();
            for (int i = 0; i < Model.Steps.Count; i++)
            {
                PhaseState state = Model.StateOf(i);
                float left = Model.PositionOf(i) * 100;
                var dot = Ui.Box(DotClass, DotClassFor(state));
                if (state == PhaseState.Current) dot.Add(Ui.Box("ssa-timeline__core"));
                dot.style.left = Length.Percent(left);
                var step = Ui.Box("ssa-timeline__step", "ssa-timeline__step--" + state.ToString().ToLowerInvariant());
                step.style.left = Length.Percent(left);
                var label = Ui.Label(Model.Steps[i], "ssa-timeline__label");
                step.Add(label);
                _lane.Add(dot);
                _lane.Add(step);
                Dots.Add(dot);
                Labels.Add(label);
            }
            _progress.style.width = Length.Percent(Model.Progress * 100);
        }

        public void SetCurrent(int current) => SetSteps(Model.Steps, current);
    }

    /// <summary>The bottom bar: what the pilot does next on the left, the actions on the right (main one last).</summary>
    public class AppActionBar : VisualElement
    {
        public const string UssClass = "ssa-actionbar";
        readonly Label _note;

        public List<AppButton> Buttons { get; } = new();

        public AppActionBar(string note = null)
        {
            AddToClassList(UssClass);
            name = "actions";
            _note = Ui.Label(note ?? "", "ssa-actionbar__note");
            Add(_note);
        }

        public string Note => _note.text;
        public void SetNote(string note) => _note.text = note ?? "";

        public AppButton AddButton(string text, AppButton.Kind kind, Action onClick = null)
        {
            var b = new AppButton(text, kind, onClick);
            Buttons.Add(b);
            Add(b);
            return b;
        }
    }
}
