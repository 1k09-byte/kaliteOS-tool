using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Native;

namespace kaliteConfig.Gaming.GameMode;

public sealed record GameModeCpuPlan(
    bool Available,
    IReadOnlyList<uint> GameIds,
    IReadOnlyList<uint> BackgroundIds,
    IReadOnlyList<uint> SystemIds,
    string Summary,
    string? UnavailableReason);

public static class GameModeCpuPlanner
{
    private const int MinimumUsableSets = 4;
    private const int MinimumGameSets = 2;

    public static GameModeCpuPlan Plan(IReadOnlyList<CpuSetEntry> topology, ulong? reservedMask)
    {
        var systemIds = topology.Select(t => t.Id).ToList();
        if (systemIds.Count == 0)
            return Unavailable(systemIds, "No CPU sets were reported by Windows.");

        var reserved = ReservedIds(topology, reservedMask);
        var usable = topology.Where(t => !reserved.Contains(t.Id)).ToList();
        if (usable.Count < MinimumUsableSets)
            return Unavailable(systemIds, "Fewer than four usable CPU sets are available for isolation.");

        var ranks = usable.Select(t => t.EfficiencyClass).Distinct().OrderByDescending(v => v).ToList();
        if (ranks.Count > 1)
            return PlanHybrid(systemIds, usable, ranks, reserved.Count);

        return PlanNonHybrid(systemIds, usable, reserved.Count);
    }

    private static GameModeCpuPlan PlanHybrid(
        List<uint> systemIds,
        List<CpuSetEntry> usable,
        List<byte> ranks,
        int reservedCount)
    {
        var game = usable.Where(t => t.EfficiencyClass == ranks[0]).Select(t => t.Id).ToList();
        var background = usable.Where(t => t.EfficiencyClass != ranks[0]).Select(t => t.Id).ToList();
        if (game.Count < MinimumGameSets || background.Count == 0)
            return Unavailable(systemIds, "The efficiency-class split is too small to isolate safely.");

        var reservedText = reservedCount > 0 ? $", {reservedCount} reserved set(s) excluded" : string.Empty;
        return new GameModeCpuPlan(
            true,
            game,
            background,
            systemIds,
            $"Isolating background work to {background.Count} efficiency-class set(s); game keeps {game.Count} performance set(s){reservedText}.",
            null);
    }

    private static GameModeCpuPlan PlanNonHybrid(List<uint> systemIds, List<CpuSetEntry> usable, int reservedCount)
    {
        var cacheGroups = usable.GroupBy(t => t.LastLevelCacheIndex)
            .OrderBy(g => g.Count())
            .ToList();
        foreach (var group in cacheGroups)
        {
            var background = group.Select(t => t.Id).ToList();
            var game = usable.Where(t => t.LastLevelCacheIndex != group.Key).Select(t => t.Id).ToList();
            if (background.Count >= 1 && game.Count >= MinimumGameSets)
                return new GameModeCpuPlan(true, game, background, systemIds, Describe(game.Count, background.Count, reservedCount), null);
        }

        var cores = usable.GroupBy(t => (t.Group, t.CoreIndex))
            .OrderBy(g => g.Min(t => t.Id))
            .ToList();
        if (cores.Count < MinimumUsableSets)
            return Unavailable(systemIds, "Fewer than four physical cores are available for isolation.");

        int backgroundCores = Math.Max(1, cores.Count / 4);
        var backgroundIds = cores.Skip(cores.Count - backgroundCores).SelectMany(g => g.Select(t => t.Id)).ToList();
        var gameIds = cores.Take(cores.Count - backgroundCores).SelectMany(g => g.Select(t => t.Id)).ToList();
        if (gameIds.Count < MinimumGameSets || backgroundIds.Count == 0)
            return Unavailable(systemIds, "The core split is too small to isolate safely.");

        return new GameModeCpuPlan(true, gameIds, backgroundIds, systemIds, Describe(gameIds.Count, backgroundIds.Count, reservedCount), null);
    }

    private static string Describe(int game, int background, int reservedCount)
    {
        var reservedText = reservedCount > 0 ? $", {reservedCount} reserved set(s) excluded" : string.Empty;
        return $"Isolating background work to {background} CPU set(s); game keeps {game} set(s){reservedText}.";
    }

    private static GameModeCpuPlan Unavailable(List<uint> systemIds, string reason) =>
        new(false, new List<uint>(), new List<uint>(), systemIds, "Core isolation is unavailable: " + reason, reason);

    private static HashSet<uint> ReservedIds(IReadOnlyList<CpuSetEntry> topology, ulong? reservedMask)
    {
        var reserved = new HashSet<uint>();
        if (reservedMask is not { } mask || mask == 0) return reserved;
        foreach (var entry in topology)
        {
            if (entry.Group != 0 || entry.LogicalIndex >= 64) continue;
            if ((mask & (1UL << entry.LogicalIndex)) != 0) reserved.Add(entry.Id);
        }
        return reserved;
    }
}
