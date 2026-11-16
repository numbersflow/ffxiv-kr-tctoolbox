using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Harvest.Planning;

[Flags]
public enum GardenRecordingMotion { Ground = 0, Mounted = 1, Flying = 2, Diving = 4, Jumping = 8 }

public static class GardenRecordingPolicy
{
    public static string? Blocker(GardenRecordingMotion motion) =>
        (motion & GardenRecordingMotion.Flying) != 0 ? "비행 중에는 지상 도보 경로를 기록할 수 없습니다."
        : (motion & GardenRecordingMotion.Diving) != 0 ? "잠수 중에는 지상 도보 경로를 기록할 수 없습니다."
        : (motion & GardenRecordingMotion.Jumping) != 0 ? "점프 중에는 지상 이동 경로를 기록할 수 없습니다."
        : null;
}

public sealed record GardenRoutePoint(float X, float Y, float Z)
{
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
    public static GardenRoutePoint From(Vector3 point) => new(point.X, point.Y, point.Z);
}

public readonly record struct GardenRouteLocation(uint World, uint Territory, int Ward, ulong House, int Division = 0);

public sealed class GardenWalkRoute
{
    public string Group { get; set; } = "";
    public string Travel { get; set; } = "home";
    public GardenRouteLocation Start { get; set; }
    public ulong DestinationHouse { get; set; }
    public List<GardenRoutePoint> Points { get; set; } = [];
    public DateTime Saved { get; set; }
    public bool Matches(GardenCell cell) => Points.Count >= 2 && Group == cell.Group
        && Start.World == cell.World && Start.Territory == cell.Territory && DestinationHouse == cell.House;
    public bool CanJoin(GardenRouteLocation location, Vector3 position) => Start.World == location.World
        && Start.Territory == location.Territory && Start.Ward == location.Ward && Start.Division == location.Division
        && (Start.House == 0 || Start.House == location.House || DestinationHouse == location.House)
        && CornerRoute.Remaining(Points, position) != null;
}

public static class CornerRoute
{
    private static Vector3 Project(Vector3 point, Vector3 start, Vector3 end)
    {
        var delta = end - start;
        return delta.LengthSquared() < 0.000001f ? start : start + delta * Math.Clamp(Vector3.Dot(point - start, delta) / delta.LengthSquared(), 0, 1);
    }

    public static List<GardenRoutePoint> Simplify(IReadOnlyList<Vector3> samples, float tolerance = 0.15f)
    {
        if (samples.Count < 2 || samples.Count > 10000 || !float.IsFinite(tolerance) || tolerance <= 0
            || samples.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)))
            throw new ArgumentException("유효한 도보 경로가 필요합니다.");
        var keep = new bool[samples.Count]; keep[0] = keep[^1] = true;
        var pending = new Stack<(int Start, int End)>(); pending.Push((0, samples.Count - 1));
        while (pending.TryPop(out var segment)) {
            var farthest = -1; var distance = tolerance * tolerance;
            for (var i = segment.Start + 1; i < segment.End; i++) {
                var squared = Vector3.DistanceSquared(samples[i], Project(samples[i], samples[segment.Start], samples[segment.End]));
                if (squared > distance) { farthest = i; distance = squared; }
            }
            if (farthest < 0) continue;
            keep[farthest] = true; pending.Push((segment.Start, farthest)); pending.Push((farthest, segment.End));
        }
        return samples.Where((_, i) => keep[i]).Select(GardenRoutePoint.From).ToList();
    }

    // Rejoin the recorded segment after a pause; never cut straight to the final corner.
    public static List<Vector3>? Remaining(IReadOnlyList<GardenRoutePoint> path, Vector3 position, float joinDistance = 1.5f)
    {
        if (path.Count < 2 || path.Count > 10000 || !float.IsFinite(joinDistance) || joinDistance <= 0
            || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)
            || path.Any(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))) return null;
        if (Vector3.Distance(position, path[^1].Position) <= 0.5f) return [];
        var best = joinDistance * joinDistance; var next = -1; var projection = Vector3.Zero;
        for (var i = 1; i < path.Count; i++) {
            var p = Project(position, path[i - 1].Position, path[i].Position);
            var distance = Vector3.DistanceSquared(position, p);
            if (distance > best) continue;
            best = distance; next = i; projection = p;
        }
        return next < 0 ? null : new[] { projection }.Concat(path.Skip(next).Select(p => p.Position)).ToList();
    }
}

public sealed class ManualGardenRoute
{
    public string Token { get; }
    public ulong Owner { get; }
    public string Group { get; }
    public string Travel { get; }
    public GardenRouteLocation Start { get; }
    public bool Active { get; private set; } = true;
    public string Error { get; private set; } = "";
    public int SampleCount => samples.Count;
    private readonly List<Vector3> samples = [];
    private long heartbeat;

    public ManualGardenRoute(string token, ulong owner, string group, string travel, GardenRouteLocation start, Vector3 position, long now)
    {
        if (string.IsNullOrWhiteSpace(token) || owner == 0 || string.IsNullOrWhiteSpace(group) || group.Length > 80
            || string.IsNullOrWhiteSpace(travel) || travel.Length > 256) throw new ArgumentException("시설 이름과 Lifestream 경로가 필요합니다.");
        Token = token; Owner = owner; Group = group; Travel = travel; Start = start; heartbeat = now;
        Sample(owner, start, position, now, true);
        if (!Active) throw new ArgumentException(Error);
    }
    public bool Touch(string token, ulong owner, long now)
    {
        if (!Active || token != Token || owner != Owner || now - heartbeat > 6000) return false;
        heartbeat = now; return true;
    }
    public void Cancel(string reason) { Active = false; Error = reason; }
    public bool Sample(ulong owner, GardenRouteLocation location, Vector3 position, long now, bool final = false)
    {
        if (!Active) return false;
        if (owner != Owner || location.World != Start.World || location.Territory != Start.Territory || location.Ward != Start.Ward || location.Division != Start.Division)
            Cancel("캐릭터·구역 변경으로 경로 기록 취소");
        else if (now - heartbeat > 6000) Cancel("워크플로우 연결 종료로 경로 기록 취소");
        else if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) Cancel("위치 확인 실패로 기록 취소");
        else if (samples.Count >= 10000 || samples.Count > 0 && Vector3.Distance(position, samples[^1]) > 20)
            Cancel("순간이동 또는 경로 기록 한도 초과로 취소");
        if (!Active) return false;
        if (samples.Count == 0 || Vector3.Distance(position, samples[^1]) >= (final ? 0.001f : 0.15f)) samples.Add(position);
        return true;
    }
    public GardenWalkRoute Finish(string token, ulong owner, GardenRouteLocation location, Vector3 position, long now)
    {
        if (token != Token || owner != Owner) throw new InvalidOperationException("다른 기록의 저장 요청");
        if (!Sample(owner, location, position, now, true)) throw new InvalidOperationException(Error);
        if (location.House is 0 or ulong.MaxValue) throw new InvalidOperationException("밭이 있는 주택 부지 안에서 저장하세요.");
        if (samples.Count < 2 || samples.Zip(samples.Skip(1), Vector3.Distance).Sum() < 0.25f)
            throw new InvalidOperationException("조금 이동한 뒤 저장하세요.");
        var points = CornerRoute.Simplify(samples);
        Active = false;
        return new() { Group = Group, Travel = Travel, Start = Start, DestinationHouse = location.House, Points = points, Saved = DateTime.UtcNow };
    }
}
