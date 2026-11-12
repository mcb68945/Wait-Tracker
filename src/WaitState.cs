using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Platform;

namespace WaitTracker;

/// <summary>
/// Holds the current run's cumulative "wait caused" data: for each player
/// (keyed by their stable <see cref="Player.NetId"/>), how much total time
/// everyone else spent idle while that player was still the one acting --
/// broken down by which wait-tracking context (<see cref="WaitCategories"/>)
/// caused it.
///
/// Model: at any moment during a multiplayer combat (or map selection, or
/// any of the other tracked contexts), some subset of players have already
/// finished their part (CombatTurnState.PlayersReadyToEndTurn, or the
/// equivalent per-context "ready" query) and are idle, waiting on the rest.
/// Whenever that "not yet ready" set shrinks to exactly one player, we start
/// a stopwatch for them tagged with the calling context; whenever it changes
/// again (someone else also finishes, someone backs out, or the context
/// ends), we stop the stopwatch and add the elapsed time to that player's
/// running total *for that category*.
///
/// ROUND 9: previously this only kept one grand total per player. Now it
/// keeps one total per (player, category) pair as well, so the end-of-run
/// screen can show a hover breakdown ("Combat: 4:02, Rest Sites: 0:18, ...")
/// instead of just the combined number.
/// </summary>
public static class WaitState
{
    private sealed class PlayerTotal
    {
        public string DisplayName = "Player";
        public Dictionary<string, double> SecondsByCategory { get; } = new();
        public double TotalSeconds => SecondsByCategory.Values.Sum();
    }

    // NetId -> running totals
    private static readonly Dictionary<ulong, PlayerTotal> Totals = new();

    private static ulong? _soleActiveNetId;
    private static string _currentCategory = WaitCategories.Combat;
    private static DateTime _segmentStartUtc;

    public static void Initialize()
    {
        Totals.Clear();
        _soleActiveNetId = null;
    }

    /// <summary>
    /// Call whenever the set of players who have NOT yet finished their part
    /// of the given context changes (e.g. after a combat PlayerEndedTurn, a
    /// map vote, a reward pick, or a new round/screen starting).
    /// <paramref name="category"/> should be one of <see cref="WaitCategories"/>
    /// -- it's only actually used when <paramref name="notReadyPlayers"/> has
    /// exactly one entry (that's the only case that opens a new timed
    /// segment), so passing anything for an empty/multi-player list is
    /// harmless.
    /// </summary>
    public static void OnNotReadyPlayersChanged(IReadOnlyCollection<Player> notReadyPlayers, string category)
    {
        CloseCurrentSegment();

        if (notReadyPlayers.Count == 1)
        {
            var p = notReadyPlayers.First();
            _soleActiveNetId = p.NetId;
            _currentCategory = category;
            EnsureEntry(p.NetId, DisplayNameFor(p));
            _segmentStartUtc = DateTime.UtcNow;
        }
        else
        {
            _soleActiveNetId = null;
        }
    }

    /// <summary>Call when combat ends, to flush any open segment.</summary>
    public static void OnCombatEnded()
    {
        CloseCurrentSegment();
    }

    private static void CloseCurrentSegment()
    {
        if (_soleActiveNetId is not ulong netId) return;

        var elapsed = (DateTime.UtcNow - _segmentStartUtc).TotalSeconds;
        if (elapsed > 0 && Totals.TryGetValue(netId, out var total))
        {
            total.SecondsByCategory.TryGetValue(_currentCategory, out var existing);
            total.SecondsByCategory[_currentCategory] = existing + elapsed;
        }

        _soleActiveNetId = null;
    }

    private static void EnsureEntry(ulong netId, string displayName)
    {
        if (!Totals.ContainsKey(netId))
        {
            Totals[netId] = new PlayerTotal { DisplayName = displayName };
        }
    }

    private static string DisplayNameFor(Player p)
    {
        // Player.NetId lines up numerically with the SteamID64 values seen
        // in the game's own log (e.g. "Player 7656119XXXXXXXXXX playing
        // card..."), and PlatformUtil.GetPlayerName(PlatformType, ulong) is
        // a public API built exactly for resolving a display name from a
        // platform id -- PlatformType only has None/Steam, and this game is
        // Steam-only (steam_api64.dll is bundled), so PlatformType.Steam is
        // the only real choice.
        try
        {
            var name = PlatformUtil.GetPlayerName(PlatformType.Steam, p.NetId);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // fall through to the character-based fallback below
        }

        return CharacterFallback(p);
    }

    /// <summary>
    /// Fallback if the Steam name lookup above ever fails or returns empty
    /// (e.g. offline/LAN play). p.Character.ToString() returns something
    /// like "CHARACTER.REGENT (66664156)" (a model-id-style string plus an
    /// instance hash) -- confirmed from a real test run's log. Clean that
    /// up into something presentable: "Regent".
    /// </summary>
    private static string CharacterFallback(Player p)
    {
        try
        {
            var raw = p.Character.ToString();
            var parenIndex = raw.IndexOf(" (", StringComparison.Ordinal);
            if (parenIndex >= 0) raw = raw[..parenIndex];

            var dotIndex = raw.LastIndexOf('.');
            var name = dotIndex >= 0 ? raw[(dotIndex + 1)..] : raw;

            if (name.Length == 0) return $"Player {p.NetId}";
            return char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant();
        }
        catch
        {
            return $"Player {p.NetId}";
        }
    }

    /// <summary>
    /// Returns (displayName, totalSeconds, secondsByCategory) sorted worst-
    /// offender first. secondsByCategory only contains categories that ever
    /// had time attributed to them for that player.
    /// </summary>
    public static IEnumerable<(string DisplayName, double Seconds, IReadOnlyDictionary<string, double> ByCategory)> GetTotalsSorted()
    {
        CloseCurrentSegment();
        return Totals.Values
            .OrderByDescending(t => t.TotalSeconds)
            .Select(t => (t.DisplayName, t.TotalSeconds, (IReadOnlyDictionary<string, double>)t.SecondsByCategory));
    }
}
