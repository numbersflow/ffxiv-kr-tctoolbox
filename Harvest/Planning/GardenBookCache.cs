using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Harvest.Planning;

public sealed class GardenBookCache
{
    public Dictionary<uint, int> Needs { get; private set; } = [];
    public bool Ready { get; private set; }
    public bool Updating { get; private set; }
    public bool HasSnapshot { get; private set; }
    public string Error { get; private set; } = "";
    public void Reset() { Needs.Clear(); Ready = Updating = HasSnapshot = false; Error = ""; }
    public void Fail(string error) { Ready = Updating = false; Error = error; }
    public static Dictionary<uint, int> SelectedNeeds(HarvestProfile profile, IReadOnlyDictionary<uint, GardenBookCache> caches)
        => profile.BookItems.ToDictionary(id => id, id => {
            var scopes = profile.BookExpansions(id).ToArray();
            if (scopes.Contains(6u)) scopes = [6];
            return scopes.Sum(scope => caches.TryGetValue(scope, out var cache) ? cache.Needs.GetValueOrDefault(id) : 0);
        });
    public void Update(string json, ulong owner, uint expansion = 6)
    {
        try {
            using var doc = JsonDocument.Parse(json); var b = doc.RootElement;
            var pending = b.TryGetProperty("Pending", out var p) && p.GetBoolean();
            if (!b.GetProperty("Available").GetBoolean()) {
                if (!pending) throw new InvalidOperationException(b.TryGetProperty("Error", out var e) ? e.GetString() : "제작 재료 조회 실패");
                Ready = false; Updating = true; Error = ""; return;
            }
            if (owner == 0 || b.GetProperty("Character").GetUInt64() != owner || b.GetProperty("Expansion").GetUInt32() != expansion)
                throw new InvalidOperationException("제작수첩 조회 캐릭터·범위 확인 대기");
            if (b.GetProperty("Error").GetString() is { Length: > 0 } error) throw new InvalidOperationException(error);
            if (pending) { Ready = false; Updating = true; Error = ""; return; }
            var needs = b.GetProperty("Rows").EnumerateArray().ToDictionary(r => r.GetProperty("ItemId").GetUInt32(), r => r.GetProperty("Needed").GetInt32());
            if (needs.Any(p => p.Key == 0 || p.Value < 0)) throw new InvalidOperationException("제작 재료 수량 확인 실패");
            Needs = needs; Ready = HasSnapshot = true; Updating = false; Error = "";
        } catch (Exception ex) { Fail(ex.Message); }
    }
}

internal sealed class GardenBookPendingException() : InvalidOperationException("제작수첩 재료 백그라운드 갱신 중");
