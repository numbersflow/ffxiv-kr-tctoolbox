using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Harvest.Planning;

public sealed class HarvestGoal
{
    public uint Item { get; set; }
    public int Quantity { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public bool Complete { get; set; }
}

public sealed class GardenCell
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Group { get; set; } = "텃밭 1";
    public int Index { get; set; }
    public string Kind { get; set; } = "plot";
    public uint Territory { get; set; }
    public uint World { get; set; }
    public ulong House { get; set; }
    public uint LayoutInstanceKey { get; set; }
    public uint LayoutSubId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float ApproachX { get; set; }
    public float ApproachY { get; set; }
    public float ApproachZ { get; set; }
    public string Travel { get; set; } = "home";
    public bool Enabled { get; set; } = true;
    public uint Seed { get; set; }
    public uint Crop { get; set; }
    public string State { get; set; } = "unscanned";
    public DateTime Observed { get; set; }
    public DateTime RetryAt { get; set; }
    public string Error { get; set; } = "";
    public DateTime Planted { get; set; }
    public DateTime Tended { get; set; }
    public DateTime ColorFed { get; set; }
    public int ColorStep { get; set; }
    public bool Owned { get; set; }
    public string Pending { get; set; } = "";
    public int Before { get; set; }
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Position => new(X, Y, Z);
    [Newtonsoft.Json.JsonIgnore, System.Text.Json.Serialization.JsonIgnore]
    public Vector3 Approach => new(ApproachX, ApproachY, ApproachZ);
}

public sealed class GardenBatch
{
    public uint Goal { get; set; }
    public uint Output { get; set; }
    public uint SeedA { get; set; }
    public uint SeedB { get; set; }
    public int Required { get; set; }
    public bool Cross { get; set; }
    public bool Clearing { get; set; }
    public List<string> Cells { get; set; } = [];
}

public sealed class HarvestProfile
{
    public List<HarvestGoal> Goals { get; set; } = [];
    public HashSet<uint> BookItems { get; set; } = [];
    public Dictionary<uint, HashSet<uint>> BookScopes { get; set; } = [];
    public List<GardenCell> Cells { get; set; } = [];
    public Dictionary<string, GardenWalkRoute> WalkRoutes { get; set; } = [];
    public int RegisterPatches(GardenCell[] discovered, Action persist)
    {
        if (discovered.Length == 0) throw new ArgumentException("발견된 재배 칸이 없습니다.");
        var first = discovered[0];
        bool SameHouse(GardenCell c) => c.World == first.World && c.Territory == first.Territory && c.House == first.House;
        var legacy = Cells.Where(c => SameHouse(c) && c.LayoutInstanceKey == 0 && c.Kind == first.Kind
            && Vector3.DistanceSquared(c.Position, first.Position) < 0.1f * 0.1f).ToArray();
        if (legacy.Any(c => c.Owned || c.Pending.Length > 0))
            throw new InvalidOperationException("기존 칸의 진행 중 재배 기록을 확인해야 합니다. 자동 등록으로 덮어쓰지 않습니다.");
        var previous = Cells;
        var previousBatch = Batch;
        var next = Cells.Except(legacy).ToList();
        var added = 0;
        foreach (var cell in discovered) {
            if (next.Any(c => SameHouse(c) && c.Kind == cell.Kind && Vector3.DistanceSquared(c.Position, cell.Position) < 0.75f * 0.75f && c.LayoutSubId == cell.LayoutSubId)) continue;
            next.Add(cell); added++;
        }
        Cells = next;
        if (Batch != null && legacy.Any(c => Batch.Cells.Contains(c.Id))) Batch = null;
        try { persist(); }
        catch { Cells = previous; Batch = previousBatch; throw; }
        return added;
    }
    public void RegisterCell(GardenCell cell, Action persist)
    {
        var sameHouse = Cells.Where(c => c.World == cell.World && c.Territory == cell.Territory && c.House == cell.House).ToArray();
        if (sameHouse.Any(c => c.Group == cell.Group && c.Index == cell.Index))
            throw new ArgumentException("같은 시설·칸 번호가 이미 등록돼 있습니다. 목록을 확인하거나 다른 번호를 선택하세요.");
        if (sameHouse.Any(c => c.Kind == cell.Kind && (c.LayoutInstanceKey != 0 && cell.LayoutInstanceKey != 0
                ? Vector3.DistanceSquared(c.Position, cell.Position) < 0.75f * 0.75f && c.LayoutSubId == cell.LayoutSubId
                : Vector3.Distance(c.Position, cell.Position) < 0.1f)))
            throw new ArgumentException("이미 등록한 재배 칸입니다. 게임에서 다른 칸을 선택하세요.");
        Cells.Add(cell);
        try { persist(); }
        catch { Cells.Remove(cell); throw; }
    }
    public GardenWalkRoute SaveWalkRoute(ManualGardenRoute recording, string token, ulong owner,
        GardenRouteLocation location, Vector3 position, long now, Action? persist = null)
    {
        var route = recording.Finish(token, owner, location, position, now);
        WalkRoutes.TryGetValue(route.Group, out var previous);
        var travel = Cells.Where(route.Matches).ToDictionary(c => c.Id, c => c.Travel);
        WalkRoutes[route.Group] = route;
        foreach (var cell in Cells) if (route.Matches(cell)) cell.Travel = route.Travel;
        try { persist?.Invoke(); }
        catch (Exception ex) {
            if (previous == null) WalkRoutes.Remove(route.Group); else WalkRoutes[route.Group] = previous;
            foreach (var cell in Cells) if (travel.TryGetValue(cell.Id, out var oldTravel)) cell.Travel = oldTravel;
            recording.Cancel("경로 파일 저장 실패 · " + ex.Message);
            throw;
        }
        return route;
    }
    public GardenBatch? Batch { get; set; }
    public uint Soil { get; set; } = 7766;
    public DateTime RetryAt { get; set; }
    public void SelectCell(string id, bool selected)
    {
        var cell = Cells.Find(c => c.Id == id) ?? throw new ArgumentException("등록된 화단 칸이 아닙니다.");
        cell.Enabled = selected;
    }
    public IEnumerable<uint> BookExpansions(uint item) => BookScopes.TryGetValue(item, out var scopes) ? scopes : new uint[] { 6 };
    public bool BookSelected(uint item, uint expansion = 6) => BookItems.Contains(item)
        && (expansion == 6 || BookExpansions(item).Contains(6u) || BookExpansions(item).Contains(expansion));
    public bool SetLegacyBookScope(uint expansion)
    {
        if (expansion > 6) throw new ArgumentOutOfRangeException(nameof(expansion));
        var legacy = BookItems.Where(id => !BookScopes.ContainsKey(id)).ToArray();
        foreach (var id in legacy) BookScopes[id] = [expansion];
        return legacy.Length > 0;
    }
    public void SelectBook(uint item, bool selected, uint expansion)
    {
        if (expansion > 6) throw new ArgumentOutOfRangeException(nameof(expansion));
        var scopes = BookItems.Contains(item) ? BookExpansions(item).ToHashSet() : new HashSet<uint>();
        if (expansion == 6) { scopes.Clear(); if (selected) scopes.Add(6); }
        else {
            if (scopes.Remove(6)) scopes.UnionWith(Enumerable.Range(0, 6).Select(i => (uint)i));
            if (selected) scopes.Add(expansion); else scopes.Remove(expansion);
        }
        if (scopes.Count == 0) { BookItems.Remove(item); BookScopes.Remove(item); }
        else { BookItems.Add(item); BookScopes[item] = scopes; }
    }
    public void SelectItems(IEnumerable<uint> items, bool selected, bool book, uint expansion = 6)
    {
        foreach (var id in items.Distinct()) {
            if (book) SelectBook(id, selected, expansion);
            else if (!selected) Goals.RemoveAll(g => g.Item == id);
            else if (Goals.Find(g => g.Item == id) is { } goal) goal.Enabled = true;
            else Goals.Add(new() { Item = id, Quantity = 1 });
        }
    }
}

