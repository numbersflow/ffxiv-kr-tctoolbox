using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Harvest.Planning;

internal sealed partial class HarvestService
{
    private string search = "", groupName = "텃밭 1", route = "home";
    private int cellIndex;
    private string configureError = "";
    private string selectedCell = "";
    private bool selectedOnly;

    private string Configure(string json)
    {
        try {
            if (disposed) throw new InvalidOperationException("재배 플러그인 재로드 중");
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (owner == 0 || Svc.PlayerState.ContentId != owner) throw new InvalidOperationException("캐릭터 확인 대기");
            if (r.TryGetProperty("Character", out var character) && character.GetUInt64() != owner) throw new InvalidOperationException("다른 캐릭터의 설정 요청");
            catalog ??= new();
            var action = r.GetProperty("Action").GetString();
            uint[]? selection = null;
            if (action == "SetSelection") {
                selection = r.GetProperty("Items").Deserialize<uint[]>() ?? [];
                if (selection.Length > 2048 || selection.Any(id => !catalog.Crops.ContainsKey(id) && !catalog.SeedCrops.ContainsKey(id)))
                    throw new ArgumentException("재배 품목 선택 범위가 잘못되었습니다.");
                _ = r.GetProperty("Selected").GetBoolean(); _ = r.GetProperty("Book").GetBoolean();
            }
            if (visit != null && action is not ("CanGrow" or "GetSettings" or "Register")) {
                if (action is not ("AddGoal" or "SetBook" or "RemoveGoal" or "RemoveCell" or "Register" or "SetCell" or "SetSelection" or "AutoRegister")) throw new ArgumentException("Unknown configuration action");
                var queued = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                queued["Character"] = owner;
                StopOwnedTravel(visit);
                pendingConfiguration.Enqueue(queued.ToJsonString());
                visit.Cancel = true;
                return "{\"Accepted\":true,\"Pending\":true}";
            }
            if (action == "CanGrow") return JsonSerializer.Serialize(new { Accepted = true, CanGrow = catalog.Crops.ContainsKey(r.GetProperty("Item").GetUInt32()) });
            if (action is "AddGoal" or "SetBook") {
                var item = r.GetProperty("Item").GetUInt32();
                if (!catalog.Crops.ContainsKey(item) && !catalog.SeedCrops.ContainsKey(item)) throw new ArgumentException("재배 품목이 아닙니다.");
                if (action == "SetBook") {
                    if (r.GetProperty("Selected").GetBoolean()) Profile.BookItems.Add(item); else Profile.BookItems.Remove(item);
                } else {
                    var target = r.GetProperty("Quantity").GetInt32();
                    if (target <= 0 || target > 999999) throw new ArgumentException("수량은 1~999999 범위여야 합니다.");
                    var goal = Profile.Goals.FirstOrDefault(g => g.Item == item);
                    if (goal == null) Profile.Goals.Add(new() { Item = item, Quantity = target });
                    else { goal.Quantity = target; goal.Enabled = true; goal.Complete = false; }
                }
            } else if (action == "SetSelection") Profile.SelectItems(selection!, r.GetProperty("Selected").GetBoolean(), r.GetProperty("Book").GetBoolean());
            else if (action == "RemoveGoal") Profile.Goals.RemoveAll(g => g.Item == r.GetProperty("Item").GetUInt32());
            else if (action == "SetCell") Profile.SelectCell(r.GetProperty("Id").GetString() ?? "", r.GetProperty("Selected").GetBoolean());
            else if (action == "RemoveCell") Profile.Cells.RemoveAll(c => c.Id == r.GetProperty("Id").GetString());
            else if (action == "AutoRegister") return AutoRegister(r.GetProperty("Group").GetString() ?? "", r.GetProperty("Route").GetString() ?? "");
            else if (action == "Register") {
                var cell = RegisterCell(r.GetProperty("Group").GetString() ?? "", r.GetProperty("Index").GetInt32(), r.GetProperty("Route").GetString() ?? "");
                nextRefresh = DateTime.MinValue; PublishGardenView();
                var message = $"재배 칸 등록 완료 · {cell.Group} / {cell.Index} · {(cell.Kind == "pot" ? "실내 화분" : "실외 밭")} · 총 {Profile.Cells.Count}칸";
                Svc.Log.Information(message);
                return JsonSerializer.Serialize(new { Accepted = true, Message = message, Cell = cell });
            }
            else if (action == "GetSettings") return JsonSerializer.Serialize(new { Accepted = true, Character = owner, Profile });
            else if (action != "CanGrow") throw new ArgumentException("Unknown Harvest configuration action");
            config.Save(); nextRefresh = DateTime.MinValue;
            PublishGardenView();
            return "{\"Accepted\":true}";
        } catch (Exception ex) {
            Svc.Log.Warning("Harvest configuration rejected: " + ex.Message);
            return JsonSerializer.Serialize(new { Accepted = false, Error = ex.Message });
        }
    }

