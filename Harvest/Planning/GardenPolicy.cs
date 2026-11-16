using System;
using System.Collections.Generic;
using System.Linq;

namespace Harvest.Planning;

public static class GardenPolicy
{
    // The second cell stays planted, including when both parents use the same seed.
    public static bool IsAnchor(GardenBatch batch, GardenCell cell) =>
        batch.Cross && !batch.Clearing && batch.Cells.Count > 1 && batch.Cells[1] == cell.Id;

    public static int RequiredSlots(IEnumerable<(uint Item, int Count, int Stack, int Room)> outputs) =>
        outputs.GroupBy(o => o.Item).Sum(g => {
            var first = g.First();
            if (first.Stack <= 0 || first.Room < 0 || g.Any(o => o.Count < 0)) throw new ArgumentException("Invalid harvest capacity");
            return (int)Math.Ceiling(Math.Max(0, g.Sum(o => (long)o.Count) - first.Room) / (double)first.Stack);
        });
}
