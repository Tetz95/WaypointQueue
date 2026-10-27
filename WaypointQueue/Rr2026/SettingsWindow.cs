using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UI.Builder;
using UnityEngine;
using UnityModManagerNet;
using WaypointQueue.UUM;

namespace WaypointQueue.UI
{
    // Railroader 2026.1 has no Unity Mod Manager settings page, so the settings get a window of their own, built from
    // the same Draw attributes UMM drew them from. Every change is saved to Settings.xml at once.
    public class SettingsWindow : WindowBase
    {
        public override string WindowIdentifier => "WaypointQueueSettings";
        public override string Title => "Waypoint Queue Settings";
        public override Vector2Int DefaultSize => new Vector2Int(560, 640);
        public override Window.Position DefaultPosition => Window.Position.Center;
        public override Window.Sizing Sizing => Window.Sizing.Resizable(new Vector2Int(460, 300), new Vector2Int(900, Screen.height));

        public static SettingsWindow Shared;

        private static readonly KeyCode[] ModifierKeys =
            [KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift, KeyCode.LeftAlt, KeyCode.RightAlt];

        // The key binding waiting for a key press, if any.
        private KeyBinding _capturing;

        protected void Awake()
        {
            Shared = this;
        }

        protected void OnDestroy()
        {
            Shared = null;
        }

        public void Show()
        {
            Rebuild();
            WindowPersistence.SetInitialPositionSize(Window, WindowIdentifier, DefaultSize, DefaultPosition, Sizing);
            Window.ShowWindow();
        }

        public void Toggle()
        {
            if (Window.IsShown)
            {
                _capturing = null;
                Window.CloseWindow();
            }
            else Show();
        }

        public override void Populate(UIPanelBuilder builder)
        {
            builder.VScrollView(delegate (UIPanelBuilder builder)
            {
                AddFields(builder, Loader.Settings);
                builder.AddExpandingVerticalSpacer();
            });
        }

        private void AddFields(UIPanelBuilder builder, object settings)
        {
            foreach (FieldInfo field in settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var header = field.GetCustomAttribute<HeaderAttribute>();
                if (header != null)
                {
                    builder.AddSection(header.header);
                }
                var drawHeader = field.GetCustomAttribute<DrawHeaderAttribute>();
                if (drawHeader != null)
                {
                    builder.AddLabel(drawHeader.Color != null ? $"<color={drawHeader.Color}>{drawHeader.Label}</color>" : drawHeader.Label);
                }
                var draw = field.GetCustomAttribute<DrawAttribute>();
                if (draw == null && field.FieldType.GetCustomAttribute<DrawFieldsAttribute>() == null)
                {
                    continue;
                }
                string label = draw?.Label ?? field.Name;
                RectTransform control = AddControl(builder, settings, field, draw);
                if (control == null)
                {
                    continue;
                }
                var element = builder.AddField(label, control);
                if (!string.IsNullOrEmpty(draw?.Tooltip))
                {
                    element.RectTransform.Tooltip(label, draw.Tooltip);
                }
            }
        }

        private RectTransform AddControl(UIPanelBuilder builder, object settings, FieldInfo field, DrawAttribute draw)
        {
            Type type = field.FieldType;
            if (type == typeof(bool))
            {
                return builder.AddToggle(() => (bool)field.GetValue(settings), value => Set(settings, field, value));
            }
            if (type == typeof(KeyBinding))
            {
                var binding = (KeyBinding)field.GetValue(settings);
                return builder.HStack(row =>
                {
                    row.AddButtonCompact(() => _capturing == binding ? "Press a key…" : binding.ToString(), () =>
                    {
                        _capturing = _capturing == binding ? null : binding;
                        Rebuild();
                    });
                    row.AddButtonCompact("Clear", () =>
                    {
                        binding.Change(KeyCode.None, 0);
                        _capturing = null;
                        Save();
                        Rebuild();
                    });
                });
            }
            if (type.IsEnum)
            {
                List<string> names = Enum.GetNames(type).ToList();
                int index = names.IndexOf(field.GetValue(settings).ToString());
                return builder.AddDropdown(names, index, i => Set(settings, field, Enum.Parse(type, names[i])));
            }
            if ((type == typeof(float) || type == typeof(int)) && draw != null && draw.Min > double.MinValue && draw.Max < double.MaxValue)
            {
                bool whole = type == typeof(int) || draw.Precision == 0;
                string format = whole ? "0" : "0." + new string('0', Math.Max(1, draw.Precision));
                return builder.AddSlider(
                    () => Convert.ToSingle(field.GetValue(settings)),
                    () => Convert.ToSingle(field.GetValue(settings)).ToString(format),
                    value => Set(settings, field, type == typeof(int) ? Mathf.RoundToInt(value) : (object)value),
                    (float)draw.Min, (float)draw.Max, whole);
            }
            if (type == typeof(float) || type == typeof(int))
            {
                return builder.AddInputField(field.GetValue(settings).ToString(), text =>
                {
                    if (type == typeof(int) && int.TryParse(text, out int i)) Set(settings, field, Math.Max(i, (int)Math.Max(draw?.Min ?? 0, int.MinValue)));
                    else if (type == typeof(float) && float.TryParse(text, out float f)) Set(settings, field, f);
                    Rebuild();
                });
            }
            if (type.IsClass && type.GetCustomAttribute<DrawFieldsAttribute>() != null)
            {
                object nested = field.GetValue(settings);
                builder.AddSection(draw?.Label ?? field.Name, section => AddFields(section, nested));
                return null;
            }
            return null;
        }

        private static void Set(object settings, FieldInfo field, object value)
        {
            field.SetValue(settings, value);
            Save();
        }

        private static void Save()
        {
            Loader.Settings.Save(Loader.ModEntry);
        }

        // A key binding takes the next key pressed, with the Ctrl, Shift and Alt held at the time. A modifier key on
        // its own (the append/replace/insert keys are Ctrl, Alt and Shift) is taken when it's released.
        private void Update()
        {
            if (_capturing == null)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _capturing = null;
                Rebuild();
                return;
            }
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                if (key == KeyCode.None || key >= KeyCode.Mouse0 || ModifierKeys.Contains(key) || !Input.GetKeyDown(key))
                {
                    continue;
                }
                byte modifiers = 0;
                if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) modifiers |= 1;
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) modifiers |= 2;
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)) modifiers |= 4;
                Capture(key, modifiers);
                return;
            }
            foreach (KeyCode key in ModifierKeys)
            {
                if (Input.GetKeyUp(key))
                {
                    Capture(key, 0);
                    return;
                }
            }
        }

        private void Capture(KeyCode key, byte modifiers)
        {
            _capturing.Change(key, modifiers);
            _capturing = null;
            Save();
            Rebuild();
        }
    }
}
