// Migrated from StardewValley-MCP (source Apache-2.0, https://github.com/amarisaster/StardewValley-MCP).
// This file: GPL-3.0-or-later. See LICENSE.txt.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;

namespace StardewMCPBridge
{
    public class ModEntry : Mod
    {
        private ModConfig config;
        private string bridgePath;
        private string actionDir;
        private BotManager botManager;
        private Texture2D companion1Portrait;
        private Texture2D companion2Portrait;
        private Texture2D companion1Sprite;
        private Texture2D companion2Sprite;

        public override void Entry(IModHelper helper)
        {
            this.config = helper.ReadConfig<ModConfig>();
            this.botManager = new BotManager(this.Monitor, helper);
            SdvbReloadManager.Init(this.Helper.DirectoryPath);
            this.logicMtime = SdvbReloadManager.StagingMtime;
            helper.ConsoleCommands.Add("svb_reload",
                "Hot-swap SdvbLogic.dll from the mod's staging folder (no game restart).",
                this.OnReloadCommand);
            this.ApplyConfigPaths(helper);

            helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
            helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            helper.Events.GameLoop.DayStarted += this.OnDayStarted;
            helper.Events.GameLoop.DayEnding += this.OnDayEnding;
            helper.Events.GameLoop.Saving += this.OnSaving;
            helper.Events.GameLoop.Saved += this.OnSaved;
            helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            helper.Events.GameLoop.TimeChanged += this.OnTimeChanged;
            helper.Events.GameLoop.ReturnedToTitle += this.OnReturnedToTitle;
            helper.Events.Display.MenuChanged += this.OnMenuChanged;
            helper.Events.Content.AssetRequested += this.OnAssetRequested;

            // Console debug commands
            helper.ConsoleCommands.Add("svb_status", "Print companion status.", this.OnStatusCommand);
            helper.ConsoleCommands.Add("svb_cleanup", "Remove all companions and their NPCs.", this.OnCleanupCommand);

            this.Monitor.Log("Stardew MCP Bridge initialized. Content pipeline registered.", LogLevel.Debug);
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            this.companion1Portrait = this.Helper.ModContent.Load<Texture2D>("assets/Companion1_portrait.png");
            this.companion2Portrait = this.Helper.ModContent.Load<Texture2D>("assets/Companion2_portrait.png");
            this.companion1Sprite = this.Helper.ModContent.Load<Texture2D>("assets/Companion1_sprite.png");
            this.companion2Sprite = this.Helper.ModContent.Load<Texture2D>("assets/Companion2_sprite.png");

            this.TryRegisterGmcm();

            this.Monitor.Log("Bridge online. Portraits and sprites loaded. Waiting for world.", LogLevel.Info);
        }