    private unsafe GardenCell RegisterCell(string group, int index, string travel)
    {
        if (string.IsNullOrWhiteSpace(group) || index is < 0 or > 7 || string.IsNullOrWhiteSpace(travel) || travel.Length > 256)
            throw new ArgumentException("시설 이름, 칸 번호 0~7, Lifestream 경로가 필요합니다.");
        var target = Svc.Targets.Target ?? throw new InvalidOperationException("화단의 한 칸 또는 화분을 선택하세요.");
        var player = Svc.Objects.LocalPlayer ?? throw new InvalidOperationException("캐릭터 확인 대기");
        var kind = garden.GetPatchKind(target.GameObjectId);
        if (kind == "unknown") throw new InvalidOperationException($"선택 대상 '{target.Name.TextValue}'은 재배 칸이 아닙니다. 밭 전체가 아닌 작물을 심는 칸을 선택하세요. ({target.ObjectKind}, {target.BaseId})");
        var housing = HousingManager.Instance();
        if (housing == null || !garden.CanManageCurrentHouse()) throw new InvalidOperationException("현재 주택의 재배 관리 권한을 확인할 수 없습니다.");
        var house = kind == "pot" ? housing->GetCurrentIndoorHouseId().Id : housing->GetCurrentHouseId().Id;
        if (house is 0 or ulong.MaxValue) throw new InvalidOperationException("현재 주택 식별 실패");
        if (garden.GetPatchDistance(target.GameObjectId) > 6f) throw new InvalidOperationException("선택한 재배 칸 가까이에서 등록하세요. 현재 서 있는 위치도 접근 위치로 저장합니다.");
        var patch = ReadPatch(target.GameObjectId) ?? throw new InvalidOperationException("재배 칸 내부 식별자를 확인할 수 없습니다.");
        var cell = new GardenCell { Group = group, Index = index, Kind = kind, House = house,
            LayoutInstanceKey = patch.InstanceKey, LayoutSubId = patch.SubId,
            World = player.CurrentWorld.RowId, Territory = Svc.ClientState.TerritoryType,
            X = target.Position.X, Y = target.Position.Y, Z = target.Position.Z,
            ApproachX = player.Position.X, ApproachY = player.Position.Y, ApproachZ = player.Position.Z, Travel = travel };
        Profile.RegisterCell(cell, config.Save);
        return cell;
    }

    private unsafe GardenPatch? ReadPatch(ulong id)
    {
        var obj = Svc.Objects.SearchById(id);
        if (obj == null) return null;
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        var group = native->SharedGroupLayoutInstance;
        if (group == null) return null;
        var slot = group->Transform.Translation;
        return new(obj.GameObjectId, garden.GetPatchKind(id), group->Id.InstanceKey, group->SubId,
            obj.Position, new(slot.X, slot.Y, slot.Z));
    }

    private GardenPatch[] NearbyPatches() => garden.GetNearbyPatchIds().Select(ReadPatch).OfType<GardenPatch>().ToArray();

