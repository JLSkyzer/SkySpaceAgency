using UnityEngine.UIElements;

namespace SsaUI
{
    /// <summary>
    /// Extensions used for VisualElement (the classic window's Bind / listenClick helpers went with it).
    /// </summary>
    public static class VisualElementExtension
    {
        public static void Show(this VisualElement element, bool show)
        {
            element.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
