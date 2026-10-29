// Collectible-ALC manager for the hot-reloadable logic assembly. GPL-3.0-or-later.
//
// `svb_reload` swaps SdvbLogic.dll without restarting the game: build Logic (its
// csproj outputs straight into the mod's staging folder), run the command, and the
// shell drops its old logic instances, loads the fresh assembly into a new
// collectible context, and re-creates every runner. Old contexts are unloaded on a
// best-effort basis — a leaked dev reload only costs memory, not correctness.
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using StardewModdingAPI;

namespace StardewMCPBridge
{
    public static class SdvbReloadManager
    {
        private static AssemblyLoadContext context;
        private static ISdvbLogicEntry entry;
        private static string shellDir;

        /// <summary>The live logic entry, loading it on first use.</summary>
        public static ISdvbLogicEntry Entry
        {
            get
            {
                if (entry == null) Load();
                return entry;
            }
        }

        public static void Init(string modDirectory)
        {
            shellDir = modDirectory;
        }

        /// <summary>Staging dll mtime as a long tick, 0 when missing.</summary>
        public static long StagingMtime
        {
            get
            {
                if (shellDir == null) return 0;
                var path = Path.Combine(shellDir, "staging", "SdvbLogic.dll");
                return File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path).Ticks
                    : 0;
            }
        }

        /// <summary>Drop the current logic assembly and load a fresh one. Returns a report.</summary>
        public static string Reload()
        {
            entry = null;

            var report = $"context={context?.Name ?? "none"}";
            if (context != null)
            {
                try
                {
                    context.Unload();
                    report += " → old unloaded";
                }
                catch (Exception ex)
                {
                    // Instances still alive somewhere: leak the old context but keep going.
                    report += $" → old kept ({ex.Message})";
                }
                context = null;
            }

            Load();
            return report + $" → loaded {context.Name}";
        }

        private static void Load()
        {
            if (shellDir == null)
                throw new InvalidOperationException("SdvbReloadManager.Init not called yet.");

            var staging = Path.Combine(shellDir, "staging", "SdvbLogic.dll");
            if (!File.Exists(staging))
                throw new FileNotFoundException(
                    $"SdvbLogic.dll not found at {staging}. Build packages/logic first (dotnet build -p:GamePath=...).");

            // LoadFromAssemblyPath locks the file, so load a rotating copy and leave the
            // staging original free for the next build.
            var liveDir = Path.Combine(shellDir, "staging", "live");
            Directory.CreateDirectory(liveDir);
            foreach (var old in Directory.GetFiles(liveDir, "SdvbLogic-*.dll"))
            {
                try { File.Delete(old); } catch { /* locked by the previous ALC — keep */ }
            }
            var live = Path.Combine(liveDir, $"SdvbLogic-{DateTime.UtcNow.Ticks}.dll");
            File.Copy(staging, live, overwrite: true);

            context = new AssemblyLoadContext($"sdvb-logic-{DateTime.UtcNow.Ticks}", isCollectible: true);
            var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(live));
            entry = (ISdvbLogicEntry)assembly.CreateInstance("SdvbLogic.SdvbLogicEntry")
                    ?? throw new TypeLoadException("SdvbLogic.SdvbLogicEntry not found in the loaded assembly.");
        }
    }
}
