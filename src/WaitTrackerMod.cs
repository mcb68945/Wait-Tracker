using System;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Rooms;

namespace WaitTracker;

/// <summary>
/// Mod entry point.
///
/// Tracking design (confirmed against the real sts2.dll public API):
/// CombatManager.Instance exposes CombatSetUp/TurnStarted/PlayerEndedTurn/
/// PlayerUnendedTurn/CombatEnded as plain public C# events, and
/// CombatManager.IsPlayerReadyToEndTurn(player) is a public per-player
/// query. We cache the current combat's player roster (handed to us as the
/// CombatState argument of CombatSetUp/TurnStarted) and, on every turn-ready
/// change, recompute who's NOT ready yet. When that shrinks to exactly one
/// player, that's the one everyone else is waiting on -- see WaitState.
///
/// Note: CombatManager.LiveTurnStateFor and CombatTurnState.PlayersReadyToEndTurn
/// looked like the obvious route here but are both *private* -- confirmed by
/// a real build error (CS0117) and a metadata check. IsPlayerReadyToEndTurn
/// is the public equivalent, so we use that instead.
///
/// ROUND 10 (patch-night hardening, no feature change): subscribed to this
/// mod's own Harmony patches as a single `_harmony.PatchAll(assembly)` call.
/// `PatchAll` walks every `[HarmonyPatch]`-attributed class in the assembly
/// and patches them in turn -- but if any one class's target method can't be
/// found/bound (e.g. a game update renames or reshapes one of the
/// Synchronizer constructors this mod hooks in NonCombatPatches.cs), the
/// exception that produces aborts the *whole* PatchAll call, silently
/// skipping every class still left to patch. That's strictly worse than it
/// needs to be: one Synchronizer changing shouldn't take the game-over-
/// screen patch (or any other still-compatible Synchronizer hook) down with
/// it. `PatchAllSafely` below does the same walk by hand, one class at a
/// time, in its own try/catch, so a single broken target is logged and
/// skipped while every other patch still applies normally. This mirrors the
/// game's own mod-loading model (confirmed via a "Exception thrown when
/// calling mod initializer of type " string in sts2.dll -- a mod whose
/// ModLoaded() throws gets logged and skipped, it doesn't take the game or
/// other mods down), just applied at a finer grain, per patch target rather
/// than per mod.
/// </summary>
// Entry point is called by the WaitTracker.dll loader (see Loader.cs), not by the game directly.
public static class WaitTrackerMod
{
    public const string ModId = "WaitTracker";
    private static Harmony? _harmony;
    private static CombatState? _combatState;

    public static void ModLoaded()
    {
        Log.Info($"[{ModId}] loading...");

        WaitState.Initialize();

        var cm = CombatManager.Instance;
        cm.CombatSetUp += OnCombatStateEvent;
        cm.TurnStarted += OnCombatStateEvent;
        cm.PlayerEndedTurn += OnPlayerEndedTurn;
        cm.PlayerUnendedTurn += OnPlayerUnendedTurn;
        cm.CombatEnded += OnCombatEnded;

        // Only used for the end-of-run screen hook plus the non-combat
        // Synchronizer hooks (see Patches.cs / NonCombatPatches.cs) -- the
        // combat tracking above needs no Harmony at all, it's all public
        // events, and already works even if every patch below fails.
        _harmony = new Harmony(ModId);
        PatchAllSafely(_harmony, typeof(WaitTrackerMod).Assembly);

        Log.Info($"[{ModId}] loaded.");
    }

    /// <summary>
    /// Same end result as Harmony's own PatchAll(assembly), except each
    /// [HarmonyPatch]-attributed class is patched in its own try/catch so a
    /// single incompatible target (most likely after a game update changes
    /// or renames a patched method) only drops that one class's patch(es)
    /// instead of silently cancelling every class still left to process in
    /// the same call. Every other context this mod tracks keeps working.
    /// </summary>
    private static void PatchAllSafely(Harmony harmony, Assembly assembly)
    {
        foreach (var type in AccessTools.GetTypesFromAssembly(assembly))
        {
            if (!type.IsDefined(typeof(HarmonyPatch), inherit: false)) continue;

            try
            {
                harmony.CreateClassProcessor(type).Patch();
            }
            catch (Exception e)
            {
                Log.Error($"[{ModId}] Failed to apply Harmony patch(es) in {type.FullName} -- " +
                          "the game may have changed this method since this mod was built. " +
                          $"Other WaitTracker patches will still be attempted. {e}");
            }
        }
    }

    private static void OnCombatStateEvent(CombatState state)
    {
        _combatState = state;
        Recompute();
    }

    private static void OnPlayerEndedTurn(Player _, bool __) => Recompute();
    private static void OnPlayerUnendedTurn(Player _) => Recompute();

    private static void OnCombatEnded(CombatRoom _)
    {
        _combatState = null;
        WaitState.OnCombatEnded();
    }

    private static void Recompute()
    {
        try
        {
            if (_combatState is null)
            {
                WaitState.OnNotReadyPlayersChanged(Array.Empty<Player>(), WaitCategories.Combat);
                return;
            }

            var cm = CombatManager.Instance;
            var notReady = _combatState.Players.Where(p => !cm.IsPlayerReadyToEndTurn(p)).ToList();
            WaitState.OnNotReadyPlayersChanged(notReady, WaitCategories.Combat);
        }
        catch (Exception e)
        {
            // Never let a tracking error take down a real combat turn.
            Log.Error($"[{ModId}] Recompute failed: {e}");
        }
    }
}
