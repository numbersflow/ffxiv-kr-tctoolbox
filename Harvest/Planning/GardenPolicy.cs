using System;
using System.Collections.Generic;
using System.Linq;

namespace Harvest.Planning;

public static class GardenPolicy
{
    public static uint Soil(bool cross, uint crossingSoil) => cross ? crossingSoil : 16026u;

    public static bool CanVisit(GardenCell cell, IReadOnlyDictionary<uint, Stock> stock, uint soil) =>
        cell.Pending.Length > 0 || cell.Owned
        || stock.GetValueOrDefault(cell.Seed)?.Inventory > 0 && stock.GetValueOrDefault(soil)?.Inventory > 0;
    // The second cell stays planted, including when both parents use the same seed.
    public static bool IsAnchor(GardenBatch batch, GardenCell cell) =>
        batch.Cross && !batch.Clearing && batch.Cells.Count > 1 && batch.Cells[1] == cell.Id;

    public static int PlantingCells(int required, int owned, int yield)
    {
        if (required < 0 || owned < 0 || yield <= 0) throw new ArgumentOutOfRangeException(nameof(yield));
        return (int)((Math.Max(0L, (long)required - owned) + yield - 1) / yield);
    }

    public static bool LimitOrdinaryBatch(GardenBatch batch, IEnumerable<GardenCell> cells, int required, int owned, int yield = 1)
    {
        if (batch.Cross) return false;
        var selected = cells.Where(c => batch.Cells.Contains(c.Id)).ToArray();
        var started = selected.Where(c => c.Owned || c.Pending.Length > 0).ToArray();
        var remaining = Math.Max(0, PlantingCells(required, owned, yield) - started.Length);
        var ids = started.Concat(selected.Except(started).Take(remaining)).Select(c => c.Id).ToList();
        var changed = batch.Required != required || !batch.Cells.SequenceEqual(ids);
        batch.Required = required; batch.Cells = ids;
        return changed;
    }

    public static bool InvalidateHarvestedCells(IEnumerable<GardenCell> cells, IReadOnlyDictionary<uint, Stock> before, IReadOnlyDictionary<uint, Stock> after)
    {
        var changed = false;
        foreach (var cell in cells.Where(c => c.Owned && c.Pending.Length == 0 && c.Crop != 0)) {
            if (!before.TryGetValue(cell.Crop, out var previous) || !after.TryGetValue(cell.Crop, out var current)
                || current.Inventory <= previous.Inventory || current.Total <= previous.Total) continue;
            // Inventory gains cannot identify the emptied slot; verify each matching crop in game.
            cell.Observed = DateTime.MinValue; cell.RetryAt = DateTime.MinValue; changed = true;
        }
        return changed;
    }

    public static Dictionary<uint,int> SoilReserve(HarvestProfile profile, IReadOnlyDictionary<uint,Crop> crops,
        IReadOnlyDictionary<uint,uint> seedCrops, IEnumerable<Cross> crosses, IReadOnlyDictionary<uint,Stock> stock)
    {
        var slots = Math.Clamp(profile.Cells.Count(c => c.Enabled && c.Kind == "plot"), 0, 24);
        if (slots == 0 || profile.Soil is < 7758 or > 7766) return [];
        var selected = profile.BookItems.Concat(profile.Goals.Where(g => g.Enabled && !g.Complete && g.Quantity > 0).Select(g => g.Item)).Distinct();
        foreach (var item in selected) {
            var seed = crops.TryGetValue(item, out var crop) ? crop.Seed : seedCrops.ContainsKey(item) ? item : 0;
            if (seed == 0 || crop?.Flower == true) continue;
            if (profile.Batch is { Cross: true, Clearing: false } batch && batch.Goal == item
                || stock.GetValueOrDefault(seed)?.CanProcure != true && crosses.Any(c => c.Output == seed))
                return new() { [profile.Soil] = slots * 3 };
        }
        return [];
    }

    public static int RequiredSlots(IEnumerable<(uint Item, int Count, int Stack, int Room)> outputs) =>
        outputs.GroupBy(o => o.Item).Sum(g => {
            var first = g.First();
            if (first.Stack <= 0 || first.Room < 0 || g.Any(o => o.Count < 0)) throw new ArgumentException("Invalid harvest capacity");
            return (int)Math.Ceiling(Math.Max(0, g.Sum(o => (long)o.Count) - first.Room) / (double)first.Stack);
        });
}
