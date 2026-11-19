using SkySpaceAgency.UI;
using SsaUI;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The pilots write their status through a FullStatus (status line, console, progress bar), as
    /// their controllers' updateUI(root, FullStatus) does for the classic window. This one is backed by
    /// detached classic elements that are never shown; Refresh copies what they hold into new-UI
    /// controls: a status pill (Normal: info, Warning: alert, Error: alert), the console text, and a
    /// progress bar. A tab calls the pilot's updateUI with <see cref="Root"/> and <see cref="Status"/>,
    /// then Refresh.
    /// </summary>
    public class AppPilotStatus
    {
        readonly StatusLine _line = new StatusLine { name = "status_pilot" };
        readonly SsaUI.Console _console = new SsaUI.Console { name = "pilot_console" };
        readonly SsaProgressBar _bar = new SsaProgressBar { name = "progress" };

        /// <summary>The detached root handed to updateUI (holds only the three classic elements).</summary>
        public VisualElement Root { get; } = new VisualElement { name = "app-status-root" };
        public FullStatus Status { get; }

        public AppPill Pill { get; } = new AppPill(StatusKind.Neutral, "");
        public AppNote Console { get; } = new AppNote();
        public AppStageBar Progress { get; } = new AppStageBar("", "", 0);

        public AppPilotStatus()
        {
            var group = new VisualElement();
            group.Add(_line);
            group.Add(_console);
            group.Add(_bar);
            Root.Add(group);
            Status = new FullStatus(Root);
            Status.Reset();
            Console.AddToClassList("ssa-console");
            Refresh();
        }

        static bool Shown(VisualElement e) => e.style.display != DisplayStyle.None;

        public string StatusText => Shown(_line) ? _line.text : "";
        public StatusLine.Level StatusLevel => _line.level;

        public static StatusKind KindOf(StatusLine.Level level) =>
            level == StatusLine.Level.Normal ? StatusKind.Info : StatusKind.Alert;

        /// <summary>Copies the classic elements into the new controls (hidden when empty).</summary>
        public void Refresh()
        {
            string status = StatusText;
            Pill.Set(string.IsNullOrEmpty(status) ? StatusKind.Neutral : KindOf(_line.level), status);
            Pill.style.display = string.IsNullOrEmpty(status) ? DisplayStyle.None : DisplayStyle.Flex;
            Console.Set(Shown(_console) ? _console.text : "");
            bool bar = Shown(_bar);
            Progress.style.display = bar ? DisplayStyle.Flex : DisplayStyle.None;
            if (bar)
            {
                float range = Mathf.Max(1e-3f, _bar.Max - _bar.Min);
                Progress.SetFraction((_bar.value - _bar.Min) / range);
                Progress.SetValue(_bar.Label ?? "");
            }
        }

        /// <summary>Clears what the last run left, as the classic tabs do when a pilot starts.</summary>
        public void Reset()
        {
            Status.Reset();
            Refresh();
        }
    }
}
