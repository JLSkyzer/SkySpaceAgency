using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>
    /// The last messages of the VAB tools, newest first (the status line shows the newest, the journal
    /// the few before it). Pure, tested in EditMode.
    /// </summary>
    public sealed class VabJournal
    {
        public const int Capacity = 6;
        readonly List<string> _lines = new();

        public IReadOnlyList<string> Lines => _lines;

        /// <summary>Adds a message (empty ones are ignored); the oldest falls off after Capacity.</summary>
        public void Add(string text, DateTime? at = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string stamp = (at ?? DateTime.Now).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string flat = text.Replace("\r", "").Replace("\n", " ").Trim();
            _lines.Insert(0, stamp + "  " + flat);
            if (_lines.Count > Capacity) _lines.RemoveAt(_lines.Count - 1);
        }

        /// <summary>The messages older than the newest, as one text (empty with fewer than two).</summary>
        public string Earlier => _lines.Count < 2 ? "" : string.Join("\n", _lines.GetRange(1, _lines.Count - 1));

        public void Clear() => _lines.Clear();
    }

    /// <summary>What the status line of the VAB tools says, by its words (the actions write plain sentences).</summary>
    public static class VabStatus
    {
        static readonly string[] Bad = { "cannot", "can't", "could not", "failed", "refus", "error", "stopped", "unexpected", "not found", "no .json", "not ready", "nothing to", "no rocket" };
        static readonly string[] Busy = { "importing", "exporting", "dumping", "working", "waiting", "restoring" };

        public static StatusKind KindOf(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return StatusKind.Neutral;
            string t = text.ToLowerInvariant();
            foreach (string w in Bad) if (t.Contains(w)) return StatusKind.Alert;
            foreach (string w in Busy) if (t.Contains(w)) return StatusKind.Info;
            return t.StartsWith("ready") || t.StartsWith("put a .json") ? StatusKind.Neutral : StatusKind.Ok;
        }
    }

    /// <summary>
    /// The VAB tools window (export, import, parts dump, open folder, status), in the style of the
    /// mockups extended to a small one-page window (no mockup exists: header, cards, buttons and pills of
    /// the new interface). Pure view; VabToolsWindow holds the actions. Opens in the VAB only.
    /// </summary>
    public class VabToolsView : VisualElement
    {
        public const string UssClass = "ssa-app";
        public const string NoFileChoice = "(no .json file in vab/imports or vab/exports)";

        public AppHeader Header { get; }
        public AppButton ExportButton { get; }
        public AppDropdown Files { get; }
        public AppButton RefreshButton { get; }
        public AppButton ImportButton { get; }
        public AppButton DumpButton { get; }
        public AppButton FolderButton { get; }
        public AppPill StatusPill { get; }
        public AppNote JournalNote { get; }
        public VabJournal Journal { get; } = new VabJournal();

        readonly List<AppButton> _buttons = new();
        public bool Busy { get; private set; }

        public VabToolsView(string title, string slogan,
            Action onExport, Action onImport, Action onRefresh, Action onDump, Action onFolder, Action onClose)
        {
            AddToClassList(UssClass);
            AddToClassList("ssa-vab");
            name = "vab-tools";
            SsaPalette.SetBackground(this, SsaPalette.WindowGradient);

            Header = new AppHeader(title, slogan);
            Header.MinimiseButton.style.display = DisplayStyle.None;   // one small page: nothing to fold
            Header.CloseButton.clicked += () => onClose?.Invoke();
            Add(Header);

            var body = Ui.Box("ssa-vab__body");
            Add(body);

            var export = new AppCard("Export");
            ExportButton = Add(export, "Export", AppButton.Kind.Main, onExport);
            ExportButton.AddToClassList("ssa-vab__last");
            export.Add(new AppNote("Writes the rocket in the VAB, and the workspace, to vab/exports as JSON."));
            body.Add(export);

            var import = new AppCard("Import");
            var row = Ui.Box("ssa-vab__row");
            Files = new AppDropdown("", new List<string> { NoFileChoice }, 0);
            Files.AddToClassList("ssa-vab__files");
            row.Add(Files);
            RefreshButton = Add(row, "Refresh", AppButton.Kind.Plain, onRefresh);
            RefreshButton.AddToClassList("ssa-vab__last");
            import.Add(row);
            ImportButton = Add(import, "Import (replaces the current rocket)", AppButton.Kind.Abort, onImport);
            ImportButton.AddToClassList("ssa-vab__last");
            import.Add(new AppNote("Reads a .json file of vab/imports or vab/exports. The workspace is saved to vab/exports first."));
            body.Add(import);

            var files = new AppCard("Parts and files");
            var tools = Ui.Box("ssa-vab__row");
            DumpButton = Add(tools, "Dump parts", AppButton.Kind.Plain, onDump);
            FolderButton = Add(tools, "Open folder", AppButton.Kind.Plain, onFolder);
            FolderButton.AddToClassList("ssa-vab__last");
            files.Add(tools);
            body.Add(files);

            StatusPill = new AppPill(StatusKind.Neutral, "Ready.");
            StatusPill.name = "status";
            body.Add(StatusPill);
            JournalNote = new AppNote();
            JournalNote.AddToClassList("ssa-console");
            JournalNote.style.display = DisplayStyle.None;
            body.Add(JournalNote);
        }

        AppButton Add(VisualElement parent, string text, AppButton.Kind kind, Action click)
        {
            var b = new AppButton(text, kind, click);
            b.AddToClassList("ssa-vab__button");
            parent.Add(b);
            _buttons.Add(b);
            return b;
        }

        /// <summary>Shows a message: the status pill takes it (coloured by what it says), the journal keeps the earlier ones.</summary>
        public void SetStatus(string text)
        {
            Journal.Add(text);
            StatusPill.Set(VabStatus.KindOf(text), text);
            JournalNote.Set(Journal.Earlier);
        }

        /// <summary>While an import runs, every button is greyed.</summary>
        public void SetBusy(bool busy)
        {
            Busy = busy;
            foreach (var b in _buttons) b.SetEnabled(!busy);
            Files.SetEnabled(!busy);
        }

        /// <summary>The import choices; none gives the placeholder choice.</summary>
        public void SetFiles(IReadOnlyList<string> choices, string keep = null)
        {
            if (choices == null || choices.Count == 0)
            {
                Files.SetChoices(new List<string> { NoFileChoice }, 0);
                return;
            }
            var list = new List<string>(choices);
            int index = keep != null ? list.IndexOf(keep) : -1;
            Files.SetChoices(list, index >= 0 ? index : 0);
        }

        /// <summary>The chosen import file, or null when there is none.</summary>
        public string SelectedFile
        {
            get
            {
                string v = Files.Value;
                return string.IsNullOrEmpty(v) || v == NoFileChoice ? null : v;
            }
        }
    }
}
