using System;
using UnityEngine.UIElements;

namespace SkySpaceAgency.AppUI
{
    /// <summary>STUB of the compact flight bar (docs/design/ui/hud.png), replaced by its migration.</summary>
    public class AppHudView : VisualElement
    {
        public event Action openWindow;
        public AppHudView() { AddToClassList("ssa-hud"); }
        public void Show(HudState state) { style.display = state == null ? DisplayStyle.None : DisplayStyle.Flex; }
        protected void RaiseOpen() => openWindow?.Invoke();
    }
}
