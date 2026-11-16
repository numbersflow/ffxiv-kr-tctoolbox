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
    public List<GardenCell> Cells { get; set; } = [];
    public GardenBatch? Batch { get; set; }
    public uint Soil { get; set; } = 7766;
    public DateTime RetryAt { get; set; }
}

public sealed record Crop(uint Item, uint Seed, string Name, bool Flower, double Hours, uint[] Colors);
public sealed record Cross(uint Output, uint A, uint B);
public sealed record Stock(int Inventory, int Retainer, bool CanProcure)
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
