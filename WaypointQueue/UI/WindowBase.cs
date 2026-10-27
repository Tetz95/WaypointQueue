using UI;
using UI.Builder;
using UI.Common;
using UnityEngine;

namespace WaypointQueue.UI
{
#if RR2026
    // 2026.1: the game's WindowManager creates builder windows from an identifier and a WindowConfig.
    public abstract class WindowBase : MonoBehaviour, IBuilderWindow
    {
        string IBuilderWindow.Identifier => WindowIdentifier;
        public WindowConfig Config => new WindowConfig(DefaultSize, DefaultPosition, Sizing);
#else
    public abstract class WindowBase : MonoBehaviour, IProgrammaticWindow
    {
#endif
        protected Window Window => GetComponent<Window>();
        public UIBuilderAssets BuilderAssets { get; set; }
        public abstract string WindowIdentifier { get; }
        public abstract string Title { get; }
        public abstract Vector2Int DefaultSize { get; }
        public abstract Window.Position DefaultPosition { get; }
        public abstract Window.Sizing Sizing { get; }
        public abstract void Populate(UIPanelBuilder builder);

        private UIPanel _panel;

        public virtual void Rebuild()
        {
            if (_panel != null)
            {
                _panel.Dispose();
            }
            Window.Title = Title;
            _panel = UIPanel.Create(Window.contentRectTransform, this.BuilderAssets, new System.Action<UIPanelBuilder>(Populate));
        }
    }
}
