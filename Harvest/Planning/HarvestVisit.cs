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
        public bool RouteWalking, CommandConfirmed;
        public HashSet<string> WalkedRoutes = [];
        public HashSet<string> HomeArrivals = [];
        public HashSet<string> FailedRoutes = [];
        public int ReservedSlots = 8;
        public DateTime Started = DateTime.UtcNow, StepAt = DateTime.UtcNow, ScanAt, TravelAt, ArrivalCheckAt;
        public DateTime PauseAt;
        public string Phase = "준비";
        public HashSet<string> Done = [], Acted = [], Tended = [];
        public HashSet<GardenBatch> Plans = [];
        public int Verified, Plants, Tends, Harvests, Feeds;
        public int Completed, Failed, RequiredSpace;
    }

    private AutomationWork CreateWork(string kind, JsonElement arguments)
    {
        if (kind != "Gardening") throw new ArgumentException("Unknown Harvest work");
        var work = new Visit { Owner = owner,
            CareOnly = arguments.TryGetProperty("CareOnly", out var care) && care.GetBoolean(),
            ReservedSlots = arguments.TryGetProperty("ReservedFreeSlots", out var slots) ? Math.Clamp(slots.GetInt32(), 0, 139) : 8 };
        return new(() => {
            if (work.Owner == 0 || work.Owner != Svc.PlayerState.ContentId || routeRecording?.Active == true || !Profile.ActiveBatches.Any() || !HasDueVisit()
                || !Profile.Cells.Any(c => c.Enabled && Profile.BatchFor(c) != null && Due(c) && ReadyForVisit(c))) return false;
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
                var waiting = Profile.Cells.Where(c => c.Enabled && Profile.BatchFor(c) != null
                    && !work.Done.Contains(c.Id) && !c.Owned && c.Pending.Length == 0 && Due(c)).ToArray();
                if (waiting.Length > 0) {
                    try { ReadVisitInventory(waiting.SelectMany(c => new[] { c.Seed, SoilFor(c) }).Where(i => i > 0)); }
                    catch (Exception ex) { work.Phase = "재고 재확인 대기 · " + ex.Message; return End(work, "Partial"); }
                }
                work.Cell = Profile.Cells.Where(c => c.Enabled && Profile.BatchFor(c) != null && !work.Done.Contains(c.Id) && c.RetryAt <= DateTime.UtcNow
                    && (work.Plans.Contains(Profile.BatchFor(c)!) || Due(c) && ReadyForVisit(c)))
                    .Where(c => CanVisit(c))
                    .OrderBy(c => c.Owned || c.Pending.Length > 0 ? 0 : 1).FirstOrDefault();
                if (work.Cell == null) return End(work, work.Failed == 0 ? "Succeeded" : "Partial");
                work.Plans.Add(Profile.BatchFor(work.Cell)!);
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
                if (work.Verifying) work.CommandConfirmed = garden.DoneCount > 0 && garden.SkippedCount == 0;
                work.ScanAt = DateTime.UtcNow;
                Queue(work, AutoGardensWork.GardenAction.Scan);
                work.Scanning = true;
                return Busy(work, "작물 상태 확인");
            }
            var observation = garden.ReadObservation(work.Object);
            if (observation.At < work.ScanAt || observation.State == PatchState.Unknown)
                throw new InvalidOperationException("새 작물 상태를 확인할 수 없습니다.");
            cell.Crop = observation.Crop; cell.Observed = observation.At; cell.Error = "";
            cell.State = observation.State switch { PatchState.Empty => "empty", PatchState.Ripe => "mature", PatchState.Withered => "withered", _ => "growing" };
            if (cell.Pending == "Tend" && !work.Verifying) { cell.Pending = ""; config.Save(); }
            if (cell.Pending.Length == 0 && cell.Owned && observation.State == PatchState.Empty) {
                cell.Owned = false; cell.Planted = DateTime.MinValue; cell.ColorStep = 0; config.Save();
                FinishCell(work);
                if (!work.CareOnly) {
                    try { ReplanAfterHarvest(work); }
                    catch (Exception ex) { work.Phase = "수동 수확·변경 재고 확인 대기 · " + ex.Message; return End(work, "Partial"); }
                }
                return Busy(work, "수동 수확·변경 반영 · 후속 작업 계속");
            }
            if (cell.Pending.Length > 0) {
                var completedAction = cell.Pending;
                if (completedAction == "Tend" && !work.CommandConfirmed) throw new InvalidOperationException("관리 실행 완료 확인 대기 · 성공 시각을 갱신하지 않습니다.");
                Reconcile(cell, observation.State);
                work.Verified++;
                if (work.Acted.Add(cell.Id)) work.Completed++;
                if (completedAction is "Plant" or "Tend") work.Tended.Add(cell.Id);
                if (completedAction == "Plant") work.Plants++;
                else if (completedAction == "Tend") work.Tends++;
                else if (completedAction == "Harvest") work.Harvests++;
                else if (completedAction == "Fertilize") work.Feeds++;
                work.Verifying = false;
                if (completedAction == "Harvest") {
                    FinishCell(work);
                    if (!work.CareOnly) {
                        try {
                            ReplanAfterHarvest(work);
                        } catch (Exception ex) { work.Phase = "수확 재고·다음 계획 확인 대기 · " + ex.Message; return End(work, "Partial"); }
                    }
                } else if (!work.CareOnly && completedAction is "Plant" or "Fertilize") {
                    try { ReadVisitInventory(new[] { cell.Seed, SoilFor(cell) }.Concat(Colors(cell))); Replan(); }
                    catch (Exception ex) { work.Phase = "심기·색상 재고 반영 대기 · " + ex.Message; return End(work, "Partial"); }
                }
                return Busy(work, "처리 결과 확인 · 후속 작업 계속");
            }
            var action = Choose(cell, observation.State);
            if (action == null && cell.Owned && observation.State is PatchState.Growing or PatchState.Ripe
                && !work.Tended.Contains(cell.Id)) action = AutoGardensWork.GardenAction.Tend;
            if (work.CareOnly && action != AutoGardensWork.GardenAction.Tend) action = null;
            if (action == null) { FinishCell(work); return Busy(work, "다음 칸 확인"); }
            var consumed = action == AutoGardensWork.GardenAction.Plant ? cell.Seed
                : action == AutoGardensWork.GardenAction.Fertilize ? Colors(cell).ElementAt(cell.ColorStep) : 0;
            cell.Before = consumed == 0 ? 0 : Bag(consumed);
            if (consumed > 0 && cell.Before == 0) { FinishCell(work); return Busy(work, "재료 준비 대기"); }
            if (action == AutoGardensWork.GardenAction.Plant && Bag(SoilFor(cell)) == 0) { FinishCell(work); return Busy(work, "흙 준비 대기"); }
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
            if (work.Cell != null && (work.Travel || work.RouteWalking)) work.FailedRoutes.Add(work.Cell.Group);
            StopOwnedTravel(work, resetRoutes: false);
            work.Failed++;
            if (work.Cell is { } cell) { cell.Error = ex.Message; cell.RetryAt = DateTime.UtcNow.AddMinutes(1); }
            FinishCell(work); config.Save();
            return Busy(work, "해당 칸 보류 · " + ex.Message);
        }
    }

    private void ReplanAfterHarvest(Visit work)
    {
        ReadVisitInventory(catalog!.Crops.Keys.Concat(catalog.SeedCrops.Keys).Concat(new[] { Profile.Soil, 16026u, 14011u, 14012u, 14013u }));
        var previous = Profile.ActiveBatches.ToArray();
        Replan();
        work.Done.RemoveWhere(id => Profile.Cells.Any(c => c.Id == id && Profile.BatchFor(c) is { } remaining
            && (!previous.Contains(remaining) || !c.Owned || c.State == "mature" && remaining.Clearing)));
    }

    private AutoGardensWork.GardenAction? Choose(GardenCell cell, PatchState state)
    {
        var batch = Profile.BatchFor(cell) ?? throw new InvalidOperationException("Missing cell plan");
        if (state == PatchState.Empty) {
            cell.Owned = false;
            if (batch.Clearing || !CanPlant(cell)) return null;
            return AutoGardensWork.GardenAction.Plant;
        }
        if (!cell.Owned) { cell.Error = "다른 작물이 있는 칸은 자동 수확·폐기하지 않습니다."; return null; }
        if (state == PatchState.Withered) { cell.Error = "작물이 말랐습니다. 자동 폐기하지 않습니다."; return null; }
        if (state == PatchState.Ripe) {
            if (!CanHarvest(cell)) return cell.Tended.AddHours(12) <= DateTime.UtcNow ? AutoGardensWork.GardenAction.Tend : null;
            return AutoGardensWork.GardenAction.Harvest;
        }
        if (state == PatchState.NeedsCare || cell.Tended.AddHours(12) <= DateTime.UtcNow) return AutoGardensWork.GardenAction.Tend;
        if (cell.ColorStep < Colors(cell).Length && cell.ColorFed.AddHours(1) <= DateTime.UtcNow) return AutoGardensWork.GardenAction.Fertilize;
        return null;
    }

    private uint[] Colors(GardenCell cell) => catalog != null && Profile.BatchFor(cell) is { Cross: false } batch && catalog!.Crops.TryGetValue(batch.Output, out var crop) ? crop.Colors : [];
    private void Reconcile(GardenCell cell, PatchState state)
    {
        var action = cell.Pending;
        if (action == "Plant") {
            if (state == PatchState.Empty) { cell.Pending = ""; throw new InvalidOperationException("심기가 확인되지 않았습니다."); }
            if (!catalog!.SeedCrops.TryGetValue(cell.Seed, out var expected) || cell.Crop != expected)
                throw new InvalidOperationException("심기 결과의 작물이 계획과 다릅니다.");
            cell.Owned = true; cell.Planted = cell.Tended = DateTime.UtcNow; cell.ColorStep = 0;
            _ = Bag(SoilFor(cell));
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
        var message = garden.EnqueueSingle(action, work.Object, item, work.Cell!.Seed, SoilFor(work.Cell));
        if (message.Length > 0) throw new InvalidOperationException(message);
    }
    private int Bag(uint item) => ReadVisitInventory(new[] { item })[item];
    private Dictionary<uint,int> ReadVisitInventory(IEnumerable<uint> items)
    {
        var ids = items.Distinct().ToArray();
        using var doc = JsonDocument.Parse(Svc.PluginInterface.GetIpcSubscriber<string, string>("GatherBuddyReborn.GetHarvestStock")
            .InvokeFunc(JsonSerializer.Serialize(ids)));
        var r = doc.RootElement;
        if (!r.GetProperty("Available").GetBoolean() || r.GetProperty("Character").GetUInt64() != owner) throw new InvalidOperationException("재고 재확인 대기");
        var counts = r.GetProperty("Rows").EnumerateArray().ToDictionary(x => x.GetProperty("ItemId").GetUInt32(), x => x.GetProperty("Inventory").GetInt32());
        if (ids.Any(id => !counts.ContainsKey(id)) || counts.Values.Any(count => count < 0)) throw new InvalidOperationException("재고 응답이 불완전합니다.");
        foreach (var (id, count) in counts)
            if (stock.TryGetValue(id, out var previous)) stock[id] = previous with { Inventory = count };
        return counts;
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
        var seeds = Profile.BatchFor(cell) is { Cross: true } batch
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
        if (resetRoutes) { work.HomeArrivals.Clear(); work.WalkedRoutes.Clear(); work.FailedRoutes.Clear(); }
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
        return new(true, work.Completed, Math.Max(work.Completed, Profile.ActiveBatches.Sum(b => b.Cells.Count)), "Gardening:" + phase, "", phase, work.Verified);
    }
    private AutomationObservation End(Visit work, string result)
    {
        foreach (var batch in Profile.ActiveBatches.Where(b => !b.Clearing)) {
            if (result != "Succeeded") break;
            var waiting = Profile.Cells.Where(c => c.Enabled && batch.Cells.Contains(c.Id) && !c.Owned
                && c.Pending.Length == 0 && c.State is "empty" or "unscanned").ToArray();
            if (waiting.Length > 0) {
                result = "Partial";
                var missing = waiting.GroupBy(c => c.Seed).Select(g => (Item: g.Key, Needed: g.Count()))
                    .Append((Item: GardenPolicy.Soil(batch.Cross, Profile.Soil), Needed: waiting.Length)).Where(p => (stock.GetValueOrDefault(p.Item)?.Inventory ?? 0) < p.Needed)
                    .Select(p => $"{catalog!.Names.GetValueOrDefault(p.Item) ?? p.Item.ToString()} {stock.GetValueOrDefault(p.Item)?.Inventory ?? 0}/{p.Needed}").ToArray();
                work.Phase = missing.Length > 0 ? "재배 준비 미완료 · " + string.Join(", ", missing)
                    : batch.Cross && waiting.Any(c => !GardenPolicy.CrossPlantingClear(batch, c, Profile.Cells))
                        ? "교배 재파종 대기 · 인접 작물 수확 후 진행" : $"재배 칸 확인·재시도 대기 · 남은 {waiting.Length}칸";
            }
        }
        if (result == "Succeeded" && Profile.Cells.Any(c => c.Enabled && Profile.BatchFor(c) != null && c.Pending.Length > 0)) {
            result = "Partial"; work.Phase = "처리 결과 확인 대기 · 미확정 조작을 재전송하지 않습니다.";
        }
        if (result == "Succeeded" && Profile.Cells.Any(c => c.Enabled && Profile.BatchFor(c) != null
            && c.Owned && c.ColorStep < Colors(c).Length && c.ColorFed.AddHours(1) <= DateTime.UtcNow
            && (stock.GetValueOrDefault(Colors(c)[c.ColorStep])?.Inventory ?? 0) == 0)) {
            result = "Partial"; work.Phase = "색상 재료 준비 대기 · 다음 방문 전에 보충합니다.";
        }
        if (result == "Succeeded" && Profile.Cells.Any(c => c.Enabled && Profile.BatchFor(c) != null
            && (c.State == "withered" || !c.Owned && c.Crop != 0))) {
            result = "Partial"; work.Phase = "작물 확인 필요 · 마른 작물·다른 작물을 자동 폐기하지 않습니다.";
        }
        if (result == "Succeeded") work.Phase = $"이번 방문 완료 · 심기 {work.Plants}, 관리 {work.Tends}, 수확 {work.Harvests}, 색상 {work.Feeds} · "
            + (!Profile.ActiveBatches.Any() ? "현재 목표 처리 완료" : "성장·다음 관리 대기");
        snapshot = JsonSerializer.Serialize(new { Available = true, Character = owner, Ready = false, Detail = work.Phase, Supply = (object?)null,
            Goals = Profile.Goals, BookItems = Profile.BookItems, Growable = catalog?.Crops.Keys,
            Searchable = catalog?.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct() });
        visit = null; nextRefresh = DateTime.MinValue; config.Save();
        return new(false, work.Completed, Math.Max(work.Completed, Profile.ActiveBatches.Sum(b => b.Cells.Count)),
            work.RequiredSpace > 0 ? "Gardening:BagSpace/" + work.RequiredSpace : "Gardening", result, work.Phase, work.Verified);
    }
}
