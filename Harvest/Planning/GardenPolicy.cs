using System;
using System.Collections.Generic;
using System.Linq;

namespace Harvest.Planning;

public static class GardenPolicy
{
    public static string FacilityKey(GardenCell cell) => $"{cell.World}:{cell.Territory}:{cell.House}:{cell.Kind}:{(cell.Kind == "pot" ? "" : cell.Group)}";

    public static uint Soil(bool cross, uint crossingSoil) => cross ? crossingSoil : 16026u;

    public static bool CanVisit(GardenCell cell, IReadOnlyDictionary<uint, Stock> stock, uint soil, bool plantingReady = true) =>
        cell.Pending.Length > 0 || cell.Owned
        || plantingReady && stock.GetValueOrDefault(cell.Seed)?.Inventory > 0 && stock.GetValueOrDefault(soil)?.Inventory > 0;
    // The second cell stays planted, including when both parents use the same seed.
    public static bool IsAnchor(GardenBatch batch, GardenCell cell) =>
        batch.Cross && !batch.Clearing && batch.Cells.Count > 1 && batch.Cells[1] == cell.Id;

    public static bool PlantingReady(GardenBatch batch, IEnumerable<GardenCell> cells, IReadOnlyDictionary<uint,Stock> stock, uint soil, IReadOnlyDictionary<uint,int>? materials = null)
    {
        if (batch.Clearing) return false;
        var empty = cells.Where(c => c.Enabled && batch.Cells.Contains(c.Id) && !c.Owned && c.Pending.Length == 0).ToArray();
        return (stock.GetValueOrDefault(soil)?.Inventory ?? 0) >= empty.Length
            && empty.GroupBy(c => c.Seed).All(g => (stock.GetValueOrDefault(g.Key)?.Inventory ?? 0) >= g.Count())
            && (materials == null || materials.All(p => (stock.GetValueOrDefault(p.Key)?.Inventory ?? 0) >= p.Value));
    }

    public static bool AdjacentSlots(GardenCell cell, GardenCell other, IEnumerable<GardenCell> cells)
    {
        bool SameBed(GardenCell c) => c.Kind == "plot" && cell.Kind == "plot" && c.World == cell.World
            && c.Territory == cell.Territory && c.House == cell.House
            && System.Numerics.Vector3.DistanceSquared(c.Position, cell.Position) < 0.75f * 0.75f;
        if (!SameBed(other)) return false;
        var indices = cells.Where(SameBed).Select(c => c.Index).ToHashSet();
        // A partial or legacy registration cannot prove which slots surround the crossing.
        if (indices.Count is not (2 or 4 or 8) || !Enumerable.Range(0, indices.Count).All(indices.Contains)) return true;
        var distance = Math.Abs(cell.Index - other.Index);
        return distance <= 1 || distance == indices.Count - 1;
    }

    public static IEnumerable<GardenCell[]> CrossPairs(IReadOnlyCollection<GardenCell> cells) =>
        cells.Where(c => c.Kind == "plot").GroupBy(c => (c.Group, c.House, c.World, c.Territory, c.X, c.Y, c.Z))
            .Where(g => g.Count() is 2 or 4 or 8 && g.Select(c => c.Index).Distinct().Count() == g.Count()
                && Enumerable.Range(0, g.Count()).All(i => g.Any(c => c.Index == i)))
            .SelectMany(g => g.Where(c => c.Enabled).SelectMany(a => g.Where(b => b.Enabled && a.Index < b.Index
                && AdjacentSlots(a, b, cells)).Select(b => new[] { a, b })));