        private void OnAssetRequested(object sender, AssetRequestedEventArgs e)
        {
            // Inject portrait textures
            if (e.NameWithoutLocale.IsEquivalentTo("Portraits/Companion1"))
            {
                e.LoadFrom(() => this.Helper.ModContent.Load<Texture2D>("assets/Companion1_portrait.png"), AssetLoadPriority.Exclusive);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Portraits/Companion2"))
            {
                e.LoadFrom(() => this.Helper.ModContent.Load<Texture2D>("assets/Companion2_portrait.png"), AssetLoadPriority.Exclusive);
            }
            // Custom sprite sheets for walking animation
            else if (e.NameWithoutLocale.IsEquivalentTo("Characters/Companion1"))
            {
                e.LoadFrom(() => this.Helper.ModContent.Load<Texture2D>("assets/Companion1_sprite.png"), AssetLoadPriority.Exclusive);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo("Characters/Companion2"))
            {
                e.LoadFrom(() => this.Helper.ModContent.Load<Texture2D>("assets/Companion2_sprite.png"), AssetLoadPriority.Exclusive);
            }
            // Inject NPC data so the game considers us valid
            else if (e.NameWithoutLocale.IsEquivalentTo("Data/Characters"))
            {
                e.Edit(asset =>
                {
                    var data = asset.AsDictionary<string, CharacterData>();

                    if (!data.Data.ContainsKey("Companion1"))
                    {
                        data.Data["Companion1"] = new CharacterData
                        {
                            DisplayName = "Companion1",
                            HomeRegion = "Town",
                        };
                    }

                    if (!data.Data.ContainsKey("Companion2"))
                    {
                        data.Data["Companion2"] = new CharacterData
                        {
                            DisplayName = "Companion2",
                            HomeRegion = "Town",
                        };
                    }
                });
            }
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            // AI ticks every frame (60/sec) for responsive combat, pathfinding, stuck detection
            if (this.config.EnableAI)
                this.botManager.Update();

            // Hot-reload watch: rebuild SdvbLogic and it swaps in automatically,
            // no console command and no restart needed.
            if (e.IsMultipleOf(60))
                this.CheckLogicReload();

            // Bridge I/O every 30 ticks (~0.5s) to avoid thrashing disk
            if (this.config.EnableBridge && e.IsMultipleOf(30))
            {
                this.SyncGameState();
                this.ProcessActions();
            }
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            // Safety net: if it's 2:00 AM (forced pass-out time), signal bots ready
            if (e.NewTime >= 2600)
                this.botManager.SignalAllSleepReady();
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // Signal all bot farmers as sleep-ready so the game doesn't deadlock
            this.botManager.SignalAllSleepReady();
            this.Monitor.Log("Day ending: bot farmers signaled sleep ready", LogLevel.Debug);
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            // Belt-and-braces with OnDayEnding: whenever the game persists state,
            // make sure bot farmers are sleep-ready so the save never deadlocks.
            this.botManager.SignalAllSleepReady();
            // Persist companions so they can be rebuilt when this save is loaded.
            this.botManager.SaveState();
            // CompanionNPC has no parameterless constructor, so XmlSerializer would
            // abort the entire save if it met one inside location.characters.
            this.botManager.DetachForSave();
            this.Monitor.Log("Saving: companion state persisted, NPCs detached", LogLevel.Debug);
        }

        private void OnSaved(object sender, SavedEventArgs e)
        {
            // Restore the companion NPCs now that the save file has been written.
            this.botManager.ReattachAfterSave();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Rebuild companions recorded by the previous session (mode, auto-combat).
            // RestoreState() sweeps leftover NPCs first, so this is also the cleanup path.
            this.botManager.RestoreState();
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            this.botManager.OnDayStarted();
            this.Monitor.Log("New day: companion stamina restored", LogLevel.Info);
        }

        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            // Persist before tearing down, so quitting straight from in-game keeps state.
            this.botManager.SaveState();
            this.botManager.Cleanup();
            this.Monitor.Log("Returned to title: companion state saved and cleaned up", LogLevel.Info);
        }

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            // Pause ONLY for dialogue: companions should stand still while someone is
            // talking to them, but keep working while the player browses inventory,
            // the ESC menu, the chat box, or shops — in multiplayer none of those
            // stop time, so freezing the AI was pure downtime.
            bool blocking = e.NewMenu is StardewValley.Menus.DialogueBox;
            this.Monitor.Log($"Menu changed: {(e.NewMenu?.GetType().Name ?? "null")} → AI {(blocking ? "paused" : "resumed")}", LogLevel.Debug);
            this.botManager.PauseAI(blocking);
        }

        private void SyncGameState()
        {
            try
            {
                var state = new
                {
                    time = Game1.timeOfDay,
                    day = Game1.dayOfMonth,
                    season = Game1.currentSeason,
                    weather = Game1.isLightning ? "storm" : Game1.isRaining ? "rain" : Game1.isSnowing ? "snow" : Game1.isDebrisWeather ? "windy" : "sunny",
                    location = Game1.currentLocation?.Name,
                    player = new
                    {
                        name = Game1.player.Name,
                        health = Game1.player.health,
                        stamina = Game1.player.Stamina,
                        money = Game1.player.Money,
                        position = new { x = Game1.player.Position.X, y = Game1.player.Position.Y }
                    },
                    companions = this.botManager.GetBotStatus(),
                    npcs = Game1.currentLocation?.characters.Select(c => new {
                        name = c.Name,
                        position = new { x = c.Position.X, y = c.Position.Y }
                    }).ToList(),
                    syncedAt = DateTime.UtcNow.ToString("o")
                };

                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                // Atomic write: temp file then rename, so MCP never reads partial JSON.
                // Retry transient IOExceptions (Windows can briefly lock a file if the
                // MCP server is reading it mid-write).
                string tmpPath = this.bridgePath + ".tmp";
                WithIoRetry(() => File.WriteAllText(tmpPath, json));
                WithIoRetry(() => File.Move(tmpPath, this.bridgePath, true));
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Bridge Sync Error: {ex.Message}", LogLevel.Error);
            }
        }

