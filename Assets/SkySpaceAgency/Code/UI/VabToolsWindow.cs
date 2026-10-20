using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using K2D2.KSPService;
using K2UI;
using KSP.UI.Binding;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    /// <summary>
    /// The "SkySpaceAgency — VAB tools" window: export, import, parts dump, open folder, status line.
    /// Built in code, so it needs no Addressables entry of its own (only K2D2_Window.uxml is one):
    /// it borrows that asset's style sheets and AppShell's USS class names. VAB only: it closes
    /// itself in any other scene. The flight window keeps its own scene gating.
    /// </summary>
    public class VabToolsWindow : MonoBehaviour
    {
        public const string WindowId = "SkySpaceAgencyVabTools";
        const string NoFileChoice = "(no .json file in vab/imports or vab/exports)";

        VisualElement _window;
        DropdownField _files;
        Label _status;
        readonly List<Button> _buttons = new();
        bool _isOpen;
        bool _busy;

        public static VabToolsWindow Create(VisualTreeAsset styleSource)
        {
            // Full-screen host that lets clicks through to the VAB; the window sits inside it.
            var host = new VisualElement { name = "vab-tools-host", pickingMode = PickingMode.Ignore };
            host.style.flexGrow = 1;

            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                IsHidingEnabled = true,
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions
                {
                    IsMovingEnabled = false,   // DragManipulator below, as for the main window
                    CheckScreenBounds = true
                }
            };
            var panel = Window.Create(options, host);
            var window = panel.gameObject.AddComponent<VabToolsWindow>();
            window.Build(host, styleSource);
            return window;
        }

        void Build(VisualElement host, VisualTreeAsset styleSource)
        {
            bool styled = false;
            if (styleSource != null)
            {
                // Unity API (VisualTreeAsset.stylesheets): K2D2.uss, KerbalUI.uss, K2UI*.uss.
                foreach (StyleSheet sheet in styleSource.stylesheets)
                {
                    if (sheet == null)
                        continue;
                    host.styleSheets.Add(sheet);
                    styled = true;
                }
            }

            _window = new VisualElement { name = "vab-tools" };
            _window.AddToClassList("root");
            _window.AddToClassList("oab-window-root");
            _window.style.position = Position.Absolute;
            _window.style.left = 420;
            _window.style.top = 90;
            _window.style.width = 340;
            if (!styled)
            {
                _window.style.backgroundColor = new Color(0.07f, 0.08f, 0.11f, 0.95f);
                Pad(_window, 8);
            }

            var header = new VisualElement { name = "header" };
            header.AddToClassList("oab-window-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            var title = new Label("SkySpaceAgency — VAB tools") { name = "header-title" };
            title.AddToClassList("oab-window-header-title");
            title.style.flexGrow = 1;
            var close = new Button(() => IsWindowOpen = false) { name = "close-button", text = "x" };
            close.AddToClassList("oab-close-button");
            header.Add(title);
            header.Add(close);
            _window.Add(header);

            var content = new VisualElement { name = "content" };
            content.AddToClassList("oab-window-content");
            _window.Add(content);

            content.Add(SectionLabel("Export"));
            content.Add(NewButton("Export", OnExport, false));

            content.Add(SectionLabel("Import"));
            var fileRow = Row();
            _files = new DropdownField { name = "import-file" };
            _files.style.flexGrow = 1;
            fileRow.Add(_files);
            fileRow.Add(NewButton("Refresh", RefreshFiles, false));
            content.Add(fileRow);
            content.Add(NewButton("Import (replaces the current rocket)", OnImport, false));

            content.Add(SectionLabel("Parts and files"));
            var toolsRow = Row();
            toolsRow.Add(NewButton("Dump parts", OnDump, true));
            toolsRow.Add(NewButton("Open folder", OnOpenFolder, true));
            content.Add(toolsRow);

            _status = new Label("Ready.") { name = "status" };
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 6;
            content.Add(_status);

            host.Add(_window);
            _window.AddManipulator(new DragManipulator(false, "vab_window_pos"));
            _window.Show(false);
        }

        static void Pad(VisualElement e, float px)
        {
            e.style.paddingLeft = px;
            e.style.paddingRight = px;
            e.style.paddingTop = px;
            e.style.paddingBottom = px;
        }

        static Label SectionLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 6;
            return label;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        Button NewButton(string text, Action onClick, bool grow)
        {
            var button = new Button(onClick) { text = text };
            if (grow)
                button.style.flexGrow = 1;
            _buttons.Add(button);
            return button;
        }

        /// <summary>Opens or closes the window; it only opens in the VAB.</summary>
        public bool IsWindowOpen
        {
            get => _isOpen;
            set
            {
                if (value && !VabReader.InVab())
                    value = false;
                if (_isOpen == value)
                    return;   // also ends the AppBar toggle -> callback -> setter loop
                _isOpen = value;
                _window?.Show(value);
                GameObject.Find(K2D2_Plugin.ToolbarOabButtonID)
                    ?.GetComponent<UIValue_WriteBool_Toggle>()
                    ?.SetValue(value);
                if (value)
                    RefreshFiles();
            }
        }

        void Update()
        {
            if (_isOpen && (!VabReader.InVab() || K2D2_Plugin.Instance == null || !K2D2_Plugin.Instance.Enabled))
                IsWindowOpen = false;
        }

        void SetStatus(string text)
        {
            if (_status != null)
                _status.text = text;
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (Button b in _buttons)
                b.SetEnabled(!busy);
        }

        void RefreshFiles()
        {
            if (_files == null)
                return;
            List<string> choices;
            try { choices = VabActions.ImportChoices(); }
            catch (Exception e)
            {
                K2D2_Plugin.logger.LogError("[VabTools] cannot list the import files: " + e.Message);
                choices = new List<string>();
            }
            string previous = _files.value;
            if (choices.Count == 0)
            {
                _files.choices = new List<string> { NoFileChoice };
                _files.SetValueWithoutNotify(NoFileChoice);
                return;
            }
            _files.choices = choices;
            _files.SetValueWithoutNotify(choices.Contains(previous) ? previous : choices[0]);
        }

        void OnExport()
        {
            if (_busy)
                return;
            VabActions.Export(out string message);
            SetStatus(message);
            RefreshFiles();
        }

        void OnDump()
        {
            if (_busy)
                return;
            VabActions.Dump(out string message);
            SetStatus(message);
        }

        void OnImport()
        {
            if (_busy)
                return;
            string choice = _files?.value;
            if (string.IsNullOrEmpty(choice) || choice == NoFileChoice)
            {
                SetStatus("Put a .json file in vab/imports first (Open folder), then press Refresh.");
                return;
            }
            StartCoroutine(RunImport(Path.Combine(VabActions.Folder(), choice)));
        }

        // Drives the import (and the coroutines it yields) step by step instead of handing it to
        // Unity: an exception that escapes it then ends here with a status line, and the buttons
        // always come back, instead of a window stuck on "Importing..." until a restart.
        IEnumerator RunImport(string path)
        {
            SetBusy(true);
            SetStatus($"Importing {Path.GetFileName(path)}...");
            var stack = new Stack<IEnumerator>();
            stack.Push(VabActions.Import(path, SetStatus));
            try
            {
                while (stack.Count > 0)
                {
                    object current = null;
                    bool moved;
                    try
                    {
                        moved = stack.Peek().MoveNext();
                        if (moved)
                            current = stack.Peek().Current;
                    }
                    catch (Exception e)
                    {
                        K2D2_Plugin.logger.LogError("[VabTools] import: stopped by an unexpected error: " + e);
                        SetStatus($"Import stopped by an unexpected error ({e.GetType().Name}: {e.Message}). " +
                                  "See the log. If the rocket changed, import the newest before_import file from vab/exports.");
                        break;
                    }
                    if (!moved)
                        stack.Pop();
                    else if (current is IEnumerator nested)
                        stack.Push(nested);
                    else
                        yield return current;
                }
            }
            finally
            {
                SetBusy(false);
            }
            RefreshFiles();
        }

        void OnOpenFolder()
        {
            try
            {
                VabActions.EnsureFolders();
                Application.OpenURL(new Uri(VabActions.Folder()).AbsoluteUri);
                SetStatus("Opened " + VabActions.Folder());
            }
            catch (Exception e)
            {
                SetStatus("Cannot open the folder: " + e.Message);
            }
        }
    }
}
