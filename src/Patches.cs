using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;

namespace WaitTracker;

/// <summary>
/// Adds our leaderboard rows to the native end-of-run summary screen.
///
/// ROUND 6 (layout fix + real clock icon + top-offender highlight):
///
/// Disassembled AnimateRunSummary's actual IL (not just its signature) to
/// track down why round 5's rows visually overlapped the death/victory
/// quote text once two extra rows got added. Two things fell out of that:
///
/// 1. AnimateRunSummary is itself `async Task`, and the private
///    AnimateScoreLines() it awaits does `_scoreLines.Clear()` as its very
///    first line before repopulating from scratch with the native rows
///    (Floors Climbed, Gold Gained, etc.) -- each added via
///    `container.AddChildSafely(line)` (a null/tree-safety wrapper, not
///    plain AddChild) *and* `_scoreLines.Add(line)`. Our old approach
///    (Prefix, raw AddChild, no `_scoreLines` bookkeeping) ran before any
///    of that and never told the game about our rows -- if anything
///    downstream sizes/positions this block off `_scoreLines.Count`
///    rather than the container's actual child count (very plausible for
///    a screen that needs to vertically center a variable-length stat
///    list), that mismatch is exactly what would produce this kind of
///    overlap.
/// 2. Because the whole chain is async, neither a Prefix nor a plain
///    Postfix reliably runs "after everything's done" -- both fire at the
///    synchronous call boundary, before the awaited work inside actually
///    completes. The fix is Harmony's task-postfix pattern: a postfix
///    whose own signature returns `Task` and takes the original `Task` as
///    a parameter gets its returned task wired up as the *real* return
///    value, so `await __result;` inside it genuinely waits for the whole
///    native animation chain (including that `_scoreLines.Clear()`) to
///    finish before we touch anything.
///
/// So this round switches to that async postfix, and mirrors the native
/// AddScoreLine path exactly: `AddChildSafely` instead of raw AddChild,
/// plus registering into the private `_scoreLines` list so our rows are
/// indistinguishable from the game's own once added.
///
/// Also: swapped the reused-from-round-4 ascension/flame icon for a real
/// clock, and highlight whoever made others wait the most.
///
/// ROUND 8 (the clock icon didn't actually load -- confirmed from the
/// real godot.log):
///
///   ERROR: Cannot open file 'res://atlases/stats_screen_atlas.sprites/stats_clock.tres'.
///
/// Round 6's guessed path never existed. Scanned the *entire* string heap
/// of sts2.dll for the literal word "clock" (not just res:// paths) and
/// got zero matches anywhere in the assembly -- the game genuinely ships
/// no clock/hourglass icon at all. The game-over screen's own icon set is
/// exactly: score_floor.png, score_gold.png, score_ascension.png,
/// score_boss.png, score_elite.png. That's also why the gold highlight
/// looked invisible: it was multiplying GoldTint over the *already
/// orange* ascension fallback icon, not a neutral one.
///
/// Fix: stop guessing at game resource paths and ship our own icon. A
/// small clock PNG (plain white, so GoldTint reads clearly against it)
/// now lives at assets/clock.png next to WaitTracker.dll, and we load it
/// straight off disk with Image.Load() instead of GD.Load("res://...").
/// To find "next to WaitTracker.dll" without hardcoding an absolute
/// install path (that would break for anyone whose Steam library isn't
/// on C:), we ask the game's own mod loader: ModManager.GetLoadedMods()
/// returns the public Mod list, each with a public `manifest.id` and a
/// public `path` (confirmed via metadata -- both public fields/props, no
/// reflection needed), so we find the Mod whose manifest.id matches ours
/// and use its path.
///
/// ROUND 9 (hover breakdown by category):
///
/// WaitState now tracks seconds per (player, category) instead of just a
/// grand total per player (see WaitCategories/WaitState). To surface that
/// on the row, we use Godot's native tooltip: any Control has a
/// `TooltipText` string property, and the engine shows it automatically on
/// hover after a short delay -- no custom popup/widget needed. The only
/// wrinkle is that tooltips in Godot belong to whichever specific Control
/// the cursor is actually over, they don't bubble up from a child to its
/// parent -- so setting TooltipText only on the NScoreLine root would miss
/// hovers that land on its inner label/icon controls (very likely, since
/// "hover over the wait time" mostly means hovering the score label
/// itself). Same fix shape as HighlightGold below: walk the whole node
/// tree and set TooltipText on every Control in it, root included, so
/// wherever the cursor actually lands already has the right text.
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), "AnimateRunSummary")]
public static class GameOverScreen_AnimateRunSummary_Patch
{
    private static readonly FieldInfo? ScoreLineContainerField =
        AccessTools.Field(typeof(NGameOverScreen), "_scoreLineContainer");

