using SsaUI;
using SkySpaceAgency.AppUI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using SkySpaceAgency.KSPService;

using KSP.UI.Binding;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.UI
{
    /// <summary>
    /// The VAB tools window: export, import, parts dump, open folder, status line and journal. The view
    /// (AppUI/VabToolsView) is built in code from the new interface's components; this MonoBehaviour holds
    /// the actions and the window's life. It needs no Addressables entry of its own: it borrows the style
    /// sheets of the new window's uxml. VAB only: it closes itself in any other scene.
    /// </summary>
    public class VabToolsWindow : MonoBehaviour
    {
        public const string WindowId = ModIdentity.VabWindowId;

        PanelRenderer _panel;
        VabToolsView _view;
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
                UseStockScale = true,      // follows the game's UI Scale, times ours (the interface scale setting)
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions
                {
                    IsMovingEnabled = false,   // DragManipulator below, as for the main window
                    CheckScreenBounds = true
                }
            };
            var panel = Window.Create(options, host);
            var window = panel.gameObject.AddComponent<VabToolsWindow>();
            window._panel = panel;
            window.Build(host, styleSource);
            return window;
        }

        void Build(VisualElement host, VisualTreeAsset styleSource)
        {
            bool styled = false;
            if (styleSource != null)
            {
                // Unity API (VisualTreeAsset.stylesheets): SsaApp.uss and the tabs' style sheets.
                foreach (StyleSheet sheet in styleSource.stylesheets)
                {
                    if (sheet == null)
                        continue;
                    host.styleSheets.Add(sheet);
                    styled = true;
                }
            }
            if (!styled)
                SkySpaceAgencyPlugin.logger.LogWarning("[VabTools] the window's style sheets were not found: the window will look plain.");

            _view = new VabToolsView(ModIdentity.Title, "VAB tools", OnExport, OnImport, RefreshFiles, OnDump, OnOpenFolder,
                () => IsWindowOpen = false);
            _view.style.left = 420;
            _view.style.top = 90;
            host.Add(_view);
            _view.AddManipulator(new DragManipulator(false, "vab_window_pos")
            {
                CanStart = target => IsInsideHeader(target),
            });
            _view.Show(false);
            ApplyScale();
        }

        bool IsInsideHeader(VisualElement e)
        {
            for (var v = e; v != null; v = v.parent)
            {
                if (v == _view.Header.MinimiseButton || v == _view.Header.CloseButton) return false;
                if (v == _view.Header) return true;
            }
            return false;
        }

        // The panel scale: the game's UI scale times the interface scale of the new window.
        void ApplyScale()
        {
            if (_panel == null) return;
            AppUI.PanelScale.Apply(_panel.panelSettings, AppUI.UiScale.Combine(AppUI.PanelScale.Stock(), AppUI.AppSettings.Scale.V));
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
                _view?.Show(value);
                GameObject.Find(SkySpaceAgencyPlugin.ToolbarOabButtonID)
                    ?.GetComponent<UIValue_WriteBool_Toggle>()
                    ?.SetValue(value);
                if (value)
                    RefreshFiles();
            }
        }

        void Update()
        {
            ApplyScale();
            if (_isOpen && (!VabReader.InVab() || SkySpaceAgencyPlugin.Instance == null || !SkySpaceAgencyPlugin.Instance.Enabled))
                IsWindowOpen = false;
        }

        void SetStatus(string text) => _view?.SetStatus(text);

        void SetBusy(bool busy)
        {
            _busy = busy;
            _view?.SetBusy(busy);
        }

        void RefreshFiles()
        {
            if (_view == null)
                return;
            List<string> choices;
            try { choices = VabActions.ImportChoices(); }
            catch (Exception e)
            {
                SkySpaceAgencyPlugin.logger.LogError("[VabTools] cannot list the import files: " + e.Message);
                choices = new List<string>();
            }
            _view.SetFiles(choices, _view.SelectedFile);
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
            string choice = _view?.SelectedFile;
            if (string.IsNullOrEmpty(choice))
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
                        SkySpaceAgencyPlugin.logger.LogError("[VabTools] import: stopped by an unexpected error: " + e);
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
