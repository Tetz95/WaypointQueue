using GalaSoft.MvvmLight.Messaging;
using Game.Events;
using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;
using WaypointQueue.Services;
using WaypointQueue.State;
using WaypointQueue.UI;
using WaypointQueue.Wrappers;

namespace WaypointQueue.UUM
{
#if RR2026
    public sealed class ModEntryPoint : Railroader.Modding.IModEntryPoint
    {
        public void OnModLoad(Railroader.Modding.IModContext context) => Loader.Start(context);

        public void OnModUnload()
        {
        }
    }

#endif
    public static class Loader
    {
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static Harmony HarmonyInstance { get; private set; }
        public static WaypointQueueController Instance { get; private set; }

        public static WaypointWindow WaypointWindow { get; private set; }
        public static RouteManagerWindow RouteManagerWindow { get; private set; }

        public static WaypointQueueSettings Settings { get; private set; }

        internal static ServiceProvider ServiceProvider { get; private set; }

        private static bool MapHasLoaded = false;

        public static GameObject QueueStorageGO { get; private set; }

        public static GameObject RouteStorageGO { get; private set; }

#if RR2026
        // Railroader 2026.1 loads the mod through ModEntryPoint below; a UMM-style mod entry (Rr2026/UmmShim.cs) keeps
        // the rest of the loader as it is, and LoaderUpdater calls OnUpdate every frame as UMM did.
        internal static void Start(Railroader.Modding.IModContext context)
        {
            var modEntry = new UnityModManager.ModEntry
            {
                Info = new UnityModManager.ModInfo { Id = "WaypointQueue", DisplayName = "Waypoint Queue (Tetz fork)" },
                Path = context.ModDirectory,
                Logger = new UnityModManager.ModLogger(context.Log),
            };
            if (!Load(modEntry)) return;
            var updater = new GameObject("WaypointQueueUpdater");
            UnityEngine.Object.DontDestroyOnLoad(updater);
            updater.AddComponent<LoaderUpdater>();
        }

        private class LoaderUpdater : MonoBehaviour
        {
            private void Update() => ModEntry?.OnUpdate?.Invoke(ModEntry, Time.deltaTime);
        }

#endif
        private static bool Load(UnityModManager.ModEntry modEntry)
        {
            if (ModEntry != null)
            {
                modEntry.Logger.Warning("WaypointQueue is already loaded!");
                return false;
            }
            Log($"Loading WaypointQueue assembly version {Assembly.GetExecutingAssembly().GetName().Version}");

            ModEntry = modEntry;
            Settings = UnityModManager.ModSettings.Load<WaypointQueueSettings>(modEntry);

            ConfigureServices();

            Messenger.Default.Register<MapDidLoadEvent>(modEntry, OnMapDidLoad);

            ModEntry.OnUnload = Unload;
            ModEntry.OnGUI = OnGUI;
            ModEntry.OnSaveGUI = OnSaveGUI;
            ModEntry.OnUpdate = OnUpdate;

            HarmonyInstance = new Harmony(modEntry.Info.Id);
            //Harmony.DEBUG = true;

            try
            {
                HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

                var waypointQueueGO = new GameObject("WaypointQueue");
                Instance = waypointQueueGO.AddComponent<WaypointQueueController>();
                waypointQueueGO.AddComponent<WaypointCarPicker>();
                waypointQueueGO.AddComponent<ErrorModalController>();
                waypointQueueGO.AddComponent<WaypointPicker>();
                waypointQueueGO.AddComponent<ModStateManager>();
                waypointQueueGO.AddComponent<ModSaveManager>();

                QueueStorageGO = new GameObject("WaypointQueueStateStorage");
                RouteStorageGO = new GameObject("WaypointQueueRouteStorage");

                UnityEngine.Object.DontDestroyOnLoad(waypointQueueGO);
                UnityEngine.Object.DontDestroyOnLoad(QueueStorageGO);
                UnityEngine.Object.DontDestroyOnLoad(RouteStorageGO);

                if (MapHasLoaded && (WaypointWindow == null || RouteManagerWindow == null))
                {
                    InitWindows();
                }
            }
            catch (Exception ex)
            {
                modEntry.Logger.LogException($"Failed to load {modEntry.Info.DisplayName}:", ex);
                Unload(modEntry);
                return false;
            }

            return true;
        }

