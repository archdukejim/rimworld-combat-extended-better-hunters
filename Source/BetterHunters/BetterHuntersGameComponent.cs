using Verse;

namespace BetterHunters
{
    /// <summary>
    /// Drives <see cref="HuntApproachWatchdog"/> once per game tick.
    ///
    /// Using a game component instead of a Harmony postfix on <c>JobDriver.DriverTick</c> means nothing at
    /// all runs for the thousands of non-hunting pawn ticks in a large late-game colony: the only always-on
    /// cost is the master-switch check plus one empty-set check inside <see cref="HuntApproachWatchdog.Tick"/>.
    /// Game components with a <c>(Game)</c> constructor are instantiated automatically for new games and
    /// back-filled into existing saves, so no manual wiring or save data is needed.
    /// </summary>
    public class BetterHuntersGameComponent : GameComponent
    {
        public BetterHuntersGameComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            BetterHuntersSettings s = BetterHuntersMod.Settings;
            if (s == null || !s.enabled || !CeBindings.CoreAvailable)
            {
                return;
            }

            // The watchdog serves the standoff re-check and the backdrop re-check; run it if either is on.
            if (!s.recheckDuringApproach && !s.checkShotBackdrop)
            {
                return;
            }

            HuntApproachWatchdog.Tick(s);
        }
    }
}
