using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace WaitTracker
{
    /// <summary>
    /// The game loads only WaitTracker.dll (the DLL named after the mod id). This loader checks which shape of the
    /// game's synchronizer code is running and loads the matching build from the same folder:
    ///   WaitTracker.V113.dll - game v0.113+ (synchronizers take IRunState; two events gained an argument)
    ///   WaitTracker.V111.dll - game v0.111
    /// One download therefore works on both the live and beta branches. Detection is by feature, not version number,
    /// so a later patch keeps working as long as that code keeps the same shape.
    /// </summary>
    [ModInitializer(nameof(ModLoaded))]
    public static class Loader
    {
        private const string ModId = "WaitTracker";

        public static void ModLoaded()
        {
            try
            {
                // v0.113 changed MapSelectionSynchronizer's constructor from RunState to IRunState (and the other
                // synchronizer signatures Wait Tracker hooks changed in the same patch).
                Type? map = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.MapSelectionSynchronizer");
                bool newShape = map != null && map.GetConstructors().Any(c =>
                    c.GetParameters() is { Length: 3 } ps && ps[2].ParameterType.Name == "IRunState");
                string build = newShape ? "WaitTracker.V113" : "WaitTracker.V111";

                Assembly self = typeof(Loader).Assembly;
                string? dir = Path.GetDirectoryName(self.Location);
                if (string.IsNullOrEmpty(dir))
                {
                    dir = ModManager.GetLoadedMods().FirstOrDefault(m => m?.manifest?.id == ModId)?.path;
                }
                if (string.IsNullOrEmpty(dir))
                {
                    Log.Error($"[{ModId}] Couldn't find the mod's folder - mod not loaded.", 0);
                    return;
                }

                string path = Path.Combine(dir, build + ".dll");
                if (!File.Exists(path))
                {
                    Log.Error($"[{ModId}] {build}.dll is missing from {dir} - reinstall the mod. Mod not loaded.", 0);
                    return;
                }

                AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
                Assembly inner = context.LoadFromAssemblyPath(path);
                Log.Info($"[{ModId}] Game sync code is {(newShape ? "v0.113+ shape" : "v0.111 shape")} - loading {build}.", 0);

                MethodInfo? init = inner.GetType("WaitTracker.WaitTrackerMod")?.GetMethod("ModLoaded", BindingFlags.Public | BindingFlags.Static);
                if (init == null)
                {
                    Log.Error($"[{ModId}] {build}.dll has no WaitTrackerMod.ModLoaded - mod not loaded.", 0);
                    return;
                }
                init.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Log.Error($"[{ModId}] Loader failed - mod not loaded: {ex}", 0);
            }
        }
    }
}
