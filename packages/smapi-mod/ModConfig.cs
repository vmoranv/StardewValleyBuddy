// Mod configuration (config.json), read via helper.ReadConfig<ModConfig>().
namespace StardewMCPBridge
{
    /// <summary>Mod configuration stored in config.json next to the manifest.</summary>
    public class ModConfig
    {
        /// <summary>Name of the JSON file used to sync game state to the MCP server.</summary>
        public string BridgeFileName { get; set; } = "bridge_data.json";

        /// <summary>Name of the folder where the MCP server queues action files.</summary>
        public string ActionsFolderName { get; set; } = "actions";

        /// <summary>Whether companion AI ticks run (set false for passive bridge-only mode).</summary>
        public bool EnableAI { get; set; } = true;

        /// <summary>Whether the mod writes game state and processes action files.</summary>
        public bool EnableBridge { get; set; } = true;
    }
}
