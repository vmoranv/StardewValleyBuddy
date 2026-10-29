// Contracts crossing the shell/logic assembly boundary. GPL-3.0-or-later.
//
// The shell (StardewValleyBuddy.dll, loaded by SMAPI) never changes at runtime.
// All frequently-edited behaviour lives in SdvbLogic.dll, loaded into a collectible
// AssemblyLoadContext and swappable via the `svb_reload` console command. Logic
// implements the interfaces here; state classes live in the shell so both sides
// agree on them without cross-context type identity.
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace StardewMCPBridge
{
    // =====================================================================
    // STATE TYPES (shell-owned, mutated by logic)
    // =====================================================================

    /// <summary>What the agent wants done over an area.</summary>
    public enum WorkTask
    {
        ClearArea,
        Plant,
        Water,
        Harvest,
        Sell,
        Chop,
        Mine,
    }

    public enum TileOutcome { Done, Skipped, Failed }

    public enum CareTask
    {
        TendAnimals,
        FeedAnimals,
        InspectCrops,
        ScanTiles,
        ChestDeposit,
        ChestWithdraw,
    }

    /// <summary>Shell-side caps shared with logic.</summary>
    public static class WorkLimits
    {
        public const int MaxTiles = 500;
        public const int MaxReroutes = 40;
        public const int MaxTileSwings = 32;
        public const int MaxJobDurationSeconds = 900;
        public const float StaminaReserve = 10f;
    }

    public class WorkProgress
    {
        public List<object> Done { get; } = new List<object>();
        public List<object> Skipped { get; } = new List<object>();
        public List<object> Failed { get; } = new List<object>();
        public int Refills { get; set; }
        public int Swings { get; set; }
    }

    public class WorkTaskState
    {
        public string Id { get; set; }
        public WorkTask Task { get; set; }
        public string LocationName { get; set; }
        public string SeedId { get; set; }
        public string SellFilter { get; set; }
        public List<Vector2> Remaining { get; } = new List<Vector2>();
        public List<Vector2> RequestedTiles { get; } = new List<Vector2>();
        public List<Vector2> PendingAtStop { get; } = new List<Vector2>();
        public int TotalTiles { get; set; }
        public WorkProgress Progress { get; } = new WorkProgress();

        public string CurrentTile { get; set; }
        public int CurrentTileSwings { get; set; }
        public string LastResult { get; set; }
        public string AbortReason { get; set; }
        public string BlockedBy { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public int Reroutes { get; set; }

        public bool IsFinished => this.Remaining.Count == 0;
        public int DoneCount => this.Progress.Done.Count;
        public int SkippedCount => this.Progress.Skipped.Count;
        public int FailedCount => this.Progress.Failed.Count;
    }

    /// <summary>Progress of a building-level task.</summary>
    public class CareState
    {
        public string Id { get; set; }
        public CareTask Task { get; set; }
        public string LocationName { get; set; }
        public string BuildingName { get; set; }
        public string ChestTile { get; set; }
        public string ItemFilter { get; set; }
        public List<Vector2> Tiles { get; } = new List<Vector2>();
        public List<object> Log { get; } = new List<object>();
        public string LastResult { get; set; }
        public string AbortReason { get; set; }
        public bool Finished { get; set; }
    }

    // =====================================================================
    // LOGIC INTERFACES
    // =====================================================================

    public interface INpcWalker
    {
        bool Active { get; }
        Point CurrentWaypoint { get; }
        void Start(IEnumerable<Point> waypoints);
        void Cancel();
        bool Tick(GameLocation location, GameTime time);
    }

    public interface ICompanionCombat
    {
        bool InCombat { get; }
        string Status { get; }
        float HealthFraction { get; }
        bool Tick();
        void Reset();
    }

    public interface ICompanionCare
    {
        CareState Current { get; }
        CareState Start(CareTask task, string buildingName = null, string chestTile = null, string itemFilter = null,
            string locationName = null, IEnumerable<Vector2> tiles = null);
        void Cancel(string reason);
        bool Tick();
        object Snapshot();
    }

    public interface ICompanionJobs
    {
        WorkTaskState Current { get; }
        int MaxTiles { get; }
        WorkTaskState Start(WorkTask task, string locationName, IEnumerable<Vector2> tiles,
            string seedId = null, string sellFilter = null);
        void Cancel(string reason);
        bool Tick();
        object Snapshot();
    }

    /// <summary>Implemented by SdvbLogic.dll; the shell calls this after every reload.</summary>
    public interface ISdvbLogicEntry
    {
        ICompanionJobs CreateJobs(CompanionFarmer companion, IMonitor monitor);
        ICompanionCombat CreateCombat(CompanionFarmer companion, IMonitor monitor);
        ICompanionCare CreateCare(CompanionFarmer companion, IMonitor monitor);
        INpcWalker CreateWalker(NPC npc);
    }
}
