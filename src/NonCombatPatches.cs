using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Sync;

namespace WaitTracker;

/// <summary>
/// ROUND 5: wait-tracking outside of combat -- map/node selection, treasure
/// chest relic picking, ancients/events, and post-combat rewards.
/// ROUND 7 added two more: rest sites/campfires, and act transitions.
///
/// None of these six systems has a static Instance singleton like
/// CombatManager, so each is captured the same way: a Harmony postfix on its
/// constructor grabs the live instance (and, for most of them, the player
/// roster passed into that constructor) and wires its public vote/choice-
/// changed events into the same WaitState.OnNotReadyPlayersChanged API
/// combat already uses. Rewards and act transitions have no such event at
/// all, so they self-track who's confirmed instead (see RewardsSetPatches
/// and ActChangeSynchronizer_Patches below).
///
/// Assumption carried over from the combat design: the whole party is
/// together in the same room for these screens, so "everyone in the run
/// roster except the stragglers" is the right definition of "who's
/// waiting." If multiplayer allows players to be on different rooms/paths
/// at once, this would need a room-scoped roster instead of the run-wide
/// one -- flagged for real-test verification.
/// </summary>
internal static class NonCombatTracking
{
    public static void RecomputeFromRoster(
        IReadOnlyList<Player> roster,
        Func<Player, bool> isReady,
        string category)
    {
        try
        {
            var notReady = roster.Where(p => !isReady(p)).ToList();
            WaitState.OnNotReadyPlayersChanged(notReady, category);
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] {category} recompute failed: {e}");
        }
    }
}

// ---------------------------------------------------------------------
// Treasure chests / relic picking
// ---------------------------------------------------------------------
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), MethodType.Constructor,
    new[] { typeof(IPlayerCollection), typeof(ulong), typeof(ActionQueueSynchronizer), typeof(RelicGrabBag), typeof(Rng) })]
public static class TreasureRoomRelicSynchronizer_Ctor_Patch
{
    [HarmonyPostfix]
    public static void Postfix(TreasureRoomRelicSynchronizer __instance, IPlayerCollection __0)
    {
        try
        {
            var sync = __instance;
            var roster = __0.Players;

            void Recompute() => NonCombatTracking.RecomputeFromRoster(
                roster,
                p => sync.GetPlayerVote(p)?.voteReceived == true,
                WaitCategories.TreasureRelics);

            sync.VotesChanged += Recompute;
            sync.RelicsAwarded += _ => WaitState.OnNotReadyPlayersChanged(Array.Empty<Player>(), WaitCategories.TreasureRelics);
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] Failed to hook TreasureRoomRelicSynchronizer: {e}");
        }
    }
}

// ---------------------------------------------------------------------
// Ancients / events
// ---------------------------------------------------------------------
[HarmonyPatch(typeof(EventSynchronizer), MethodType.Constructor,
    new[] { typeof(RunLocationTargetedMessageBuffer), typeof(INetGameService), typeof(IPlayerCollection), typeof(IRunState), typeof(ulong), typeof(ulong) })]
public static class EventSynchronizer_Ctor_Patch
{
    [HarmonyPostfix]
    public static void Postfix(EventSynchronizer __instance, IPlayerCollection __2)
    {
        try
        {
            var sync = __instance;
            var roster = __2.Players;

            void Recompute()
            {
                // Non-shared events are a single player's own encounter
                // (e.g. running in parallel with teammates elsewhere) --
                // nobody else is actually waiting on it, so skip entirely
                // rather than reporting a misleading roster-wide result.
                if (!sync.IsShared) return;

                NonCombatTracking.RecomputeFromRoster(
                    roster,
                    p => sync.GetPlayerVote(p).HasValue,
                    WaitCategories.AncientsEvents);
            }

            sync.PlayerVoteChanged += _ => Recompute();
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] Failed to hook EventSynchronizer: {e}");
        }
    }
}

// ---------------------------------------------------------------------
// Map / node selection
// ---------------------------------------------------------------------
[HarmonyPatch(typeof(MapSelectionSynchronizer), MethodType.Constructor,
    new[] { typeof(INetGameService), typeof(ActionQueueSynchronizer), typeof(RunState) })]
