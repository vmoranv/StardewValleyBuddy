// Logic assembly entry point — recreated on every `svb_reload`. GPL-3.0-or-later.
using StardewModdingAPI;
using StardewValley;

namespace SdvbLogic
{
    using StardewMCPBridge;

    /// <summary>Loaded by the shell's collectible ALC; see SdvbReloadManager.</summary>
    public class SdvbLogicEntry : ISdvbLogicEntry
    {
        public ICompanionJobs CreateJobs(CompanionFarmer companion, IMonitor monitor)
            => new CompanionJobRunner(companion, monitor);

        public ICompanionCombat CreateCombat(CompanionFarmer companion, IMonitor monitor)
            => new CompanionCombat(companion, monitor);

        public ICompanionCare CreateCare(CompanionFarmer companion, IMonitor monitor)
            => new CompanionCareRunner(companion, monitor);

        public INpcWalker CreateWalker(NPC npc)
            => new NpcWalker(npc);
    }
}
