namespace WaitTracker;

/// <summary>
/// ROUND 9: the categories a wait segment can be attributed to, used both as
/// the per-player breakdown keys shown in the end-of-run tooltip
/// (Patches.cs) and as the human-readable tag in error logs (previously
/// just the raw synchronizer class name). One constant per wait-tracking
/// context wired up in WaitTrackerMod.cs / NonCombatPatches.cs.
/// </summary>
internal static class WaitCategories
{
    public const string Combat = "Combat";
    public const string MapSelection = "Map Selection";
    public const string TreasureRelics = "Treasure/Relics";
    public const string AncientsEvents = "Ancients/Events";
    public const string Rewards = "Rewards";
    public const string RestSites = "Rest Sites";
    public const string ActTransitions = "Act Transitions";
}
