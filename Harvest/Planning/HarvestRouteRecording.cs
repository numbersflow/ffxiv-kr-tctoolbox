using System;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Harvest.Core;

namespace Harvest.Planning;

internal sealed partial class HarvestService
{
    private readonly ICallGateProvider<string, string> routeRecordingGate;
    private ManualGardenRoute? routeRecording;
    private long nextRouteSample;
    private static GardenRecordingMotion RecordingMotion() =>
        (Svc.Condition[ConditionFlag.Mounted] ? GardenRecordingMotion.Mounted : 0)
        | (Svc.Condition[ConditionFlag.InFlight] ? GardenRecordingMotion.Flying : 0)
        | (Svc.Condition[ConditionFlag.Diving] ? GardenRecordingMotion.Diving : 0)
        | (Svc.Condition[ConditionFlag.Jumping] ? GardenRecordingMotion.Jumping : 0);

    private static string? RecordingBlocker()
    {
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer == null) return "캐릭터 위치 확인 대기";
        if (Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return "구역 이동 종료 대기";
        if (Svc.Condition[ConditionFlag.InCombat]) return "전투 종료 대기";
        if (Svc.Condition[ConditionFlag.Casting]) return "시전 종료 대기";
        if (ExternalNav.TryGetActiveMover(out var mover)) return "자동 이동 종료 대기 · " + mover;
        return GardenRecordingPolicy.Blocker(RecordingMotion());
    }

    private static unsafe GardenRouteLocation CurrentRouteLocation()
    {
        var player = Svc.Objects.LocalPlayer ?? throw new InvalidOperationException("캐릭터 위치 확인 대기");
        var housing = HousingManager.Instance();
        if (housing == null) return new(player.CurrentWorld.RowId, Svc.ClientState.TerritoryType, -1, 0);
        var house = housing->IsInside() ? housing->GetCurrentIndoorHouseId().Id : housing->GetCurrentHouseId().Id;
        return new(player.CurrentWorld.RowId, Svc.ClientState.TerritoryType, housing->GetCurrentWard(),
            house == ulong.MaxValue ? 0 : house, housing->GetCurrentDivision());
    }

    private string RouteRecordingRequest(string json)
    {
        try {
            if (disposed) throw new InvalidOperationException("플러그인 재로드 중");
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            var character = r.GetProperty("Character").GetUInt64();
            var token = r.GetProperty("Token").GetString() ?? "";
            var action = r.GetProperty("Action").GetString();
            if (character == 0 || character != owner || character != Svc.PlayerState.ContentId || !Svc.ClientState.IsLoggedIn)
                throw new InvalidOperationException("기록 캐릭터 확인 대기");
            if (action == "Probe") {
                var reason = RecordingBlocker();
                return JsonSerializer.Serialize(new { Accepted = true, Location = CurrentRouteLocation(),
                    Position = new { Svc.Objects.LocalPlayer!.Position.X, Svc.Objects.LocalPlayer.Position.Y, Svc.Objects.LocalPlayer.Position.Z },
                    Blocked = reason != null, Reason = reason, Motion = RecordingMotion().ToString(), Busy = garden.IsBusy || visit != null, Recording = routeRecording?.Active == true });
            }
            if (action == "Start") {
                if (routeRecording?.Active == true) {
                    if (routeRecording.Token != token || routeRecording.Owner != character) throw new InvalidOperationException("다른 경로를 기록 중입니다.");
                } else {
                    if (visit != null || garden.IsBusy) throw new InvalidOperationException("현재 재배 작업 종료 대기");
                    if (RecordingBlocker() is { } busy) throw new InvalidOperationException("기록 시작 대기 · " + busy);
                    var location = CurrentRouteLocation();
                    if (location.Ward < 0 && location.House == 0) throw new InvalidOperationException("Lifestream 도착 후 주거 구역에서 기록하세요.");
                    routeRecording = new(token, character, r.GetProperty("Group").GetString() ?? "",
                        r.GetProperty("Travel").GetString() ?? "", location, Svc.Objects.LocalPlayer!.Position, Environment.TickCount64);
                }
            } else {
                if (routeRecording == null || routeRecording.Token != token || routeRecording.Owner != character)
                    throw new InvalidOperationException("원래 경로 기록이 종료되었습니다.");
                if (action == "Heartbeat") routeRecording.Touch(token, character, Environment.TickCount64);
                else if (action == "Save") {
                    var saved = Profile.SaveWalkRoute(routeRecording, token, character, CurrentRouteLocation(), Svc.Objects.LocalPlayer!.Position, Environment.TickCount64, config.Save);
                    PublishGardenView();
                    Svc.Log.Information($"Garden walking route saved: group={saved.Group}, samples={routeRecording.SampleCount}, corners={saved.Points.Count}");
                } else if (action == "Cancel") routeRecording.Cancel("경로 기록 취소");
                else if (action != "Status") throw new ArgumentException("Unknown route recording action");
            }
            return JsonSerializer.Serialize(new { Accepted = true, Token = token, Character = character,
                Active = routeRecording!.Active, Samples = routeRecording.SampleCount, Error = routeRecording.Error,
                Group = routeRecording.Group,
                Corners = Profile.WalkRoutes.TryGetValue(routeRecording.Group, out var route) ? route.Points.Count : 0 });
        } catch (Exception ex) { return JsonSerializer.Serialize(new { Accepted = false, Error = ex.Message }); }
    }

    private void RecordRoutePosition(IFramework framework)
    {
        if (routeRecording?.Active != true || Environment.TickCount64 < nextRouteSample) return;
        nextRouteSample = Environment.TickCount64 + 100;
        try {
            if (RecordingBlocker() is { } reason) routeRecording.Cancel(reason);
            else routeRecording.Sample(Svc.PlayerState.ContentId, CurrentRouteLocation(), Svc.Objects.LocalPlayer!.Position, Environment.TickCount64);
            if (!routeRecording.Active) Svc.Log.Information("Garden recording stopped: " + routeRecording.Error);
        } catch (Exception ex) { routeRecording.Cancel(ex.Message); }
    }
}