        private static void ConfigureServices()
        {
            // This is a bit ugly but it provides the benefit of dependency injection
            // without the overhead of requiring an extra DI package
            ServiceProvider = new ServiceProvider();

            ServiceProvider.AddSingleton<IOpsControllerWrapper>(new OpsControllerWrapper());

            ServiceProvider.AddSingleton<TrainControllerWrapper>(new TrainControllerWrapper());

            ServiceProvider.AddSingleton<ICarService>(new CarService(
                ServiceProvider.GetService<TrainControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<UncouplingService>(new UncouplingService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<IOpsControllerWrapper>()
                ));
            ServiceProvider.AddSingleton<AutoEngineerService>(new AutoEngineerService(
                ServiceProvider.GetService<ICarService>()
                ));

            ServiceProvider.AddSingleton<CouplingService>(new CouplingService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>(),
                ServiceProvider.GetService<TrainControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<RefuelService>(new RefuelService(
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>(),
                ServiceProvider.GetService<IOpsControllerWrapper>()
                ));

            ServiceProvider.AddSingleton<WaypointResolver>(new WaypointResolver(
                ServiceProvider.GetService<UncouplingService>(),
                ServiceProvider.GetService<RefuelService>(),
                ServiceProvider.GetService<CouplingService>(),
                ServiceProvider.GetService<ICarService>(),
                ServiceProvider.GetService<AutoEngineerService>()
                ));
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float delta)
        {
            if (Settings.toggleWaypointPanelKey.Down() && WaypointWindow != null)
            {
                WaypointWindow.Toggle();
            }

            if (Settings.toggleRoutesPanelKey.Down() && RouteManagerWindow != null)
            {
                RouteManagerWindow.Toggle();
            }

        }

        private static bool Unload(UnityModManager.ModEntry modEntry)
        {
            HarmonyInstance.UnpatchAll(modEntry.Info.Id);

            PendingSwitchRestorer.Shutdown();

            if (Instance != null) UnityEngine.Object.DestroyImmediate(Instance.gameObject);
            Instance = null;

            if (WaypointWindow != null) UnityEngine.Object.DestroyImmediate(WaypointWindow.gameObject);
            WaypointWindow = null;

            if (RouteManagerWindow != null) UnityEngine.Object.DestroyImmediate(RouteManagerWindow.gameObject);
            RouteManagerWindow = null;

            if (QueueStorageGO != null) UnityEngine.Object.DestroyImmediate(QueueStorageGO.gameObject);
            QueueStorageGO = null;

            if (RouteStorageGO != null) UnityEngine.Object.DestroyImmediate(RouteStorageGO.gameObject);
            RouteStorageGO = null;

            return true;
        }

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
#if !RR2026
            Settings.Draw(modEntry);
#endif
        }

        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Settings.Save(modEntry);
        }

        private static void OnMapDidLoad(MapDidLoadEvent @event)
        {
            MapHasLoaded = true;

            InitWindows();
        }

        private static void InitWindows()
        {
            WindowHelper.CreateWindow<WaypointWindow>(null);
            WaypointWindow = WaypointWindow.Shared;

            WindowHelper.CreateWindow<RouteManagerWindow>(null);
            RouteManagerWindow = RouteManagerWindow.Shared;
#if RR2026

            // No UMM settings page in 2026.1: the Waypoints window has a Settings button instead.
            WindowHelper.CreateWindow<SettingsWindow>(null);
#endif
        }

        public static void Log(string str)
        {
            ModEntry?.Logger.Log(str);
        }

        public static void LogDebug(string str)
        {
#if DEBUG
            ModEntry?.Logger.Log(str);
#endif
        }

        public static void LogError(string str)
        {
            ModEntry?.Logger.Error(str);
        }
    }
}
