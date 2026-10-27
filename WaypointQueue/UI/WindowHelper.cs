using System;
using UI;
using UI.Common;
using UnityEngine;

namespace WaypointQueue.UI
{
    internal class WindowHelper
    {
        public static void CreateWindow<TWindow>(Action<TWindow> configure) where TWindow : Component, IBuilderWindow
        {
            WindowManager.Shared.CreateWindowWithComponent(configure);
        }
    }
}