    public static bool AlignCrossAnchor(GardenBatch batch, IEnumerable<GardenCell> cells, IReadOnlyDictionary<uint, Crop> crops, IReadOnlyDictionary<uint, uint> seeds)
    {
        if (!batch.Cross || batch.Clearing || batch.Cells.Count != 2) return false;
        var parents = cells.Where(c => batch.Cells.Contains(c.Id)).ToArray();
        if (parents.Length != 2 || parents.Any(c => !c.Owned || c.Pending.Length > 0 || c.Planted == DateTime.MinValue)) return false;
        var aHours = crops.GetValueOrDefault(seeds.GetValueOrDefault(parents[0].Seed))?.Hours ?? 0;
        var bHours = crops.GetValueOrDefault(seeds.GetValueOrDefault(parents[1].Seed))?.Hours ?? 0;
        var timed = double.IsFinite(aHours) && double.IsFinite(bHours) && aHours > 0 && bHours > 0 && aHours != bHours;
        if (!timed && parents[0].Planted == parents[1].Planted) return false;
        var anchor = timed ? (aHours > bHours ? parents[0] : parents[1]) : parents.OrderBy(c => c.Planted).First();
        if (batch.Cells[1] == anchor.Id) return false;
        batch.Cells = [parents.Single(c => c.Id != anchor.Id).Id, anchor.Id];
        return true;
    }

    public static bool FitsGrowthWindow(GardenBatch crossing, IEnumerable<GardenCell> cells,
        IReadOnlyDictionary<uint, Crop> crops, IReadOnlyDictionary<uint, uint> seeds, double hours, DateTime now)
    {
        if (!crossing.Cross || crossing.Clearing || !double.IsFinite(hours) || hours <= 0) return false;
        var parents = cells.Where(c => crossing.Cells.Contains(c.Id)).ToArray();
        if (parents.Length != crossing.Cells.Count || parents.Length < 2 || parents.Any(c => !c.Owned || c.Pending.Length > 0
            || c.State is not ("growing" or "mature") || c.Observed == DateTime.MinValue || c.Error.Length > 0 || c.Planted == DateTime.MinValue || c.Planted > now
            || !seeds.TryGetValue(c.Seed, out var item) || !crops.TryGetValue(item, out var crop) || !double.IsFinite(crop.Hours) || crop.Hours <= 0)) return false;
        var producer = parents.Single(c => c.Id == crossing.Cells[0]);
        if (producer.State != "growing") return false;
        var remaining = crops[seeds[producer.Seed]].Hours - (now - producer.Planted).TotalHours;
        // Leave an hour for travel and timed work; actual occupied neighbors still block cross planting if late.
        return remaining >= hours + 1;
    }

    public static bool CrossPlantingClear(GardenBatch batch, GardenCell cell, IReadOnlyCollection<GardenCell> cells) =>
        !batch.Cross || !cells.Any(other => !batch.Cells.Contains(other.Id) && AdjacentSlots(cell, other, cells)
            && (other.Owned || other.Pending.Length > 0 || other.Crop != 0 && other.State != "empty"));

    public static bool CanPlant(HarvestProfile profile, GardenCell cell, IReadOnlyDictionary<uint, Crop> crops,
        IReadOnlyDictionary<uint, uint> seeds, DateTime now)
    {
        var batch = profile.BatchFor(cell);
        if (batch == null || batch.Clearing) return false;
        if (batch.Cross) return CrossPlantingClear(batch, cell, profile.Cells);
        var hours = crops.GetValueOrDefault(batch.Output)?.Hours ?? 0;
        return !profile.ActiveBatches.Any(cross => cross.Cross && !cross.Clearing
            && cross.Cells.Any(id => profile.Cells.Any(parent => parent.Id == id && AdjacentSlots(cell, parent, profile.Cells)))
            && !FitsGrowthWindow(cross, profile.Cells, crops, seeds, hours, now));
    }

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
        var remaining = Math.Max(0, PlantingCells(required, owned, yield) - started.Count(c => c.Enabled));
        var ids = started.Concat(selected.Except(started).Where(c => c.Enabled).Take(remaining)).Select(c => c.Id).ToList();
        var changed = batch.Required != required || !batch.Cells.SequenceEqual(ids);
        batch.Required = required; batch.Cells = ids;
        return changed;
    }

    public static bool InvalidateHarvestedCells(IEnumerable<GardenCell> cells, IReadOnlyDictionary<uint, Stock> before, IReadOnlyDictionary<uint, Stock> after)
    {
        var changed = false;
        foreach (var cell in cells.Where(c => c.Pending.Length == 0 && c.Crop != 0)) {
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
            if (profile.ActiveBatches.Any(batch => batch.Cross && !batch.Clearing && batch.Goal == item)
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