public static class MapSelectionSynchronizer_Ctor_Patch
{
    [HarmonyPostfix]
    public static void Postfix(MapSelectionSynchronizer __instance, RunState __2)
    {
        try
        {
            var sync = __instance;
            var roster = __2.Players;

            void Recompute() => NonCombatTracking.RecomputeFromRoster(
                roster,
                p => sync.GetVote(p).HasValue,
                WaitCategories.MapSelection);

            sync.PlayerVoteChanged += (_, __, ___) => Recompute();
            sync.PlayerVoteCancelled += _ => Recompute();
            sync.PlayerVotesCleared += Recompute;
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] Failed to hook MapSelectionSynchronizer: {e}");
        }
    }
}

// ---------------------------------------------------------------------
// Post-combat rewards
//
// Architecturally different from the other three: RewardsSetSynchronizer
// has no "something changed" event at all, and each RewardsSet is
// constructed per-player (RewardsSet.Player), not roster-wide. Instead we
// track whichever (player, rewardsSetId) pairs we've actually observed via
// BeginRewardsSet, keyed per player so a new floor's set replaces the old
// one, and recompute against the public IsRewardsSetCompleted(player, id)
// query whenever any of the public reward-selection entry points fire.
// Lower confidence than the other three contexts -- needs a real test to
// confirm BeginRewardsSet actually fires for remote players too, and not
// just the local one.
// ---------------------------------------------------------------------
[HarmonyPatch(typeof(RewardsSetSynchronizer))]
public static class RewardsSetSynchronizer_Patches
{
    private static readonly Dictionary<ulong, (Player Player, int Id)> Tracked = new();

    /// <summary>Call at the start of a new run so stale reward-set ids from a
    /// previous run can't leak in (mirrors WaitState.Initialize()).</summary>
    public static void Reset() => Tracked.Clear();

    [HarmonyPatch("BeginRewardsSet", new[] { typeof(RewardsSet) })]
    [HarmonyPostfix]
    public static void BeginRewardsSet_Postfix(RewardsSetSynchronizer __instance, RewardsSet __0)
    {
        try
        {
            var player = __0.Player;
            if (player is null) return;
            Tracked[player.NetId] = (player, __0.Id);
            Recompute(__instance);
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] BeginRewardsSet hook failed: {e}");
        }
    }

    [HarmonyPatch("SelectLocalReward", new[] { typeof(Reward) })]
    [HarmonyPostfix]
    public static void SelectLocalReward_Postfix(RewardsSetSynchronizer __instance) => Recompute(__instance);

    [HarmonyPatch("HandleRewardSelectedMessage", new[] { typeof(RewardSelectedMessage), typeof(ulong) })]
    [HarmonyPostfix]
    public static void HandleRewardSelectedMessage_Postfix(RewardsSetSynchronizer __instance) => Recompute(__instance);

    [HarmonyPatch("HandleRewardSetSkippedMessage", new[] { typeof(RewardSetSkippedMessage), typeof(ulong) })]
    [HarmonyPostfix]
    public static void HandleRewardSetSkippedMessage_Postfix(RewardsSetSynchronizer __instance) => Recompute(__instance);

    [HarmonyPatch("SkipLocalRewardsSet", new Type[0])]
    [HarmonyPostfix]
    public static void SkipLocalRewardsSet_Postfix(RewardsSetSynchronizer __instance) => Recompute(__instance);

    private static void Recompute(RewardsSetSynchronizer sync)
    {
        try
        {
            var roster = Tracked.Values.Select(t => t.Player).ToList();
            NonCombatTracking.RecomputeFromRoster(
                roster,
                p => Tracked.TryGetValue(p.NetId, out var t) && sync.IsRewardsSetCompleted(t.Player, t.Id),
                WaitCategories.Rewards);
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] RewardsSetSynchronizer recompute failed: {e}");
        }
    }
}

// ---------------------------------------------------------------------
// Rest sites / campfires
//
// Same shape as treasure/relic picking: constructor hands us the roster
// via an IPlayerCollection argument, and GetChosenOptionIndex(netId) is a
// direct public per-player query -- no self-tracking needed for the normal
// "picked an option" path. The one wrinkle: it's unclear whether skipping a
// rest site (as opposed to choosing rest/smith/etc.) also updates
// GetChosenOptionIndex, so HandleRestSiteSkippedMessage (private, network
// handler) is patched separately as a belt-and-suspenders trigger, tracked
// in a small per-instance "skipped" set so a skip counts as "done" even if
// GetChosenOptionIndex never reflects it. Without that, a single skip could
// make this mod think that player is *permanently* the one everyone's
// waiting on for the rest of the room's lifetime.
// ---------------------------------------------------------------------
public static class RestSiteSynchronizer_Patches
{
    private sealed class TrackerState
    {
        public IReadOnlyList<Player> Roster = Array.Empty<Player>();
        public HashSet<ulong> SkippedIds { get; } = new();
    }