    private string AutoRegister(string group, string travel)
    {
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(travel) || travel.Length > 256)
            throw new ArgumentException("시설 이름과 Lifestream 경로가 필요합니다.");
        if (!garden.CanManageCurrentHouse()) throw new InvalidOperationException("현재 주택의 재배 관리 권한을 확인할 수 없습니다.");
        var player = Svc.Objects.LocalPlayer ?? throw new InvalidOperationException("캐릭터 확인 대기");
        var location = CurrentRouteLocation();
        if (location.House is 0 or ulong.MaxValue) throw new InvalidOperationException("현재 주택 식별 실패");
        var patches = GardenRegistration.Nearest(NearbyPatches(), player.Position);
        var cells = patches.Select((p, index) => new GardenCell {
            Group = group, Index = index, Kind = p.Kind, World = location.World, Territory = location.Territory, House = location.House,
            LayoutInstanceKey = p.InstanceKey, LayoutSubId = p.SubId,
            X = p.Origin.X, Y = p.Origin.Y, Z = p.Origin.Z,
            ApproachX = player.Position.X, ApproachY = player.Position.Y, ApproachZ = player.Position.Z, Travel = travel
        }).ToArray();
        var added = Profile.RegisterPatches(cells, config.Save);
        nextRefresh = DateTime.MinValue; PublishGardenView();
        var message = $"자동 등록 완료 · 발견 {patches.Length}칸 · 신규 {added}칸 · 기존 {patches.Length - added}칸 유지";
        Svc.Log.Information($"{message}; instance={patches[0].InstanceKey}; subIds={string.Join(',', patches.Select(p => p.SubId))}");
        return JsonSerializer.Serialize(new { Accepted = true, Message = message });
    }

    private object RegistrationTarget()
    {
        var target = Svc.Targets.Target;
        return new { Name = target?.Name.TextValue ?? "선택 없음", ObjectId = target?.GameObjectId ?? 0,
            Kind = target == null ? "unknown" : garden.GetPatchKind(target.GameObjectId),
            Permission = garden.CanManageCurrentHouse(), Nearby = NearbyPatches() };
    }
    private void Post(object request)
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!;
        data["Character"] = owner;
        var json = data.ToJsonString();
        _ = Svc.Framework.RunOnFrameworkThread(() => {
            if (disposed) return;
            using var result = JsonDocument.Parse(Configure(json));
            configureError = result.RootElement.GetProperty("Accepted").GetBoolean() ? "" : result.RootElement.GetProperty("Error").GetString() ?? "설정 실패";
        });
    }

    internal void Draw()
    {
        if (disposed) return;
        ImGui.TextWrapped("워크플로우의 재배 작업이 켜져 있고 준비·방문할 일이 있을 때만 자동 실행합니다.");
        if (error.Length > 0) ImGui.TextWrapped(error);
        if (configureError.Length > 0) ImGui.TextWrapped(configureError);
        if (!ImGui.BeginTabBar("##gardenTabs")) return;
        try {
            if (ImGui.BeginTabItem("재배 품목")) {
                try { DrawCropSelection(); } finally { ImGui.EndTabItem(); }
            }
            if (ImGui.BeginTabItem("선택한 목표")) {
                try { DrawSelectedGoals(); } finally { ImGui.EndTabItem(); }
            }
            if (ImGui.BeginTabItem("등록 화단")) {
                try { DrawRegisteredCells(); } finally { ImGui.EndTabItem(); }
            }
        } finally { ImGui.EndTabBar(); }
    }

    private void DrawCropSelection()
    {
        ImGui.InputTextWithHint("##harvestSearch", "작물·꽃·씨앗 이름 또는 ID", ref search, 120);
        ImGui.Checkbox("선택한 품목만", ref selectedOnly);
        var profile = CurrentProfile;
        if (catalog == null || profile == null) { ImGui.TextUnformatted("현재 캐릭터의 재배 품목 조회 중"); return; }
        var goals = profile.Goals.ToDictionary(g => g.Item);
        var items = catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct()
            .Where(id => (!selectedOnly || goals.ContainsKey(id)) && (search.Length == 0
                || catalog.Names[id].Contains(search, StringComparison.OrdinalIgnoreCase) || id.ToString().Contains(search)))
            .OrderBy(id => catalog.Names[id]).ToArray();
        ImGui.TextUnformatted($"{items.Length}개 품목 · 여러 품목 선택 가능 · 수량 입력 후 Enter로 적용");
        if (!ImGui.BeginTable("##gardenItems", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable)) return;
        try {
            ImGui.TableSetupColumn("선택"); ImGui.TableSetupColumn("품목"); ImGui.TableSetupColumn("종류");
            ImGui.TableSetupColumn("목표 보유량"); ImGui.TableSetupColumn("진행"); ImGui.TableHeadersRow();
            foreach (var id in items) {
                goals.TryGetValue(id, out var goal);
                var chosen = goal?.Enabled == true;
                ImGui.TableNextRow(); ImGui.TableNextColumn();
                if (ImGui.Checkbox("##chooseCrop" + id, ref chosen)) Post(chosen
                    ? new { Action = "AddGoal", Item = id, Quantity = Math.Max(1, goal?.Quantity ?? 1) }
                    : (object)new { Action = "RemoveGoal", Item = id });
                ImGui.TableNextColumn(); ImGui.TextUnformatted(catalog.Names[id]);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("아이템 ID: " + id);
                ImGui.TableNextColumn(); ImGui.TextUnformatted(catalog.SeedCrops.ContainsKey(id) ? "씨앗"
                    : catalog.Crops[id].Flower ? "꽃·화분" : "작물");
                ImGui.TableNextColumn();
                if (goal != null) {
                    var count = goal.Quantity;
                    ImGui.SetNextItemWidth(-1);
                    if (ImGui.InputInt("##cropQuantity" + id, ref count, 0, 0, "%d", ImGuiInputTextFlags.EnterReturnsTrue))
                        Post(new { Action = "AddGoal", Item = id, Quantity = count });
                } else ImGui.TextUnformatted("—");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(goal == null ? "미선택" : goal.Complete ? "완료" : "확보 중");
            }
        } finally { ImGui.EndTable(); }
    }

    private void DrawSelectedGoals()
    {
        var profile = CurrentProfile;
        if (profile != null) {
            ImGui.Separator();
            foreach (var goal in profile.Goals.ToArray()) {
                if (ImGui.SmallButton("삭제##goal" + goal.Item)) Post(new { Action = "RemoveGoal", Item = goal.Item });
                ImGui.SameLine(); ImGui.TextUnformatted($"{catalog?.Names.GetValueOrDefault(goal.Item)} {goal.Quantity}개 · {(goal.Complete ? "완료" : "확보 중")}");
            }
            foreach (var id in profile.BookItems.ToArray()) {
                if (ImGui.SmallButton("제작 재료 제외##book" + id)) Post(new { Action = "SetBook", Item = id, Selected = false });
                ImGui.SameLine(); ImGui.TextUnformatted(catalog?.Names.GetValueOrDefault(id) ?? id.ToString());
            }
        }
        if (profile == null || profile.Goals.Count + profile.BookItems.Count == 0) ImGui.TextUnformatted("선택한 목표가 없습니다.");
    }

    private void DrawRegisteredCells()
    {
        var profile = CurrentProfile;
        if (profile != null && ImGui.BeginTable("##gardenCells", 3, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable)) {
            try {
                ImGui.TableSetupColumn("사용"); ImGui.TableSetupColumn("화단·칸"); ImGui.TableSetupColumn("상태"); ImGui.TableHeadersRow();
                foreach (var cell in profile.Cells) {
                    ImGui.TableNextRow(); ImGui.TableNextColumn();
                    var selected = cell.Enabled;
                    if (ImGui.Checkbox("##cellEnabled" + cell.Id, ref selected)) Post(new { Action = "SetCell", Id = cell.Id, Selected = selected });
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{cell.Group} / {cell.Index}##cell{cell.Id}", selectedCell == cell.Id)) selectedCell = cell.Id;
                    ImGui.TableNextColumn(); ImGui.TextUnformatted(CellState(cell.State));
                }
            } finally { ImGui.EndTable(); }
        }
        if (profile?.Cells.FirstOrDefault(c => c.Id == selectedCell) is { } current) {
            ImGui.TextWrapped($"{current.Group} / {current.Index} · {(current.Kind == "pot" ? "실내 화분" : "실외 화단")} · {current.Travel}");
            ImGui.TextWrapped($"현재 작물: {catalog?.Names.GetValueOrDefault(current.Crop) ?? "미확인"}");
            if (current.Error.Length > 0) ImGui.TextWrapped(current.Error);
            if (ImGui.Button("선택한 칸 등록 해제")) Post(new { Action = "RemoveCell", Id = current.Id });
        }
        if (profile == null || profile.Cells.Count == 0) ImGui.TextUnformatted("등록된 화단·화분이 없습니다.");
        ImGui.Separator();
        ImGui.InputText("시설 이름", ref groupName, 80);
        ImGui.InputInt("칸 번호 (시계 방향 0~7)", ref cellIndex);
        ImGui.InputText("이 시설로 오는 Lifestream 경로", ref route, 256);
        ImGui.TextWrapped("목표 칸·화분을 선택하고 상호작용할 위치에 선 뒤 등록하세요. 다른 화단은 다른 시설 이름을 사용합니다.");
        if (ImGui.Button("선택한 칸과 현재 접근 위치 등록")) Post(new { Action = "Register", Group = groupName, Index = cellIndex, Route = route });
    }

    private static string CellState(string state) => state switch {
        "empty" => "빈 칸", "growing" => "성장 중", "mature" => "수확 가능", "withered" => "말라 죽음", _ => "미확인"
    };
}
