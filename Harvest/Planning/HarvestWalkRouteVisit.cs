using System;
using System.Collections.Generic;
using System.Numerics;
using Harvest.Core;

namespace Harvest.Planning;

internal sealed partial class HarvestService
{
    private bool WalkingRouteInProgress(Visit work, GardenCell cell, out string phase)
    {
        phase = "개인주택 텔레포트";
        if (work.FailedRoutes.Contains(cell.Group)) throw new InvalidOperationException("이번 방문에서 해당 경로가 실패하여 재이동을 보류합니다.");
        var busy = Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        var arrivalKey = cell.Kind == "pot" ? $"pot:{cell.World}:{cell.Territory}:{cell.House}:{cell.Travel}" : cell.Group;
        if (work.HomeArrivals.Contains(arrivalKey) && !work.Travel
            && (cell.Kind == "pot" || work.WalkedRoutes.Contains(cell.Group)) && !AtCell(cell)) {
            work.HomeArrivals.Remove(arrivalKey); work.WalkedRoutes.Remove(cell.Group);
        }
        if (!work.HomeArrivals.Contains(arrivalKey)) {
            if (!work.Travel) {
                if (busy.InvokeFunc() || ExternalNav.TryGetActiveMover(out _)) return true;
                if (cell.Kind == "pot") {
                    // Enter_house=2: indoor crops must not depend on the user's default home-entry mode.
                    Svc.PluginInterface.GetIpcSubscriber<int, int?, object>("Lifestream.EnqueuePropertyShortcut").InvokeAction(1, 2);
                } else if (Profile.WalkRoutes.ContainsKey(cell.Group)) {
                    // PropertyType.Home=1, HouseEnterMode.None=0: the recorded outdoor path owns the walk after teleport.
                    Svc.PluginInterface.GetIpcSubscriber<int, int?, object>("Lifestream.EnqueuePropertyShortcut").InvokeAction(1, 0);
                } else Svc.PluginInterface.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction("home");
                work.Travel = true; work.TravelAt = DateTime.UtcNow;
                return true;
            }
            if (busy.InvokeFunc() || DateTime.UtcNow - work.TravelAt < TimeSpan.FromSeconds(1)) {
                work.ArrivalCheckAt = DateTime.MinValue;
                return true;
            }
            if (cell.Kind == "pot" && !AtCell(cell)) {
                phase = "실내 입장·주택 정보 확인";
                if (work.ArrivalCheckAt == DateTime.MinValue) work.ArrivalCheckAt = DateTime.UtcNow;
                if (DateTime.UtcNow - work.ArrivalCheckAt < TimeSpan.FromSeconds(10)) return true;
                Svc.Log.Information($"Garden indoor arrival mismatch: expected={cell.World}:{cell.Territory}:{cell.House}, actual={CurrentRouteLocation()}");
                throw new InvalidOperationException("실내 입장 후 등록한 주택 정보를 확인할 수 없습니다.");
            }
            if (Profile.WalkRoutes.TryGetValue(cell.Group, out var arrivalRoute)
                && (Svc.Objects.LocalPlayer is not { } arrivedPlayer || !arrivalRoute.CanJoin(CurrentRouteLocation(), arrivedPlayer.Position))) {
                phase = "주택 도착 정보 확인";
                if (work.ArrivalCheckAt == DateTime.MinValue) work.ArrivalCheckAt = DateTime.UtcNow;
                if (DateTime.UtcNow - work.ArrivalCheckAt < TimeSpan.FromSeconds(10)) return true;
                throw new InvalidOperationException("개인주택 도착 정보를 기다렸지만 저장한 도보 경로와 일치하지 않습니다.");
            }
            work.Travel = false; work.ArrivalCheckAt = DateTime.MinValue; work.HomeArrivals.Add(arrivalKey);
        }
        phase = "저장한 도보 경로 이동";
        if (!Profile.WalkRoutes.TryGetValue(cell.Group, out var route)) return false;
        if (!route.Matches(cell)) throw new InvalidOperationException("시설과 저장한 도보 경로의 주택·구역이 다릅니다.");
        var position = Svc.Objects.LocalPlayer?.Position ?? throw new InvalidOperationException("캐릭터 위치 확인 대기");
        if (work.WalkedRoutes.Contains(cell.Group) && AtCell(cell)) return false;
        if (work.RouteWalking) {
            if (busy.InvokeFunc()) return true;
            work.RouteWalking = false;
            if (Vector3.Distance(position, route.Points[^1].Position) > 1f) throw new InvalidOperationException("저장한 경로의 도착점에 도달하지 못했습니다.");
            if (!AtCell(cell)) throw new InvalidOperationException("도보 경로가 등록 주택에 도착하지 않았습니다.");
            work.WalkedRoutes.Add(cell.Group); work.StepAt = DateTime.UtcNow;
            return false;
        }
        var join = route.CanJoin(CurrentRouteLocation(), position);
        if (!join) {
            throw new InvalidOperationException("개인주택 텔레포트 도착지와 저장한 도보 경로가 다릅니다. 다른 위치에서 걸어서 연결하지 않습니다.");
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
