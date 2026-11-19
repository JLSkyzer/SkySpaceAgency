using System;
using System.Reflection;
using KTools;
using SkySpaceAgency.UI;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>Settings of the new window, in the mod's settings file.</summary>
    public static class AppSettings
    {
        static Setting<float> _scale;
        static Setting<string> _page;

        public static Setting<float> Scale => _scale ??= new Setting<float>("app_ui_scale", UiScale.Default);
        public static Setting<string> Page => _page ??= new Setting<string>("app_main_page", "lift");

        public const string PositionKey = "app_window_pos";
        public const string HeightKey = "app_window_height";
        public const string WidthKey = "app_window_width";
    }

    /// <summary>Applies a scale to a window's own PanelSettings (each Window.Create window has one).</summary>
    public static class PanelScale
    {
        static readonly MethodInfo ApplyMethod = typeof(PanelSettings).GetMethod("ApplyPanelSettings",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        /// <summary>True when the scale was changed. ApplyPanelSettings is what UITK for KSP2 calls after its own writes.</summary>
        public static bool Apply(PanelSettings settings, float scale)
        {
            if (settings == null || !UiScale.NeedsApply(settings.scale, scale)) return false;
            settings.scale = scale;
            ApplyMethod?.Invoke(settings, null);
            return true;
        }

        /// <summary>The game's UI scale, as UITK for KSP2 gives it to the windows that follow it (1 if unknown).</summary>
        public static float Stock()
        {
            try
            {
                var template = UitkForKsp2.UitkForKsp2Plugin.PanelSettings;
                return template != null ? template.scale : 1;
            }
            catch (Exception)
            {
                return 1;
            }
        }
    }

    /// <summary>
    /// The new wide window (preview). Created from SkySpaceAgency_App.uxml, whose only content is the style
    /// sheet and a full-screen host that lets clicks through; the frame is built in code (AppWindowView).
    /// Moved by its header (DragManipulator, saved), resized from the lower-right corner (UITK for KSP2's
    /// ResizeManipulator, the one Redux's WindowResizeHandle adds; size saved by WindowSizeSaver), scaled
    /// by our interface scale on top of the game's (PanelScale, checked every frame because UITK for
    /// KSP2 rewrites the panel scale on every resolution or UI-scale change).
    /// </summary>
    public class AppWindow : MonoBehaviour
    {
        PanelRenderer _panel;
        AppWindowView _view;
        WindowSizeSaver _size;
        bool _open, _reclamp, _awaitLayout;

        public AppWindowView View => _view;
        public static event Action OpenClassicWindow;

        public static AppWindow Create(VisualTreeAsset uxml)
        {
            var options = new WindowOptions
            {
                WindowId = ModIdentity.AppWindowId,
                Parent = null,
                IsHidingEnabled = true,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions { IsMovingEnabled = false, CheckScreenBounds = true },
            };
            var panel = Window.Create(options, uxml);
            var window = panel.gameObject.AddComponent<AppWindow>();
            window._panel = panel;
            window.Build(panel.GetWindowRoot());
            return window;
        }

        /// <summary>Builds the frame and the preview pages into the host. Also used by the EditMode harness.</summary>
        public static AppWindowView BuildView(VisualElement host, AppDemo.Hooks hooks)
        {
            var view = new AppWindowView(ModIdentity.Title, ModIdentity.Slogan);
            view.style.left = AppWindowGeometry.DefaultPosition.x;
            view.style.top = AppWindowGeometry.DefaultPosition.y;
            view.style.width = AppWindowGeometry.DefaultSize.x;
            view.style.height = AppWindowGeometry.DefaultSize.y;
            view.style.minWidth = AppWindowGeometry.MinSize.x;
            view.style.minHeight = AppWindowGeometry.MinSize.y;
            AppDemo.Build(view, hooks);
            host?.Add(view);
            return view;
        }

        void Build(VisualElement host)
        {
            if (host == null)
            {
                L.Log("AppWindow: the window root was not resolved, the new window stays empty.");
                return;
            }
            host.pickingMode = PickingMode.Ignore;
            host.style.position = Position.Absolute;
            host.style.left = 0; host.style.top = 0; host.style.right = 0; host.style.bottom = 0;

            var hooks = new AppDemo.Hooks
            {
                Scale = UiScale.Clamp(AppSettings.Scale.V),
                SetScale = s => { AppSettings.Scale.V = s; ApplyScale(); },
                OpenClassicWindow = () => OpenClassicWindow?.Invoke(),
            };
            _view = BuildView(host, hooks);
            _view.Restore(AppSettings.Page.V);
            _view.Tabs.selected += code => AppSettings.Page.V = code;
            _view.Header.CloseButton.clicked += () => IsWindowOpen = false;

            _view.Query<FloatField>().ForEach(f => f.DisableGameInputOnFocus());

            // Drag by the header only (the body has sliders and fields), position saved as for the classic window.
            var header = _view.Header;
            _view.AddManipulator(new DragManipulator(false, AppSettings.PositionKey)
            {
                CanStart = target => IsInside(target, header) && !IsInside(target, header.MinimiseButton) && !IsInside(target, header.CloseButton),
            });
            _view.MakeResizable(new ResizeOptions
            {
                IsResizingEnabled = true,
                CheckScreenBounds = true,
                MinWidth = AppWindowGeometry.MinSize.x,
                MinHeight = AppWindowGeometry.MinSize.y,
            });
            _size = new WindowSizeSaver(_view, AppSettings.HeightKey, AppSettings.WidthKey, AppWindowGeometry.MinSize);

            host.RegisterCallback<GeometryChangedEvent>(_ => _reclamp = true);
            // After opening or restoring from minimised, the layout read in LateUpdate is the old one
            // until the next layout pass: re-clamp only once the window has been laid out again.
            _view.RegisterCallback<GeometryChangedEvent>(_ => _awaitLayout = false);
            _view.minimisedChanged += minimised =>
            {
                _size.Paused = minimised;
                if (!minimised) { _awaitLayout = true; _reclamp = true; }
            };
            IsWindowOpen = false;
            ApplyScale();
            L.Log($"AppWindow: built, scale {UiScale.Percent(AppSettings.Scale.V)} on the game's {PanelScale.Stock():0.##}");
        }

        static bool IsInside(VisualElement e, VisualElement ancestor)
        {
            for (var v = e; v != null; v = v.parent)
                if (v == ancestor) return true;
            return false;
        }

        public bool IsWindowOpen
        {
            get => _open;
            set
            {
                _open = value;
                if (_view == null) return;
                _view.style.display = value ? DisplayStyle.Flex : DisplayStyle.None;
                if (value) { _awaitLayout = true; _reclamp = true; }
            }
        }

        void ApplyScale()
        {
            if (_panel == null) return;
            float stock = PanelScale.Stock();
            float want = UiScale.Combine(stock, AppSettings.Scale.V);
            if (PanelScale.Apply(_panel.panelSettings, want))
            {
                _reclamp = true;
                L.Log($"AppWindow: panel scale {want:0.###} (game {stock:0.###} x ours {UiScale.Percent(AppSettings.Scale.V)})");
            }
        }

        // Both: UITK for KSP2 may rewrite the panel scale before or after our LateUpdate in a frame.
        void Update() => ApplyScale();

        void LateUpdate()
        {
            ApplyScale();
            if (_reclamp && _open && !_awaitLayout && _view?.panel != null && !_view.Minimised && Reclamp())
                _reclamp = false;
        }

        // Keeps the window inside the panel after a scale or resolution change (the panel shrinks in panel pixels).
        // False while there is no layout to clamp yet (the caller tries again next frame).
        bool Reclamp()
        {
            Rect bounds = _view.panel.visualTree.contentRect;
            Rect now = _view.layout;
            if (bounds.width <= 0 || now.width <= 0 || now.height <= 0) return false;
            Rect r = AppWindowGeometry.Restore(now.position, now.size, bounds.size);
            if (r == now) return true;
            _view.style.left = r.x;
            _view.style.top = r.y;
            _view.style.width = r.width;
            _view.style.height = r.height;
            return true;
        }
    }
}
