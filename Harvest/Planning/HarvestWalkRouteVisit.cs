using System;
using System.Collections.Generic;
using System.Numerics;
using Harvest.Core;

namespace Harvest.Planning;

internal sealed partial class HarvestService
{
    private bool WalkingRouteInProgress(Visit work, GardenCell cell, out string phase)
    {
        phase = "저장한 도보 경로 이동";
        if (!Profile.WalkRoutes.TryGetValue(cell.Group, out var route)) return false;
        if (!route.Matches(cell)) throw new InvalidOperationException("시설과 저장한 도보 경로의 주택·구역이 다릅니다.");
        var position = Svc.Objects.LocalPlayer?.Position ?? throw new InvalidOperationException("캐릭터 위치 확인 대기");
        if (work.WalkedRoutes.Contains(cell.Group) && AtCell(cell)) return false;
        var busy = Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        if (work.RouteWalking) {
            if (busy.InvokeFunc()) return true;
            work.RouteWalking = false;
            if (Vector3.Distance(position, route.Points[^1].Position) > 1f) throw new InvalidOperationException("저장한 경로의 도착점에 도달하지 못했습니다.");
            if (!AtCell(cell)) throw new InvalidOperationException("도보 경로가 등록 주택에 도착하지 않았습니다.");
            work.WalkedRoutes.Add(cell.Group); work.StepAt = DateTime.UtcNow;
            return false;
        }
        var join = route.CanJoin(CurrentRouteLocation(), position);
        if (work.Travel) {
            phase = "Lifestream 이동 후 도보 경로 연결";
            if (busy.InvokeFunc()) return true;
            if (!join) {
                if (DateTime.UtcNow - work.TravelAt < TimeSpan.FromSeconds(10)) return true;
                throw new InvalidOperationException("Lifestream 도착지와 저장한 도보 경로가 다릅니다. 도착 지점부터 다시 기록하세요.");
            }
            work.Travel = false;
        }
        if (!join) {
            phase = "도보 경로 출발점으로 이동";
            if (ExternalNav.TryGetActiveMover(out _)) return true;
            work.Travel = true; work.TravelAt = DateTime.UtcNow;
            Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(route.Travel);
            return true;
        }
        var points = CornerRoute.Remaining(route.Points, position)!;
        if (points.Count == 0) {
            if (!AtCell(cell)) throw new InvalidOperationException("경로 도착점의 주택 식별이 다릅니다.");
            work.WalkedRoutes.Add(cell.Group); return false;
        }
        if (ExternalNav.TryGetActiveMover(out _)) return true;
        work.RouteWalking = true;
        Svc.PluginInterface.GetIpcSubscriber<List<Vector3>, bool?, float?, float?, object>("Lifestream.MoveEx")
            .InvokeAction(points, true, 0.3f, 0.15f);
        return true;
    }
}
