using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Lumina.Excel.Sheets;
using Harvest.Core;

namespace Harvest.Planning;

internal sealed class Catalog
{
    public Dictionary<uint, Crop> Crops { get; } = [];
    public Dictionary<uint, uint> SeedCrops { get; } = [];
    public List<Cross> Crosses { get; } = [];
    internal sealed class ItemNames(IReadOnlyDictionary<uint, Item> items)
    {
        private readonly Dictionary<uint, string> names = [];
        public string this[uint id] => GetValueOrDefault(id) ?? throw new KeyNotFoundException();
        public string? GetValueOrDefault(uint id)
        {
            if (names.TryGetValue(id, out var name)) return name;
            if (!items.TryGetValue(id, out var item)) return null;
            return names[id] = item.Name.ExtractText();
        }
    }
    public ItemNames Names { get; }
    private static string Resource(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(suffix)))!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    public Catalog() : this(Svc.Data.GetExcelSheet<Item>(), Svc.Data.GetExcelSheet<GardeningSeed>()) { }
    internal Catalog(IEnumerable<Item> sourceItems, IEnumerable<GardeningSeed> sourceSeeds)
    {
        var items = sourceItems.ToDictionary(i => i.RowId);
        Names = new(items);
        var rows = sourceSeeds;
        var seedForRow = items.Values.Where(i => i.FilterGroup == 20 && i.ItemUICategory.RowId == 82)
            .ToDictionary(i => i.AdditionalData.RowId, i => i.RowId);
        using var timing = JsonDocument.Parse(Resource("seeds.json"));
        using var yields = JsonDocument.Parse(Resource("crop-yields.json"));
        foreach (var row in rows) {
            if (!seedForRow.TryGetValue(row.RowId, out var seed) || !items.ContainsKey(row.Item.RowId)) continue;
            SeedCrops[seed] = row.Item.RowId;
            var hours = timing.RootElement.TryGetProperty(row.Item.RowId.ToString(CultureInfo.InvariantCulture), out var value)
                ? value.GetProperty("duration").GetDouble() : 0;
            // shortcut: unknown crops reserve one item per cell; add verified yields when new seeds are supported.
            var yield = yields.RootElement.TryGetProperty(row.Item.RowId.ToString(CultureInfo.InvariantCulture), out var amount)
                ? amount.GetProperty("yield").GetInt32() : 1;
            if (yield <= 0) throw new InvalidDataException("Invalid crop yield");
            Crops[row.Item.RowId] = new(row.Item.RowId, seed, Names[row.Item.RowId], row.IsPlantPotFlowerSeed, hours, [], yield);
        }
        using var variants = JsonDocument.Parse(Resource("gardening-sources.json"));
        foreach (var p in variants.RootElement.EnumerateObject()) {
            var id = uint.Parse(p.Name, CultureInfo.InvariantCulture);
            var seed = p.Value[0].GetUInt32();
            if (!SeedCrops.TryGetValue(seed, out var basic) || !Crops.TryGetValue(basic, out var crop) || !items.ContainsKey(id)) continue;
            Crops[id] = crop with { Item = id, Name = Names[id], Colors = crop.Flower ? Pomaces(Names[id], Names[basic]) : [] };
        }
        foreach (var line in Resource("GardeningCrossbreed.csv").Split('\n').Skip(1).Where(l => !string.IsNullOrWhiteSpace(l))) {
            var ids = line.Trim().Split(',').Select(uint.Parse).ToArray();
            if (ids.Length == 3 && ids.All(SeedCrops.ContainsKey)) Crosses.Add(new(ids[0], ids[1], ids[2]));
        }
    }
    private static uint[] Pomaces(string target, string basic)
    {
        if (target == basic) return [];
        var color = target.Split(':').Last().Trim();
        return color switch {
            "빨강" => [14011], "파랑" => [14012], "노랑" => [14013],
            "초록" => [14012, 14013], "주황" => [14011, 14013], "보라" => [14011, 14012],
            "하양" or "검정" or "혼합" or "분홍" or "무지개" => [14011, 14012, 14013],
            _ => throw new InvalidDataException("미지원 꽃 색상: " + target)
        };
    }
}
