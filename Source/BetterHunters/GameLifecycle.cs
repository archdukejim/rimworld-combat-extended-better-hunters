using HarmonyLib;
using Verse;

namespace BetterHunters
{
    /// <summary>
    /// Drops the per-pawn caches when a game is loaded or started, so nothing carries across saves.
    /// The mod stores no data of its own - these are purely in-memory working caches.
    /// </summary>
    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class GameLifecycle
    {
        public static void Postfix()
        {
            EngagementSolver.ClearCache();
            BipodDeployPatch.Reset();
        }
    }
}
