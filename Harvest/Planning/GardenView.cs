using System.Collections.Generic;
using System.Linq;

namespace Harvest.Planning;

public sealed record GardenItemView(uint Item, uint Seed, bool Flower, double Hours, bool Selected,
    bool BookSelected, int Quantity, bool Complete, int Needed, int Inventory, int Retainer, bool Known);

public static class GardenView
{
    public static GardenItemView[] Items(HarvestProfile profile, IReadOnlyDictionary<uint, Crop> crops,
        IReadOnlyDictionary<uint, uint> seeds, IReadOnlyDictionary<uint, Stock> stock,
        IReadOnlyDictionary<uint, int> books, bool known, uint expansion = 6)
    {
        var goals = profile.Goals.ToDictionary(g => g.Item);
        return crops.Keys.Concat(seeds.Keys).Distinct().Select(id => {
            var crop = crops.GetValueOrDefault(seeds.GetValueOrDefault(id, id));
            goals.TryGetValue(id, out var goal);
            stock.TryGetValue(id, out var held);
            return new GardenItemView(id, seeds.ContainsKey(id) ? id : crop?.Seed ?? 0,
                crop?.Flower ?? false, crop?.Hours ?? 0, goal?.Enabled == true,
                profile.BookSelected(id, expansion), goal?.Quantity ?? 0, goal?.Complete ?? false,
                books.GetValueOrDefault(id), held?.Inventory ?? 0, held?.Retainer ?? 0, known && held != null);
        }).ToArray();
    }
}