public sealed record Crop(uint Item, uint Seed, string Name, bool Flower, double Hours, uint[] Colors, int Yield = 1);
public sealed record Cross(uint Output, uint A, uint B);
public sealed record Stock(int Inventory, int Retainer, bool CanProcure, bool CanProcureNow = true)
{
    public int Total => checked(Inventory + Retainer);
}
public sealed record SeedStage(uint Output, uint A, uint B, int Required);
public sealed record SeedPlan(IReadOnlyList<SeedStage> Stages, IReadOnlyDictionary<uint, int> Supplies, string Error);

public static class SeedPlanner
{
    public static SeedPlan Build(uint seed, int required, IReadOnlyDictionary<uint, Stock> stock, IReadOnlyList<Cross> crosses)
    {
        if (seed == 0 || required <= 0 || required > 999999) throw new ArgumentException("잘못된 씨앗 목표");
        var paths = new Dictionary<uint, (int Depth, Cross? Cross)>();
        foreach (var (id, value) in stock)
            if (value.Total > 0 || value.CanProcure) paths[id] = (0, null);
        bool changed;
        do {
            changed = false;
            foreach (var cross in crosses) {
                if (!paths.TryGetValue(cross.A, out var a) || !paths.TryGetValue(cross.B, out var b)) continue;
                var depth = 1 + Math.Max(a.Depth, b.Depth);
                if (paths.TryGetValue(cross.Output, out var old) && old.Depth <= depth) continue;
                paths[cross.Output] = (depth, cross); changed = true;
            }
        } while (changed);
        var stages = new List<SeedStage>();
        var supplies = new Dictionary<uint, int>();
        var visiting = new HashSet<uint>();
        bool Visit(uint id, int count) {
            if (stock.TryGetValue(id, out var held) && held.Total >= count) return true;
            if (held?.CanProcure == true) { supplies[id] = Math.Max(supplies.GetValueOrDefault(id), count); return true; }
            if (!paths.TryGetValue(id, out var path) || !visiting.Add(id)) return false;
            var candidates = crosses.Where(c => c.Output == id && paths.ContainsKey(c.A) && paths.ContainsKey(c.B))
                .OrderBy(c => 1 + Math.Max(paths[c.A].Depth, paths[c.B].Depth)).Distinct();
            foreach (var pair in candidates) {
                var previousSupplies = new Dictionary<uint, int>(supplies);
                var previousStages = stages.ToArray();
                var valid = Visit(pair.A, pair.A == pair.B ? 2 : 1) && (pair.A == pair.B || Visit(pair.B, 1));
                if (valid) {
                    visiting.Remove(id);
                    var existing = stages.FindIndex(s => s.Output == id);
                    if (existing < 0) stages.Add(new(id, pair.A, pair.B, count));
                    else if (stages[existing].Required < count) stages[existing] = stages[existing] with { Required = count };
                    return true;
                }
                supplies.Clear(); foreach (var p in previousSupplies) supplies[p.Key] = p.Value;
                stages.Clear(); stages.AddRange(previousStages);
            }
            visiting.Remove(id);
            return false;
        }
        return Visit(seed, required) ? new(stages, supplies, "") : new([], supplies, "시작 씨앗까지 연결되는 확보 경로 확인 필요");
    }
}
