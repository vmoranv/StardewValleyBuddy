// Per-save companion state, persisted via helper.Data so companions come back
// after a save/load cycle. File: companion_state.json inside the save folder.
namespace StardewMCPBridge
{
    using System.Collections.Generic;

    /// <summary>Serializado por SMAPI Data API (<c>helper.Data.WriteJsonFile</c>).</summary>
    public class CompanionState
    {
        /// <summary>One entry per companion that was alive when the save was written.</summary>
        public List<CompanionSnapshot> Companions { get; set; } = new List<CompanionSnapshot>();
    }

    /// <summary>The subset of companion state worth restoring on load.</summary>
    public class CompanionSnapshot
    {
        public string Name { get; set; }
        public string Mode { get; set; } = "follow";
        public bool AutoCombat { get; set; }
    }
}
