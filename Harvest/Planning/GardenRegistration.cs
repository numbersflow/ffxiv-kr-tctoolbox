using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Harvest.Planning;

public sealed record GardenPatch(ulong Object, string Kind, uint InstanceKey, uint SubId, Vector3 Origin, Vector3 Slot);

public static class GardenRegistration
{
    public static GardenPatch[] Nearest(IReadOnlyList<GardenPatch> patches, Vector3 position)
    {
        var nearest = patches.OrderBy(p => Vector3.DistanceSquared(p.Origin, position)).FirstOrDefault();
        if (nearest == null || Vector3.Distance(nearest.Origin, position) > 6)
            throw new InvalidOperationException("밭이나 화분 가까이에서 자동 등록을 누르세요.");
        if (nearest.InstanceKey == 0) throw new InvalidOperationException("재배 시설의 내부 식별자를 확인할 수 없습니다.");
        var group = patches.Where(p => p.Kind == nearest.Kind && p.InstanceKey == nearest.InstanceKey).ToArray();
        if (group.Any(p => !float.IsFinite(p.Slot.X) || !float.IsFinite(p.Slot.Y) || !float.IsFinite(p.Slot.Z)))
            throw new InvalidOperationException("재배 칸 좌표가 올바르지 않습니다.");
        if (group.Select(p => p.SubId).Distinct().Count() != group.Length
            || nearest.Kind == "plot" && group.Length is not (2 or 4 or 8)
            || nearest.Kind == "pot" && group.Length != 1)
            throw new InvalidOperationException("재배 시설의 전체 칸을 유일하게 식별할 수 없습니다. 시설 가까이에서 다시 확인하세요.");
        var center = group.Aggregate(Vector3.Zero, (sum, p) => sum + p.Slot) / group.Length;
        if (group.Length > 1 && group.Any(p => Vector3.DistanceSquared(p.Slot, center) < 0.01f))
            throw new InvalidOperationException("재배 칸의 배치를 확인할 수 없습니다.");
        var ordered = group.OrderBy(p => Math.Atan2(p.Slot.Z - center.Z, p.Slot.X - center.X)).ToArray();
        var first = Array.FindIndex(ordered, p => p.SubId == ordered.Min(p => p.SubId));
        return ordered.Skip(first).Concat(ordered.Take(first)).ToArray();
    }

    public static bool Matches(GardenCell cell, GardenPatch patch) => SameSlot(cell, patch.Kind, patch.Origin, patch.SubId);

    public static bool SameSlot(GardenCell cell, string kind, Vector3 origin, uint subId)
    {
        // Pots share sub-ID zero; only their saved furniture position survives zone re-entry.
        var tolerance = kind == "pot" ? 0.01f : 0.75f;
        return cell.Kind == kind && Vector3.DistanceSquared(cell.Position, origin) < tolerance * tolerance
            && (cell.LayoutInstanceKey == 0 || cell.LayoutSubId == subId);
    }
}