    private static readonly Dictionary<RestSiteSynchronizer, TrackerState> Trackers = new();

    /// <summary>Drops references to any rest sites tracked this run.</summary>
    public static void Reset() => Trackers.Clear();

    [HarmonyPatch(typeof(RestSiteSynchronizer), MethodType.Constructor,
        new[] { typeof(RunLocationTargetedMessageBuffer), typeof(INetGameService), typeof(IPlayerCollection), typeof(ulong), typeof(RunLobby) })]
    public static class Ctor_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RestSiteSynchronizer __instance, IPlayerCollection __2)
        {
            try
            {
                var state = new TrackerState { Roster = __2.Players };
                Trackers[__instance] = state;
                __instance.AfterPlayerOptionChosen += (_, __, ___) => Recompute(__instance, state);
            }
            catch (Exception e)
            {
                Log.Error($"[{WaitTrackerMod.ModId}] Failed to hook RestSiteSynchronizer: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(RestSiteSynchronizer), "HandleRestSiteSkippedMessage",
        new[] { typeof(RestSiteSkippedMessage), typeof(ulong) })]
    public static class Skipped_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(RestSiteSynchronizer __instance, ulong __1)
        {
            if (!Trackers.TryGetValue(__instance, out var state)) return;
            state.SkippedIds.Add(__1);
            Recompute(__instance, state);
        }
    }

    private static void Recompute(RestSiteSynchronizer sync, TrackerState state) =>
        NonCombatTracking.RecomputeFromRoster(
            state.Roster,
            p => state.SkippedIds.Contains(p.NetId) || sync.GetChosenOptionIndex(p.NetId).HasValue,
            WaitCategories.RestSites);
}

// ---------------------------------------------------------------------
// Act transitions -- the "ready up" gate after a boss fight, before the
// party moves to the next act.
//
// ActChangeSynchronizer has no per-player "changed" event or query, just a
// public OnPlayerReady(Player, int) called whenever anyone (local or
// remote) readies up, and a private _readyPlayers field it presumably
// tracks internally. Since there's no way to directly query "is this
// player ready" from outside, this self-tracks a ready-id set per
// instance instead, exactly like the rewards-screen tracker. Rather than
// trying to interpret what the int argument means (act index? a count?
// unclear from metadata alone), the tracked set just clears itself once
// the whole roster is accounted for, so the next transition -- whether
// this object gets reused across acts or a fresh one is constructed each
// time -- starts from a clean slate either way.
// ---------------------------------------------------------------------
public static class ActChangeSynchronizer_Patches
{
    private sealed class TrackerState
    {
        public IReadOnlyList<Player> Roster = Array.Empty<Player>();
        public HashSet<ulong> ReadyIds { get; } = new();
    }

    private static readonly Dictionary<ActChangeSynchronizer, TrackerState> Trackers = new();

    /// <summary>Drops references to any act transitions tracked this run.</summary>
    public static void Reset() => Trackers.Clear();

    [HarmonyPatch(typeof(ActChangeSynchronizer), MethodType.Constructor, new[] { typeof(RunState) })]
    public static class Ctor_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ActChangeSynchronizer __instance, RunState __0)
        {
            try
            {
                Trackers[__instance] = new TrackerState { Roster = __0.Players };
            }
            catch (Exception e)
            {
                Log.Error($"[{WaitTrackerMod.ModId}] Failed to hook ActChangeSynchronizer: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(ActChangeSynchronizer), "OnPlayerReady", new[] { typeof(Player), typeof(int) })]
    public static class OnPlayerReady_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ActChangeSynchronizer __instance, Player __0)
        {
            if (!Trackers.TryGetValue(__instance, out var state)) return;

            try
            {
                state.ReadyIds.Add(__0.NetId);
                var notReady = state.Roster.Where(p => !state.ReadyIds.Contains(p.NetId)).ToList();
                WaitState.OnNotReadyPlayersChanged(notReady, WaitCategories.ActTransitions);

                // Everyone's ready for this transition -- clear so the next
                // one starts clean regardless of whether this instance gets
                // reused across acts or a fresh one is constructed each time.
                if (notReady.Count == 0) state.ReadyIds.Clear();
            }
            catch (Exception e)
            {
                Log.Error($"[{WaitTrackerMod.ModId}] ActChangeSynchronizer recompute failed: {e}");
            }
        }
    }
}
