#if RR2026
// Railroader 2026.1 has its own mod loader instead of Unity Mod Manager, and no settings support for DLL mods. This
// file stands in for the parts of UMM this mod uses, so the settings class and loader compile unchanged: the Draw
// attributes only describe the settings (SettingsWindow builds the in-game window from them), settings are saved to
// Settings.xml in the mod folder in UMM's format, and key bindings are checked the way UMM checks them.
using System;
using System.IO;
using System.Xml.Serialization;
using UnityEngine;

namespace UnityModManagerNet
{
    [Serializable]
    public class KeyBinding
    {
        public KeyCode keyCode;

        // Bit 1 Ctrl, 2 Shift, 4 Alt, as in UMM.
        public byte modifiers;

        public void Change(KeyCode key, byte mods)
        {
            keyCode = key;
            modifiers = mods;
        }

        private static bool Ctrl() => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        private static bool Shift() => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        private static bool Alt() => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);

        // Exactly the modifiers set must be held.
        private bool CheckModifierKeys() =>
            ((modifiers & 1) != 0) == Ctrl() && ((modifiers & 2) != 0) == Shift() && ((modifiers & 4) != 0) == Alt();

        public bool Pressed() => keyCode != KeyCode.None && CheckModifierKeys() && Input.GetKey(keyCode);

        public bool Down() => keyCode != KeyCode.None && CheckModifierKeys() && Input.GetKeyDown(keyCode);

        public bool Up() => keyCode != KeyCode.None && CheckModifierKeys() && Input.GetKeyUp(keyCode);

        public override string ToString()
        {
            if (keyCode == KeyCode.None) return "None";
            string s = "";
            if ((modifiers & 1) != 0) s += "Ctrl+";
            if ((modifiers & 2) != 0) s += "Shift+";
            if ((modifiers & 4) != 0) s += "Alt+";
            return s + keyCode;
        }
    }

    public enum DrawType
    {
        Auto,
        Ignore,
        Toggle,
        ToggleGroup,
        Slider,
        Field,
        KeyBinding,
        PopupList,
    }

    [Flags]
    public enum DrawFieldMask
    {
        Any = 0,
        Public = 1,
        Serialized = 2,
        SkipNotSerialized = 4,
        OnlyDrawAttr = 8,
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class DrawAttribute : Attribute
    {
        public DrawType Type = DrawType.Auto;
        public string Label;
        public string Tooltip;
        public double Min = double.MinValue;
        public double Max = double.MaxValue;
        public int Precision = 2;
        public bool Collapsible;

        public DrawAttribute() { }

        public DrawAttribute(string label) { Label = label; }

        public DrawAttribute(DrawType type) { Type = type; }

        public DrawAttribute(string label, DrawType type)
        {
            Label = label;
            Type = type;
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class DrawHeaderAttribute : Attribute
    {
        public string Label;
        public string Color;

        public DrawHeaderAttribute(string label) { Label = label; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class DrawFieldsAttribute : Attribute
    {
        public DrawFieldMask Mask;

        public DrawFieldsAttribute(DrawFieldMask mask) { Mask = mask; }
    }

    public interface IDrawable
    {
        void OnChange();
    }

    public class UnityModManager
    {
        public class ModInfo
        {
            public string Id;
            public string DisplayName;
            public string Version;
        }

        public class ModLogger
        {
            private readonly Railroader.Modding.IModLogger _log;

            public ModLogger(Railroader.Modding.IModLogger log) { _log = log; }

            public void Log(string str) => _log.Info(str);

            public void Warning(string str) => _log.Warning(str);

            public void Error(string str) => _log.Error(str);

            public void LogException(string key, Exception e) => _log.Error(e, key);

            public void LogException(Exception e) => _log.Error(e, e.Message);
        }

        public class ModEntry
        {
            public ModInfo Info;
            public string Path;
            public ModLogger Logger;

            public Func<ModEntry, bool> OnUnload;
            public Action<ModEntry> OnGUI;
            public Action<ModEntry> OnSaveGUI;
            public Action<ModEntry, float> OnUpdate;
        }

        public class ModSettings
        {
            public virtual void Save(ModEntry modEntry) => Save(this, modEntry);

            public virtual string GetPath(ModEntry modEntry) => System.IO.Path.Combine(modEntry.Path, "Settings.xml");

            public static void Save<T>(T data, ModEntry modEntry) where T : ModSettings, new()
            {
                string path = data.GetPath(modEntry);
                try
                {
                    using var writer = new StreamWriter(path);
                    new XmlSerializer(typeof(T)).Serialize(writer, data);
                }
                catch (Exception e)
                {
                    modEntry.Logger.Error("Can't save " + path + ".");
                    modEntry.Logger.LogException(e);
                }
            }

            public static T Load<T>(ModEntry modEntry) where T : ModSettings, new()
            {
                var t = new T();
                string path = t.GetPath(modEntry);
                if (!File.Exists(path)) return t;
                try
                {
                    using var stream = File.OpenRead(path);
                    return (T)new XmlSerializer(typeof(T)).Deserialize(stream);
                }
                catch (Exception e)
                {
                    modEntry.Logger.Error("Can't read " + path + ".");
                    modEntry.Logger.LogException(e);
                    return t;
                }
            }
        }
    }
}

namespace GalaSoft.MvvmLight.Messaging
{
    using System.Collections.Generic;
    using Game.Events;

    // The game's EventBus in place of MVVM Light's Messenger: subscriptions are kept per recipient so Unregister can
    // drop them all.
    public class Messenger
    {
        public static Messenger Default { get; } = new Messenger();

        private readonly Dictionary<object, List<IDisposable>> _subscriptions = new(ReferenceEqualityComparer.Instance);

        public void Register<TMessage>(object recipient, Action<TMessage> action)
        {
            if (!_subscriptions.TryGetValue(recipient, out var list))
            {
                list = new List<IDisposable>();
                _subscriptions[recipient] = list;
            }
            list.Add(EventBus.Subscribe(action));
        }

        public void Unregister(object recipient)
        {
            if (!_subscriptions.TryGetValue(recipient, out var list)) return;
            _subscriptions.Remove(recipient);
            foreach (var subscription in list) subscription.Dispose();
        }

        public void Send<TMessage>(TMessage message) => EventBus.Publish(message);

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif
