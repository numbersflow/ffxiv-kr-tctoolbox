using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Harvest.Core;
using Harvest.Modules;
using LocalAutomation;

namespace Harvest.Planning;

internal sealed partial class HarvestService
{
    private sealed class Visit
    {
        public ulong Owner;
        public GardenCell? Cell;
        public ulong Object;
        public bool Cancel, Paused, Move, Travel, Scanning, Verifying, CareOnly;
        public bool RouteWalking;
        public HashSet<string> WalkedRoutes = [];
        public HashSet<string> HomeArrivals = [];
        public int ReservedSlots = 8;
        public DateTime Started = DateTime.UtcNow, StepAt = DateTime.UtcNow, ScanAt, TravelAt;
        public DateTime PauseAt;
        public string Phase = "준비";
        public HashSet<string> Done = [];
        public int Completed, Failed, RequiredSpace;
    }

    private AutomationWork CreateWork(string kind, JsonElement arguments)
    {
        if (kind != "Gardening") throw new ArgumentException("Unknown Harvest work");
        var work = new Visit { Owner = owner,
            CareOnly = arguments.TryGetProperty("CareOnly", out var care) && care.GetBoolean(),
            ReservedSlots = arguments.TryGetProperty("ReservedFreeSlots", out var slots) ? Math.Clamp(slots.GetInt32(), 0, 139) : 8 };
        return new(() => {
            if (work.Owner == 0 || work.Owner != Svc.PlayerState.ContentId || routeRecording?.Active == true || Profile.Batch == null || !HasDueVisit()) return false;
            config.GardensWork.AutoLoopEnabled = false;
            garden.Enable();
            if (!garden.IsEnabled) return false;
            visit = work;
            return true;
        }, () => { work.Cancel = true; StopOwnedTravel(work); }, () => Observe(work), () => true,
            () => { work.Cancel = true; StopOwnedTravel(work); garden.Disable(); visit = null; config.Save(); },
            () => { work.Cancel = true; garden.Disable(); visit = null; config.Save(); },
            (paused, _) => {
                if (paused && !work.Paused) {
                    work.PauseAt = DateTime.UtcNow;
                    var travelPause = Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsWorkflowPaused");
                    if (travelPause.HasFunction && travelPause.InvokeFunc()) {
                        if (work.Move) { ExternalNav.TryStopMovement(); work.Move = false; }
                    } else StopOwnedTravel(work);
                }
                if (!paused && work.Paused) work.StepAt += DateTime.UtcNow - work.PauseAt;
                work.Paused = paused;
            },
            () => work.Paused ? garden.IsBusy ? "Pausing" : "Paused" : "Continuing");
    }

    private AutomationObservation Observe(Visit work)
    {
        if (visit != work) return new(false, work.Completed, work.Completed, "Gardening", "Cancelled");
        if (Svc.PlayerState.ContentId != work.Owner) return new(true, work.Completed, 1, "Gardening:Owner", "", "원래 캐릭터 확인 대기");
        try {
            if (garden.IsBusy) return Busy(work, "현재 칸 조작 마무리");
            if (work.Cancel) return End(work, "Cancelled");
            if (work.Paused) return Busy(work, "일시정지");
            if (work.Cell == null) {
                var batch = Profile.Batch;
                work.Cell = Profile.Cells.Where(c => c.Enabled && batch?.Cells.Contains(c.Id) == true && !work.Done.Contains(c.Id) && Due(c))
                    .Where(c => GardenPolicy.CanVisit(c, stock, CurrentSoil))
                    .OrderBy(c => c.Owned || c.Pending.Length > 0 ? 0 : 1).FirstOrDefault();
                if (work.Cell == null) return End(work, work.Failed == 0 ? "Succeeded" : "Partial");
                work.StepAt = DateTime.UtcNow; work.Scanning = false; work.Verifying = false;
            }
            var cell = work.Cell;
            if (DateTime.UtcNow - work.StepAt > TimeSpan.FromMinutes(3)) throw new InvalidOperationException("시설 접근·조작 시간 초과");
            if (WalkingRouteInProgress(work, cell, out var routePhase)) return Busy(work, routePhase);
            if (!AtCell(cell)) {
                throw new InvalidOperationException("개인주택 텔레포트 결과가 등록한 재배 시설의 위치와 다릅니다.");
            }
            work.Travel = false;
            if (work.Object == 0) {
                var patches = NearbyPatches();
                var candidates = patches.Where(p => GardenRegistration.Matches(cell, p)).ToArray();
                if (candidates.Length != 1 || candidates[0].InstanceKey != cell.LayoutInstanceKey)
                    Svc.Log.Information($"Garden patch identity: expected={cell.LayoutInstanceKey}:{cell.LayoutSubId} at {cell.Position}, matches={candidates.Length}, nearby={string.Join(';', patches.Select(p => $"{p.InstanceKey}:{p.SubId}@{p.Origin}"))}");
                if (candidates.Length != 1) throw new InvalidOperationException("등록 시설의 칸을 유일하게 식별할 수 없습니다.");
                work.Object = candidates[0].Object;
            }
            if (garden.GetPatchDistance(work.Object) > AutoGardensWork.InteractRange) {
                if (work.Move) {
                    if (ExternalNav.IsVnavmeshPathRunning() || ExternalNav.IsVnavmeshPathfindInProgress()) return Busy(work, "시설 접근");
                    throw new InvalidOperationException("시설 접근이 상호작용 범위에 도달하지 못했습니다.");
                }
                if (ExternalNav.TryGetActiveMover(out _)) return Busy(work, "외부 이동 종료 대기");
                if (!ExternalNav.TryMoveTo(cell.Approach, false, out var started, "Harvest") || !started)
                    throw new InvalidOperationException("시설 접근 경로를 시작할 수 없습니다.");
                work.Move = true; return Busy(work, "시설 접근 시작");
            }
            if (work.Move) { ExternalNav.TryStopMovement(); work.Move = false; }
            if (!work.Scanning) {
                work.ScanAt = DateTime.UtcNow;
                Queue(work, AutoGardensWork.GardenAction.Scan);
                work.Scanning = true;
                return Busy(work, "작물 상태 확인");
            }
            var observation = garden.ReadObservation(work.Object);
            if (observation.At < work.ScanAt || observation.State == PatchState.Unknown)
                throw new InvalidOperationException("새 작물 상태를 확인할 수 없습니다.");
            cell.Crop = observation.Crop; cell.Observed = observation.At;
            cell.State = observation.State switch { PatchState.Empty => "empty", PatchState.Ripe => "mature", PatchState.Withered => "withered", _ => "growing" };
            if (cell.Pending.Length > 0 && work.Verifying) {
                Reconcile(cell, observation.State);
                work.Completed++; FinishCell(work); return Busy(work, "처리 결과 저장");
            }
            if (cell.Pending.Length > 0) {
                Reconcile(cell, observation.State);
                FinishCell(work); return Busy(work, "이전 처리 결과 복구");
            }
            var action = Choose(cell, observation.State);
            if (work.CareOnly && action != AutoGardensWork.GardenAction.Tend) action = null;
            if (action == null) { FinishCell(work); return Busy(work, "다음 칸 확인"); }
            var consumed = action == AutoGardensWork.GardenAction.Plant ? cell.Seed
                : action == AutoGardensWork.GardenAction.Fertilize ? Colors(cell).ElementAt(cell.ColorStep) : 0;
            cell.Before = consumed == 0 ? 0 : Bag(consumed);
            if (consumed > 0 && cell.Before == 0) { FinishCell(work); return Busy(work, "재료 준비 대기"); }
            if (action == AutoGardensWork.GardenAction.Plant && Bag(CurrentSoil) == 0) { FinishCell(work); return Busy(work, "흙 준비 대기"); }
            if (action == AutoGardensWork.GardenAction.Harvest && FreeSlots() < HarvestSlots(cell) + work.ReservedSlots) {
                work.RequiredSpace = HarvestSlots(cell) + work.ReservedSlots;
                throw new InvalidOperationException("가방 여유 부족 · 작물과 교배 씨앗 수용 공간 필요");
            }
            cell.Pending = action.ToString()!;
            config.Save();
            Queue(work, action.Value, consumed);
            work.Scanning = false; work.Verifying = true;
            return Busy(work, action.ToString()!);
        } catch (Exception ex) {
            error = ex.Message;
            StopOwnedTravel(work, resetRoutes: false);
            work.Failed++;
            if (work.Cell is { } cell) { cell.Error = ex.Message; cell.RetryAt = DateTime.UtcNow.AddMinutes(1); }
            FinishCell(work); config.Save();
            return Busy(work, "해당 칸 보류 · " + ex.Message);
        }
    }

