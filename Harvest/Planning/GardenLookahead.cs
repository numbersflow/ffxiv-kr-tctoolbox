using System;
using System.Collections.Generic;
using System.Linq;

namespace Harvest.Planning;

public sealed record GardenReservation(uint Output, GardenCell[] Pair, DateTime PlantAt, string[] Neighbors, DateTime NeighborDeadline);

public static class GardenLookahead
{
    public static List<GardenReservation> Build(HarvestProfile profile, IReadOnlyDictionary<uint, Crop> crops,
        IReadOnlyDictionary<uint, uint> seeds, IEnumerable<SeedPlan> plans, DateTime now)
    {
        var result = new List<GardenReservation>();
        var batches = profile.ActiveBatches.ToArray();
        double Hours(uint seed) => crops.GetValueOrDefault(seeds.GetValueOrDefault(seed))?.Hours ?? 0;
        DateTime End(DateTime start, double hours) => double.IsFinite(hours) && hours > 0
            && hours < (DateTime.MaxValue - start).TotalHours ? start.AddHours(hours) : DateTime.MaxValue;
        DateTime Mature(GardenCell c) => c.State == "mature" ? now
            : c.State == "growing" && c.Planted != DateTime.MinValue && c.Error.Length == 0
                ? End(c.Planted, Hours(c.Seed)) : DateTime.MaxValue;
        DateTime Release(GardenCell c) {
            if (c.Pending.Length > 0) return DateTime.MaxValue;
            if (!c.Owned) return c.Crop == 0 || c.State == "empty" ? now : DateTime.MaxValue;
            if (!c.Enabled || profile.BatchFor(c) is not { } batch || batch.Cross && !batch.Clearing) return DateTime.MaxValue;
            return new[] { Mature(c), c.RetryAt, now.AddTicks(1) }.Max();
        }
        var free = profile.Cells.ToDictionary(c => c.Id, Release);
        string[] Neighbors(GardenCell[] pair) => profile.Cells.Where(c => !pair.Contains(c)
            && pair.Any(p => GardenPolicy.AdjacentSlots(p, c, profile.Cells))).Select(c => c.Id).ToArray();
        var pairs = GardenPolicy.CrossPairs(profile.Cells).Select(p => (Pair: p, Neighbors: Neighbors(p))).ToArray();
        var ready = new Dictionary<uint, DateTime>();

        foreach (var batch in batches.Where(b => b.Cross && !b.Clearing && b.Cells.Count == 2)) {
            var pair = batch.Cells.Select(id => profile.Cells.Single(c => c.Id == id)).ToArray();
            var neighbors = Neighbors(pair);
            var planted = pair.All(c => c.Owned && c.Pending.Length == 0 && c.Observed != DateTime.MinValue && c.Error.Length == 0);
            var finish = planted ? new[] { now, Mature(pair[0]) }.Max() : End(now, Hours(pair[0].Seed));
            if (neighbors.Any(id => free[id] == DateTime.MaxValue)) finish = DateTime.MaxValue;
            Reserve(batch.Output, pair, neighbors, now, finish, planted && finish != DateTime.MaxValue ? finish : now,
                pair[0].Owned ? Mature(pair[0]) : End(now, Hours(pair[0].Seed)),
                pair[1].Owned ? Mature(pair[1]) : End(now, Hours(pair[1].Seed)));
        }

        foreach (var plan in plans.Where(p => p.Error.Length == 0)) {
            // Future placements are estimates. Rebuild them after every verified harvest.
            foreach (var stage in plan.Stages.Take(3)) {
                if (ready.ContainsKey(stage.Output)) continue;
                var inputs = new[] { ready.GetValueOrDefault(stage.A, now), ready.GetValueOrDefault(stage.B, now) }.Max();
                var choice = pairs.Select(p => (p.Pair, p.Neighbors,
                        At: p.Neighbors.Concat(p.Pair.Select(c => c.Id)).Select(id => free[id]).Append(inputs).Max()))
                    .Where(p => p.At != DateTime.MaxValue).OrderBy(p => p.At).FirstOrDefault();
                if (choice.Pair == null) { ready[stage.Output] = DateTime.MaxValue; continue; }
                var firstHours = Hours(stage.A);
                var secondHours = Hours(stage.B);
                if (firstHours > secondHours && secondHours > 0) (firstHours, secondHours) = (secondHours, firstHours);
                var finish = End(choice.At, firstHours);
                Reserve(stage.Output, choice.Pair, choice.Neighbors, choice.At, finish, choice.At, finish, End(choice.At, secondHours));
            }
        }
        return result;

        void Reserve(uint output, GardenCell[] pair, string[] neighbors, DateTime start, DateTime finish,
            DateTime neighborDeadline, DateTime producerFree, DateTime anchorFree) {
            ready[output] = finish;
            result.Add(new(output, pair, start, neighbors, neighborDeadline));
            free[pair[0].Id] = producerFree > finish ? producerFree : finish;
            free[pair[1].Id] = anchorFree > finish ? anchorFree : finish;
            foreach (var id in neighbors)
                if (free[id] < finish) free[id] = finish;
        }
    }

    public static bool Fits(IEnumerable<GardenReservation> reservations, GardenCell cell, double hours, DateTime now)
    {
        var deadline = reservations.Select(r => r.Pair.Contains(cell) ? r.PlantAt
            : r.Neighbors.Contains(cell.Id) ? r.NeighborDeadline : DateTime.MaxValue).DefaultIfEmpty(DateTime.MaxValue).Min();
        return deadline == DateTime.MaxValue || double.IsFinite(hours) && hours > 0
            && (deadline - now).TotalHours >= hours + 1;
    }
}
