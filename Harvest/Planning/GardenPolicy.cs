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

    public static bool LimitOrdinaryBatch(GardenBatch batch, IEnumerable<GardenCell> cells, int required, int owned)
    {
        if (batch.Cross) return false;
        var selected = cells.Where(c => batch.Cells.Contains(c.Id)).ToArray();
        var started = selected.Where(c => c.Owned || c.Pending.Length > 0).ToArray();
        var remaining = Math.Max(0, required - owned - started.Length);
        var ids = started.Concat(selected.Except(started).Take(remaining)).Select(c => c.Id).ToList();
        var changed = batch.Required != required || !batch.Cells.SequenceEqual(ids);
        batch.Required = required; batch.Cells = ids;
        return changed;
    }

    public static int RequiredSlots(IEnumerable<(uint Item, int Count, int Stack, int Room)> outputs) =>
        outputs.GroupBy(o => o.Item).Sum(g => {
            var first = g.First();
            if (first.Stack <= 0 || first.Room < 0 || g.Any(o => o.Count < 0)) throw new ArgumentException("Invalid harvest capacity");
            return (int)Math.Ceiling(Math.Max(0, g.Sum(o => (long)o.Count) - first.Room) / (double)first.Stack);
        });
}
