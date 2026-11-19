using System.Collections.Generic;
using System.Linq;

namespace SkySpaceAgency.AppUI
{
    /// <summary>Which tab of the tab bar is shown. Only an existing, enabled tab can be selected.</summary>
    public class TabModel
    {
        readonly List<string> _codes = new();
        readonly HashSet<string> _disabled = new();

        public TabModel(IEnumerable<string> codes = null)
        {
            if (codes != null)
                foreach (string c in codes)
                    Add(c);
        }

        public IReadOnlyList<string> Codes => _codes;
        public string Current { get; private set; }

        /// <summary>Adds a tab; the first tab added becomes the current one.</summary>
        public void Add(string code)
        {
            if (string.IsNullOrEmpty(code) || _codes.Contains(code)) return;
            _codes.Add(code);
            Current ??= code;
        }

        public bool IsEnabled(string code) => code != null && _codes.Contains(code) && !_disabled.Contains(code);

        /// <summary>Selects the tab; false (and nothing changes) for an unknown or disabled one.</summary>
        public bool Select(string code)
        {
            if (!IsEnabled(code)) return false;
            Current = code;
            return true;
        }

        /// <summary>Disabling the current tab moves the selection to the first enabled one.</summary>
        public void SetEnabled(string code, bool enabled)
        {
            if (enabled) _disabled.Remove(code); else _disabled.Add(code);
            if (Current == code && !enabled)
                Current = FirstEnabled();
            else if (Current == null && enabled)
                Current = FirstEnabled();
        }

        /// <summary>The tab to open with: the saved one if it still exists and is enabled, else the first enabled one.</summary>
        public string Restore(string saved)
        {
            Current = IsEnabled(saved) ? saved : FirstEnabled();
            return Current;
        }

        string FirstEnabled() => _codes.FirstOrDefault(IsEnabled);
    }
}
