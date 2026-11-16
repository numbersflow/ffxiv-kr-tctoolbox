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
    private int quantity = 1, cellIndex;
    private string configureError = "";

    private string Configure(string json)
    {
        try {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (owner == 0 || Svc.PlayerState.ContentId != owner) throw new InvalidOperationException("캐릭터 확인 대기");
            if (r.TryGetProperty("Character", out var character) && character.GetUInt64() != owner) throw new InvalidOperationException("다른 캐릭터의 설정 요청");
            catalog ??= new();
            var action = r.GetProperty("Action").GetString();
            if (visit != null && action is not ("CanGrow" or "GetSettings" or "Register")) {
                if (action is not ("AddGoal" or "SetBook" or "RemoveGoal" or "RemoveCell" or "Register")) throw new ArgumentException("Unknown configuration action");
                var queued = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                queued["Character"] = owner;
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
            } else if (action == "RemoveGoal") Profile.Goals.RemoveAll(g => g.Item == r.GetProperty("Item").GetUInt32());
            else if (action == "RemoveCell") Profile.Cells.RemoveAll(c => c.Id == r.GetProperty("Id").GetString());
            else if (action == "Register") RegisterCell(r.GetProperty("Group").GetString() ?? "", r.GetProperty("Index").GetInt32(), r.GetProperty("Route").GetString() ?? "");
            else if (action == "GetSettings") return JsonSerializer.Serialize(new { Accepted = true, Character = owner, Profile });
            else if (action != "CanGrow") throw new ArgumentException("Unknown Harvest configuration action");
            config.Save(); nextRefresh = DateTime.MinValue;
            return "{\"Accepted\":true}";
        } catch (Exception ex) { return JsonSerializer.Serialize(new { Accepted = false, Error = ex.Message }); }
    }

    private unsafe void RegisterCell(string group, int index, string travel)
    {
        if (string.IsNullOrWhiteSpace(group) || index is < 0 or > 7 || string.IsNullOrWhiteSpace(travel) || travel.Length > 256)
            throw new ArgumentException("시설 이름, 칸 번호 0~7, Lifestream 경로가 필요합니다.");
        var target = Svc.Targets.Target ?? throw new InvalidOperationException("화단의 한 칸 또는 화분을 선택하세요.");
        var player = Svc.Objects.LocalPlayer ?? throw new InvalidOperationException("캐릭터 확인 대기");
        var kind = garden.GetPatchKind(target.GameObjectId);
        if (kind == "unknown") throw new InvalidOperationException("재배할 수 있는 시설을 선택하세요.");
        var housing = HousingManager.Instance();
        if (housing == null || !housing->HasHousePermissions()) throw new InvalidOperationException("주택 관리 권한 확인 필요");
        var house = kind == "pot" ? housing->GetCurrentIndoorHouseId().Id : housing->GetCurrentHouseId().Id;
        if (house == 0) throw new InvalidOperationException("현재 주택 식별 실패");
        if (Profile.Cells.Any(c => c.Group == group && c.Index == index && c.World == player.CurrentWorld.RowId && c.House == house))
            throw new ArgumentException("같은 시설·칸 번호가 이미 등록돼 있습니다.");
        Profile.Cells.Add(new() { Group = group, Index = index, Kind = kind, House = house,
            World = player.CurrentWorld.RowId, Territory = Svc.ClientState.TerritoryType,
            X = target.Position.X, Y = target.Position.Y, Z = target.Position.Z,
            ApproachX = player.Position.X, ApproachY = player.Position.Y, ApproachZ = player.Position.Z, Travel = travel });
    }

    private void Post(object request)
    {
        var data = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(request))!;
        data["Character"] = owner;
        var json = data.ToJsonString();
        _ = Svc.Framework.RunOnFrameworkThread(() => {
            using var result = JsonDocument.Parse(Configure(json));
            configureError = result.RootElement.GetProperty("Accepted").GetBoolean() ? "" : result.RootElement.GetProperty("Error").GetString() ?? "설정 실패";
        });
    }

    internal void Draw()
    {
        ImGui.TextWrapped("워크플로우의 하베스트 작업이 켜져 있고 준비·방문할 일이 있을 때만 자동 실행합니다.");
        if (error.Length > 0) ImGui.TextWrapped(error);
        if (configureError.Length > 0) ImGui.TextWrapped(configureError);
        ImGui.InputTextWithHint("##harvestSearch", "작물·꽃·씨앗 이름 또는 ID", ref search, 120);
        ImGui.InputInt("목표 보유량", ref quantity);
        if (catalog != null && search.Length > 0) {
            foreach (var pair in catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct().Where(id =>
                catalog.Names[id].Contains(search, StringComparison.OrdinalIgnoreCase) || id.ToString().Contains(search)).Take(30)) {
                if (ImGui.SmallButton("추가##harvest" + pair)) Post(new { Action = "AddGoal", Item = pair, Quantity = quantity });
                ImGui.SameLine(); ImGui.TextUnformatted(catalog.Names[pair] + " #" + pair);
            }
        }
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
        ImGui.Separator();
        ImGui.InputText("시설 이름", ref groupName, 80);
        ImGui.InputInt("칸 번호 (시계 방향 0~7)", ref cellIndex);
        ImGui.InputText("이 시설로 오는 Lifestream 경로", ref route, 256);
        ImGui.TextWrapped("목표 칸·화분을 선택하고 상호작용할 위치에 선 뒤 등록하세요. 다른 화단은 다른 시설 이름을 사용합니다.");
        if (ImGui.Button("선택한 칸과 현재 접근 위치 등록")) Post(new { Action = "Register", Group = groupName, Index = cellIndex, Route = route });
        if (profile != null) foreach (var cell in profile.Cells.ToArray()) {
            if (ImGui.SmallButton("등록 해제##cell" + cell.Id)) Post(new { Action = "RemoveCell", Id = cell.Id });
            ImGui.SameLine(); ImGui.TextUnformatted($"{cell.Group} / {cell.Index} · {cell.State} · {cell.Error}");
        }
    }
}
