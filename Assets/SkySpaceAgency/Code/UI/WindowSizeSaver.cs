using KTools;
using UnityEngine;
using UnityEngine.UIElements;

namespace SkySpaceAgency.UI
{
    /// <summary>
    /// Saves the window size the player picks with the resize handle (UITK for KSP2's ResizeManipulator, added by
    /// the window), and applies it again when the window is created. The handle only resizes; it keeps
    /// nothing between sessions.
    ///
    /// The height keeps its old key (`main_window_size`, written by our former height-only resize
    /// manipulator), so a saved height survives the change; the width has its own key. Writes are
    /// delayed until the size has stopped changing for half a second, so a resize drag does not
    /// rewrite the settings file at every pointer move.
    /// </summary>
    public class WindowSizeSaver
    {
        const float Invalid = -1;
        const long SaveDelayMs = 500;

        readonly VisualElement _target;
        readonly Vector2 _minSize;
        readonly Setting<float> _height;
        readonly Setting<float> _width;
        IVisualElementScheduledItem _pendingSave;
        bool _clampedToPanel;

        bool _paused;

        /// <summary>
        /// While true, size changes are not saved (the new window, minimised to its header). Pausing
        /// first writes a save still waiting for its delay, from the size laid out before the pause.
        /// </summary>
        public bool Paused
        {
            get => _paused;
            set
            {
                if (value && !_paused)
                {
                    _pendingSave?.Pause();
                    Save();
                }
                _paused = value;
            }
        }

        public WindowSizeSaver(VisualElement target, string heightKey, string widthKey, Vector2 minSize)
        {
            _target = target;
            _minSize = minSize;
            _height = new Setting<float>(heightKey, Invalid);
            _width = new Setting<float>(widthKey, Invalid);

            Vector2 saved = new Vector2(_width.V, _height.V);
            Vector2 size = ClampSize(saved, minSize, Vector2.positiveInfinity);
            if (saved.x > 0) _target.style.width = size.x;
            if (saved.y > 0) _target.style.height = size.y;

            _target.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        /// <summary>
        /// The size to apply: each saved dimension at least the minimum and at most the panel. A
        /// dimension never saved (zero or less) is returned as is, and left to the uxml default.
        /// </summary>
        public static Vector2 ClampSize(Vector2 saved, Vector2 min, Vector2 max)
        {
            return new Vector2(ClampOne(saved.x, min.x, max.x), ClampOne(saved.y, min.y, max.y));
        }

        static float ClampOne(float value, float min, float max)
        {
            if (value <= 0) return value;
            return Mathf.Min(Mathf.Max(value, min), Mathf.Max(max, min));
        }

        /// <summary>True when the new size differs from the saved one by a whole pixel or more.</summary>
        public static bool ShouldSave(Vector2 saved, Vector2 current)
        {
            if (current.x <= 0 || current.y <= 0) return false;   // hidden window (display: none)
            return Mathf.Abs(Mathf.Round(current.x) - saved.x) >= 1 || Mathf.Abs(Mathf.Round(current.y) - saved.y) >= 1;
        }

        void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (Paused)
                return;
            Vector2 current = evt.newRect.size;
            if (current.x <= 0 || current.y <= 0)
                return;

            // A size saved on a bigger screen must not come back larger than this one.
            if (!_clampedToPanel)
            {
                _clampedToPanel = true;
                Rect bounds = _target.panel?.visualTree?.contentRect ?? Rect.zero;
                if (bounds.width > 0 && bounds.height > 0)
                {
                    Vector2 clamped = ClampSize(current, _minSize, bounds.size);
                    if (clamped != current)
                    {
                        _target.style.width = clamped.x;
                        _target.style.height = clamped.y;
                        return;   // the next GeometryChangedEvent saves the clamped size
                    }
                }
            }

            if (!ShouldSave(new Vector2(_width.V, _height.V), current))
                return;

            if (_pendingSave == null)
                _pendingSave = _target.schedule.Execute(Save);
            _pendingSave.ExecuteLater(SaveDelayMs);
        }

        void Save()
        {
            if (Paused)
                return;
            Vector2 current = _target.layout.size;
            if (!ShouldSave(new Vector2(_width.V, _height.V), current))
                return;
            _width.V = Mathf.Round(current.x);
            _height.V = Mathf.Round(current.y);
            L.Log($"WindowSizeSaver: window size saved {_width.V.ToString(System.Globalization.CultureInfo.InvariantCulture)}x" +
                  $"{_height.V.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }
}