        private void ProcessActions()
        {
            try
            {
                if (!Directory.Exists(this.actionDir))
                    return;

                // Drain the queue oldest-first. Each command is its own file named
                // <timestamp>-<seq>.json, so ordinal filename sort is chronological.
                string[] files = Directory.GetFiles(this.actionDir, "*.json");
                if (files.Length == 0)
                    return;
                Array.Sort(files, StringComparer.Ordinal);

                foreach (string file in files)
                {
                    string json;
                    try
                    {
                        json = WithIoRetry(() => File.ReadAllText(file));
                        // Delete immediately so each command is consumed exactly once,
                        // even if handling below throws.
                        WithIoRetry(() => File.Delete(file));
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Action read error ({Path.GetFileName(file)}): {ex.Message}", LogLevel.Error);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(json))
                        continue;

                    try
                    {
                        this.HandleAction(json);
                    }
                    catch (Exception ex)
                    {
                        this.Monitor.Log($"Action handling error: {ex.Message}", LogLevel.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"Action Processing Error: {ex.Message}", LogLevel.Error);
            }
        }

        private void HandleAction(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("actionType", out var actionType))
                return;

            // Route to bot manager for companion actions
            this.botManager.ProcessAction(json);

            // Handle chat separately (not a companion action)
            if (actionType.GetString() == "chat")
            {
                if (root.TryGetProperty("metadata", out var meta) &&
                    meta.TryGetProperty("message", out var msg))
                {
                    string text = msg.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        Game1.chatBox?.addMessage(text, Microsoft.Xna.Framework.Color.Gold);
                        this.Monitor.Log($"Chat sent: {text}", LogLevel.Info);
                    }
                }
            }
        }

        // ======================
        // CONFIG
        // ======================

        /// <summary>Recompute bridge paths from the current config (on entry and after GMCM reset).</summary>
        private void ApplyConfigPaths(IModHelper helper)
        {
            this.bridgePath = Path.Combine(helper.DirectoryPath, this.config.BridgeFileName);
            this.actionDir = Path.Combine(helper.DirectoryPath, this.config.ActionsFolderName);
        }

        /// <summary>
        /// Register the Generic Mod Config Menu UI when it's installed. This is a soft
        /// dependency: the API is consumed via `dynamic` so there's no hard reference,
        /// and any failure (absent, incompatible version, API shape change) falls back
        /// to plain config.json editing with a warning.
        /// </summary>
        private void TryRegisterGmcm()
        {
            try
            {
                if (!this.Helper.ModRegistry.IsLoaded("spacechase0.GenericModConfigMenu"))
                    return;

                dynamic api = this.Helper.ModRegistry.GetApi("spacechase0.GenericModConfigMenu");
                if (api == null)
                {
                    this.Monitor.Log("GMCM detected but its API is unavailable — config.json editing only.", LogLevel.Debug);
                    return;
                }

                api.Register(
                    this.ModManifest,
                    (Action)(() =>
                    {
                        // Reset to defaults and retarget paths immediately
                        this.config = new ModConfig();
                        this.ApplyConfigPaths(this.Helper);
                    }),
                    (Action)(() =>
                    {
                        // Retarget paths and persist whenever GMCM saves or resets
                        this.ApplyConfigPaths(this.Helper);
                        this.Helper.WriteConfig(this.config);
                    })
                );

                api.AddSectionTitle(this.ModManifest, (Func<string>)(() => "MCP Bridge"));

                api.AddBoolOption(
                    this.ModManifest,
                    (Func<bool>)(() => this.config.EnableBridge),
                    (Action<bool>)(v => this.config.EnableBridge = v),
                    (Func<string>)(() => "Enable bridge"),
                    (Func<string>)(() => "Sync game state to and read actions from the MCP server.")
                );

                api.AddBoolOption(
                    this.ModManifest,
                    (Func<bool>)(() => this.config.EnableAI),
                    (Action<bool>)(v => this.config.EnableAI = v),
                    (Func<string>)(() => "Enable companion AI"),
                    (Func<string>)(() => "Run autonomous companion behavior (uncheck for passive bridge-only mode).")
                );

                api.AddTextOption(
                    this.ModManifest,
                    (Func<string>)(() => this.config.BridgeFileName),
                    (Action<string>)(v => this.config.BridgeFileName = v),
                    (Func<string>)(() => "Bridge file name"),
                    (Func<string>)(() => "JSON file used to publish game state (takes effect on save reload).")
                );

                api.AddTextOption(
                    this.ModManifest,
                    (Func<string>)(() => this.config.ActionsFolderName),
                    (Action<string>)(v => this.config.ActionsFolderName = v),
                    (Func<string>)(() => "Actions folder name"),
                    (Func<string>)(() => "Folder where the MCP server queues command files (takes effect on save reload).")
                );

                this.Monitor.Log("Generic Mod Config Menu integration registered.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                // Non-fatal: config.json editing always works
                this.Monitor.Log($"GMCM registration failed (config.json editing still works): {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>Swap the logic assembly without restarting the game.</summary>
        private void OnReloadCommand(string command, string[] args)
        {
            try
            {
                this.ReloadLogic();
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"svb_reload failed: {ex.Message}", LogLevel.Error);
            }
        }

        private long logicMtime;

        /// <summary>Reload when the staging dll's mtime moved (a new build landed).</summary>
        private void CheckLogicReload()
        {
            long mtime = SdvbReloadManager.StagingMtime;
            if (mtime == 0 || mtime == this.logicMtime) return;

            this.logicMtime = mtime;
            try
            {
                this.ReloadLogic();
            }
            catch (Exception ex)
            {
                // A half-written dll from a running build — skip; the next mtime
                // change retries automatically.
                this.Monitor.Log($"Auto-reload skipped: {ex.Message}", LogLevel.Warn);
            }
        }

        private void ReloadLogic()
        {
            foreach (var ai in this.botManager.AIs)
                ai.DetachLogic();

            var report = SdvbReloadManager.Reload();

            foreach (var ai in this.botManager.AIs)
                ai.AttachLogic();

            this.Monitor.Log($"Logic reloaded: {report}", LogLevel.Info);
        }

        private void OnStatusCommand(string command, string[] args)
        {
            if (!Context.IsWorldReady)
            {
                this.Monitor.Log("World not loaded — no companion status available.", LogLevel.Info);
                return;
            }

            try
            {
                var statuses = this.botManager.GetBotStatus();
                string json = JsonSerializer.Serialize(statuses, new JsonSerializerOptions { WriteIndented = true });
                this.Monitor.Log("Companion status:", LogLevel.Info);
                this.Monitor.Log(json, LogLevel.Info);
            }
            catch (Exception ex)
            {
                this.Monitor.Log($"svb_status failed: {ex.Message}", LogLevel.Error);
            }
        }

        private void OnCleanupCommand(string command, string[] args)
        {
            this.botManager.Cleanup();
        }

        // ======================
        // FILE I/O RETRY
        // ======================

        private const int IoRetryAttempts = 3;
        private const int IoRetryDelayMs = 50;

        /// <summary>
        /// Run a file operation, retrying transient IOExceptions a few times with a
        /// short delay (Windows can briefly lock a file while the MCP server reads it).
        /// Non-IO exceptions propagate to the caller.
        /// </summary>
        private static void WithIoRetry(Action operation)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    operation();
                    return;
                }
                catch (IOException) when (attempt < IoRetryAttempts)
                {
                    Thread.Sleep(IoRetryDelayMs);
                }
            }
        }

        private static T WithIoRetry<T>(Func<T> operation)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return operation();
                }
                catch (IOException) when (attempt < IoRetryAttempts)
                {
                    Thread.Sleep(IoRetryDelayMs);
                }
            }
        }
    }
}