    private static readonly FieldInfo? ScoreLinesField =
        AccessTools.Field(typeof(NGameOverScreen), "_scoreLines");

    // ROUND 8: no clock icon exists anywhere in the game (confirmed by
    // scanning sts2.dll's whole string heap), so we ship our own and load
    // it from disk next to WaitTracker.dll instead of guessing a res://
    // path. Only used as an absolute last resort if that fails entirely.
    private const string BundledIconFileName = "clock.png";
    private const string FallbackIconPath = "res://images/ui/game_over_screen/score_ascension.png";

    private static Texture2D? _cachedIcon;

    private static readonly Color GoldTint = new(1.0f, 0.843f, 0.0f);

    [HarmonyPostfix]
    public static async Task Postfix(Task __result, NGameOverScreen __instance)
    {
        // Wait for the real native animation chain -- score lines, badges,
        // score bar, all of it -- to finish before we touch the container.
        await __result;

        try
        {
            var container = ScoreLineContainerField?.GetValue(__instance) as Node;
            var scoreLines = ScoreLinesField?.GetValue(__instance) as System.Collections.IList;
            if (container is null)
            {
                Log.Error($"[{WaitTrackerMod.ModId}] Could not find _scoreLineContainer via reflection " +
                          "-- the game's internals may have changed since this mod was built.");
                return;
            }

            var icon = ResolveClockIcon();

            var totals = new List<(string DisplayName, double Seconds, IReadOnlyDictionary<string, double> ByCategory)>(WaitState.GetTotalsSorted());
            var rows = 0;
            var isTopOffender = true;

            foreach (var (displayName, seconds, byCategory) in totals)
            {
                if (seconds < 1) continue; // skip players who never made anyone wait

                var line = NScoreLine.Create(
                    label: $"{displayName} made others wait",
                    score: FormatSeconds(seconds),
                    icon: icon);

                // Mirror the native AddScoreLine path exactly: AddChildSafely
                // (not raw AddChild) and register into _scoreLines too, so
                // our rows behave identically to the game's own from here on.
                container.AddChildSafely(line);
                scoreLines?.Add(line);

                SetTooltipRecursive(line, BuildTooltip(byCategory));

                if (isTopOffender)
                {
                    HighlightGold(line);
                    isTopOffender = false;
                }

                _ = AnimateInSafely(line);
                rows++;
            }

            Log.Info($"[{WaitTrackerMod.ModId}] Added {rows} wait-time row(s) to the end-of-run screen.");
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] Failed to add wait-time rows: {e}");
        }
        finally
        {
            // Totals are per-run; reset here (once, after the screen that
            // reads them) so a second run in the same session doesn't show
            // combined totals from both.
            WaitState.Initialize();
            RewardsSetSynchronizer_Patches.Reset();
            RestSiteSynchronizer_Patches.Reset();
            ActChangeSynchronizer_Patches.Reset();
        }
    }

    /// <summary>
    /// Loads our bundled clock.png from disk, next to WaitTracker.dll.
    /// The mod's own install directory is asked from the game's mod
    /// loader (ModManager.GetLoadedMods()) rather than derived from
    /// Assembly.Location or a hardcoded path, since we don't know how
    /// this mod's assembly was loaded (byte-array loads leave Location
    /// blank) or where the user's Steam library lives. Caches the result
    /// so repeated end-of-run screens (multiple runs in one session)
    /// don't reload/log every time. Falls back to the old ascension icon
    /// if anything here goes wrong, so a bad install never blanks the row.
    /// </summary>
    private static Texture2D? ResolveClockIcon()
    {
        if (_cachedIcon is not null) return _cachedIcon;

        try
        {
            var mod = ModManager.GetLoadedMods().FirstOrDefault(m => m?.manifest?.id == WaitTrackerMod.ModId);
            if (mod?.path is string modDir)
            {
                var iconPath = Path.Combine(modDir, BundledIconFileName);
                if (File.Exists(iconPath))
                {
                    var image = new Image();
                    var err = image.Load(iconPath);
                    if (err == Error.Ok)
                    {
                        _cachedIcon = ImageTexture.CreateFromImage(image);
                        return _cachedIcon;
                    }

                    Log.Error($"[{WaitTrackerMod.ModId}] Failed to load bundled clock icon at " +
                              $"{iconPath}: {err}");
                }
                else
                {
                    Log.Error($"[{WaitTrackerMod.ModId}] Bundled clock icon not found at {iconPath} " +
                              "-- make sure clock.png shipped alongside WaitTracker.dll.");
                }
            }
            else
            {
                Log.Error($"[{WaitTrackerMod.ModId}] Could not resolve this mod's own install directory " +
                          "via ModManager.GetLoadedMods().");
            }
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] Failed to load custom clock icon: {e}");
        }

        _cachedIcon = GD.Load<Texture2D>(FallbackIconPath);
        return _cachedIcon;
    }

    /// <summary>
    /// Tints the row's icon gold to flag the worst offender. NScoreLine is
    /// built from a packed scene at runtime (no named C# fields to reach
    /// into directly), so this walks the node tree looking for the actual
    /// icon control by type rather than by a guessed node path. Falls back
    /// to tinting the whole row if no icon node is found, so the highlight
    /// degrades gracefully instead of silently doing nothing.
    /// </summary>
    private static void HighlightGold(Node line)
    {
        var icon = FindTextureRect(line);
        if (icon is not null)
        {
            icon.Modulate = GoldTint;
        }
        else if (line is CanvasItem ci)
        {
            ci.Modulate = GoldTint;
        }
    }

    private static TextureRect? FindTextureRect(Node node)
    {
        if (node is TextureRect tr) return tr;
        foreach (var child in node.GetChildren())
        {
            var found = FindTextureRect(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Builds the hover text: one "Category: M:SS" line per category that
    /// actually accumulated time for this player, worst-category-first.
    /// Categories under a second are dropped, same threshold as whole-row
    /// display, so the tooltip never shows a breakdown that adds up to
    /// noticeably less than the row's own total from rounding noise alone.
    /// </summary>
    private static string BuildTooltip(IReadOnlyDictionary<string, double> byCategory)
    {
        var lines = byCategory
            .Where(kv => kv.Value >= 1)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Key}: {FormatSeconds(kv.Value)}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Sets TooltipText on every Control in the row, root included. Godot
    /// tooltips belong to whichever specific Control is under the cursor --
    /// they don't bubble up from a child to its parent -- and NScoreLine is
    /// a packed scene with no named fields, so there's no reliable way to
    /// know in advance which inner label/icon control the cursor is most
    /// likely to land on. Setting it everywhere sidesteps that: no matter
    /// which control catches the hover, it already has the same text.
    /// </summary>
    private static void SetTooltipRecursive(Node node, string tooltip)
    {
        if (node is Control control)
        {
            control.MouseFilter = Control.MouseFilterEnum.Stop;
            control.TooltipText = tooltip;
        }

        foreach (var child in node.GetChildren())
        {
            SetTooltipRecursive(child, tooltip);
        }
    }

    private static async Task AnimateInSafely(NScoreLine line)
    {
        try
        {
            await line.AnimateIn();
        }
        catch (Exception e)
        {
            Log.Error($"[{WaitTrackerMod.ModId}] AnimateIn failed for a wait-time row: {e}");
        }
    }

    private static string FormatSeconds(double totalSeconds)
    {
        var t = TimeSpan.FromSeconds(totalSeconds);
        return t.Hours > 0
            ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }
}