    private AutoGardensWork.GardenAction? Choose(GardenCell cell, PatchState state)
    {
        var batch = Profile.Batch!;
        if (state == PatchState.Empty) {
            cell.Owned = false;
            if (batch.Clearing) return null;
            return AutoGardensWork.GardenAction.Plant;
        }
        if (!cell.Owned) return null;
        if (state == PatchState.Withered) { cell.Error = "작물이 말랐습니다. 자동 폐기하지 않습니다."; return null; }
        if (state == PatchState.Ripe) {
            if (GardenPolicy.IsAnchor(batch, cell)) return null;
            if (batch.Cross && !batch.Clearing && !Profile.Cells.Any(c => batch.Cells.Contains(c.Id) && c.Id != cell.Id && c.Owned && c.State != "empty")) return null;
            return AutoGardensWork.GardenAction.Harvest;
        }
        if (state == PatchState.NeedsCare || cell.Tended.AddHours(12) <= DateTime.UtcNow) return AutoGardensWork.GardenAction.Tend;
        if (cell.ColorStep < Colors(cell).Length && cell.ColorFed.AddHours(1) <= DateTime.UtcNow) return AutoGardensWork.GardenAction.Fertilize;
        return null;
    }

    private uint[] Colors(GardenCell cell) => Profile.Batch is { Cross: false } batch && catalog!.Crops.TryGetValue(batch.Output, out var crop) ? crop.Colors : [];
    private void Reconcile(GardenCell cell, PatchState state)
    {
        var action = cell.Pending;
        if (action == "Plant") {
            if (state == PatchState.Empty) { cell.Pending = ""; throw new InvalidOperationException("심기가 확인되지 않았습니다."); }
            if (!catalog!.SeedCrops.TryGetValue(cell.Seed, out var expected) || cell.Crop != expected)
                throw new InvalidOperationException("심기 결과의 작물이 계획과 다릅니다.");
            cell.Owned = true; cell.Planted = cell.Tended = DateTime.UtcNow; cell.ColorStep = 0;
            _ = Bag(CurrentSoil);
        } else if (action == "Harvest") {
            if (state != PatchState.Empty) throw new InvalidOperationException("수확 결과 미확정 · 재전송하지 않습니다.");
            cell.Owned = false; cell.Planted = DateTime.MinValue; cell.ColorStep = 0;
        } else if (action == "Fertilize") {
            if (cell.ColorStep >= Colors(cell).Length || Bag(Colors(cell)[cell.ColorStep]) != cell.Before - 1)
                throw new InvalidOperationException("색상 재료 사용 결과 미확정 · 재전송하지 않습니다.");
            cell.ColorStep++; cell.ColorFed = DateTime.UtcNow;
        } else if (action == "Tend") {
            if (state != PatchState.Growing && state != PatchState.Ripe) throw new InvalidOperationException("관리 결과 미확정");
            cell.Tended = DateTime.UtcNow;
        }
        cell.Pending = ""; cell.Error = ""; config.Save();
    }
    private void Queue(Visit work, AutoGardensWork.GardenAction action, uint item = 0)
    {
        var message = garden.EnqueueSingle(action, work.Object, item, work.Cell!.Seed, CurrentSoil);
        if (message.Length > 0) throw new InvalidOperationException(message);
    }
    private int Bag(uint item)
    {
        using var doc = JsonDocument.Parse(Svc.PluginInterface.GetIpcSubscriber<string, string>("GatherBuddyReborn.GetHarvestStock")
            .InvokeFunc(JsonSerializer.Serialize(new[] { item })));
        var r = doc.RootElement;
        if (!r.GetProperty("Available").GetBoolean() || r.GetProperty("Character").GetUInt64() != owner) throw new InvalidOperationException("재고 재확인 대기");
        var count = r.GetProperty("Rows")[0].GetProperty("Inventory").GetInt32();
        if (stock.TryGetValue(item, out var previous)) stock[item] = previous with { Inventory = count };
        return count;
    }
    private static unsafe int FreeSlots()
    {
        var manager = InventoryManager.Instance();
        if (manager == null) return 0;
        var free = 0;
        foreach (var type in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 }) {
            var bag = manager->GetInventoryContainer(type);
            if (bag == null || !bag->IsLoaded || bag->Items == null) return 0;
            for (var i = 0; i < bag->Size; i++) if (bag->GetInventorySlot(i)->ItemId == 0) free++;
        }
        return free;
    }
    private int HarvestSlots(GardenCell cell)
    {
        // Gardening sheets do not expose yield. Reserve one full stack per possible output.
        var crop = catalog!.SeedCrops.GetValueOrDefault(cell.Seed);
        var seeds = Profile.Batch is { Cross: true } batch
            ? catalog.Crosses.Where(c => c.A == batch.SeedA && c.B == batch.SeedB || c.B == batch.SeedA && c.A == batch.SeedB)
                .Select(c => c.Output).Distinct().ToArray() : [];
        return (crop == 0 ? 1 : OutputSlots(crop)) + (seeds.Length == 0 ? 0 : seeds.Max(OutputSlots));
    }
    private int OutputSlots(uint item)
    {
        var row = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRow(item);
        var stack = checked((int)row.StackSize);
        return GardenPolicy.RequiredSlots(new[] { (item, stack, stack, 0) });
    }
    private static unsafe bool AtCell(GardenCell cell)
    {
        var housing = HousingManager.Instance();
        return housing != null && Svc.ClientState.TerritoryType == cell.Territory && Svc.Objects.LocalPlayer?.CurrentWorld.RowId == cell.World
            && (housing->GetCurrentHouseId().Id == cell.House || housing->GetCurrentIndoorHouseId().Id == cell.House);
    }
    private void StopOwnedTravel(Visit work, bool resetRoutes = true)
    {
        if (resetRoutes) { work.HomeArrivals.Clear(); work.WalkedRoutes.Clear(); }
        if (work.Move) { ExternalNav.TryStopMovement(); work.Move = false; }
        if (work.Travel || work.RouteWalking) {
            var gate = Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
            if (gate.HasAction) gate.InvokeAction();
            work.Travel = work.RouteWalking = false;
        }
    }
    private void FinishCell(Visit work)
    {
        if (work.Cell != null) work.Done.Add(work.Cell.Id);
        work.Cell = null; work.Object = 0; work.Scanning = false; work.Verifying = false;
    }
    private AutomationObservation Busy(Visit work, string phase)
    {
        work.Phase = phase;
        return new(true, work.Completed, Profile.Batch?.Cells.Count ?? 1, "Gardening:" + phase, "", phase, work.Completed);
    }
    private AutomationObservation End(Visit work, string result)
    {
        visit = null; nextRefresh = DateTime.MinValue; config.Save();
        return new(false, work.Completed, Profile.Batch?.Cells.Count ?? 1,
            work.RequiredSpace > 0 ? "Gardening:BagSpace/" + work.RequiredSpace : "Gardening", result, work.Phase, work.Completed);
    }
}
