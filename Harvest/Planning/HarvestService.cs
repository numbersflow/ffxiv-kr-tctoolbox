using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Harvest.Core;
using Harvest.Modules;
using LocalAutomation;

namespace Harvest.Planning;

internal sealed partial class HarvestService : IDisposable
{
    private readonly Configuration config;
    private readonly AutoGardensWork garden;
    private Catalog? catalog;
    private readonly AutomationEndpoint endpoint;
    private readonly ICallGateProvider<string> snapshotGate;
    private readonly ICallGateProvider<string, string> configGate;
    private readonly ICallGateProvider<string> protectionGate;
    private readonly ICallGateProvider<string> soilVentureGate;
    private string soilVentureSnapshot = "{\"Available\":false}";
    private readonly ICallGateProvider<string> gardenViewGate;
    private readonly ICallGateProvider<uint, string> gardenScopeGate;
    private readonly ConcurrentDictionary<uint, string> gardenScopes = new();
    private volatile int requestedGardenExpansion = 6;
    private string gardenView = "{\"Available\":false}";
    private bool stockKnown, inventoryChecked;
    private readonly GardenBookCache bookCache = new();
    private readonly Dictionary<uint, GardenBookCache> scopedBookCaches = [];
    private Dictionary<uint, int>? selectedBookNeeds;
    private bool bookReady => bookCache.Ready && Profile.BookItems.SelectMany(Profile.BookExpansions).Distinct()
        .All(scope => scope == 6 || scopedBookCaches.TryGetValue(scope, out var cache) && cache.Ready);
    private bool bookUpdating => bookCache.Updating || scopedBookCaches.Values.Any(cache => cache.Updating);
    private string bookError => bookCache.Error.Length > 0 ? bookCache.Error
        : scopedBookCaches.Values.Select(cache => cache.Error).FirstOrDefault(message => message.Length > 0) ?? "";
    private bool bookViewVerified;
    private Dictionary<uint, Stock> stock = [];
    private Dictionary<uint, int> books => selectedBookNeeds ?? bookCache.Needs;
    private Dictionary<uint, int> supplies = [];
    private string snapshot = "{\"Available\":false,\"Ready\":false,\"Error\":\"준비 중\"}";
    private string error = "";
    private string lastReadiness = "";
    internal static TimeSpan RefreshInterval(bool pending) => TimeSpan.FromMilliseconds(pending ? 250 : 5000);
    private DateTime nextRefresh;
    // Collect on the framework thread, then publish the complete set for one character.
    private readonly GardenBookSweep bookSweep = new();
    private ulong owner;
    private Visit? visit;
    private HarvestProfile? displayProfile;
    private readonly Queue<string> pendingConfiguration = [];
    private bool disposed;
    public bool Active => visit != null;
    private HarvestProfile Profile => config.Profiles.GetValueOrDefault(owner)
        ?? (config.Profiles[owner] = new());
    internal Catalog? Catalog => catalog;
    internal HarvestProfile? CurrentProfile => displayProfile;
    internal string Error => error;
    private uint SoilFor(GardenCell cell) => GardenPolicy.Soil(Profile.BatchFor(cell)?.Cross == true, Profile.Soil);
    private Dictionary<uint, int> BatchMaterials(GardenBatch batch)
    {
        var result = new Dictionary<uint, int>();
        if (batch.Clearing) return result;
        var cells = Profile.Cells.Where(c => batch.Cells.Contains(c.Id) && c.Enabled).ToArray();
        foreach (var group in cells.Where(c => !c.Owned).GroupBy(c => c.Seed)) result[group.Key] = group.Count();
        var soil = cells.Count(c => !c.Owned);
        if (soil > 0) result[GardenPolicy.Soil(batch.Cross, Profile.Soil)] = soil;
        if (!batch.Cross && catalog!.Crops.TryGetValue(batch.Output, out var crop))
            foreach (var group in cells.SelectMany(c => crop.Colors.Skip(c.ColorStep)).GroupBy(i => i)) result[group.Key] = group.Count();
        return result;
    }
    private bool CanPlant(GardenCell cell) => catalog != null && GardenPolicy.CanPlant(Profile, cell, catalog.Crops, catalog.SeedCrops, DateTime.UtcNow);
    private bool CanVisit(GardenCell cell) => Profile.BatchFor(cell) is { } batch
        && (cell.Owned || cell.Pending.Length > 0 || CanPlant(cell))
        && GardenPolicy.CanVisit(cell, stock, SoilFor(cell), GardenPolicy.PlantingReady(batch, Profile.Cells, stock, SoilFor(cell), BatchMaterials(batch)));

    private Func<uint[], Dictionary<uint, int>?> inventoryReader = ReadBagCounts;

    public HarvestService(Configuration config, AutoGardensWork garden)
    {
        this.config = config; this.garden = garden;
        routeRecordingGate = Svc.PluginInterface.GetIpcProvider<string, string>("Harvest.RouteRecordingV1");
        routeRecordingGate.RegisterFunc(request => IpcFrameworkGate.Get("Harvest.RouteRecordingV1", () => RouteRecordingRequest(request), "{\"Accepted\":false}"));
        gardenViewGate = Svc.PluginInterface.GetIpcProvider<string>("Harvest.GetGardenView");
        gardenViewGate.RegisterFunc(() => gardenView);
        gardenScopeGate = Svc.PluginInterface.GetIpcProvider<uint, string>("Harvest.GetGardenViewForExpansion");
        gardenScopeGate.RegisterFunc(scope => {
            if (scope > 6) return "{\"Available\":false,\"Error\":\"잘못된 확장팩\"}";
            requestedGardenExpansion = (int)scope;
            return scope == 6 ? gardenView : gardenScopes.GetValueOrDefault(scope)
                ?? "{\"Available\":false,\"Error\":\"확장팩 제작 재료 계산 중\"}";
        });
        snapshotGate = Svc.PluginInterface.GetIpcProvider<string>("Harvest.GetWorkflowSnapshot");
        snapshotGate.RegisterFunc(() => snapshot);
        configGate = Svc.PluginInterface.GetIpcProvider<string, string>("Harvest.Configure");
        configGate.RegisterFunc(request => IpcFrameworkGate.Get("Harvest.Configure", () => Configure(request), "{\"Accepted\":false}"));
        soilVentureGate = Svc.PluginInterface.GetIpcProvider<string>("Harvest.GetSoilVentureTargets");
        soilVentureGate.RegisterFunc(() => soilVentureSnapshot);
        protectionGate = Svc.PluginInterface.GetIpcProvider<string>("Harvest.GetProtection");
        protectionGate.RegisterFunc(() => IpcFrameworkGate.Get("Harvest.GetProtection",
            () => JsonSerializer.Serialize(new { Available = owner != 0, Character = owner, Items = Reservations() }), "{\"Available\":false}"));
        endpoint = new(Svc.PluginInterface, Svc.Framework, "Harvest", CreateWork,
            () => Svc.ClientState.IsLoggedIn && !AutomationGate.TryGetBusyReason(out _, allowMountedTravel: true),
            () => visit != null || garden.IsBusy || routeRecording?.Active == true, () => Svc.ClientState.IsLoggedIn ? Svc.PlayerState.ContentId : 0);
        Svc.Framework.Update += Refresh;
        Svc.Framework.Update += RecordRoutePosition;
    }

    private Dictionary<uint, int> Reservations()
    {
        var result = new Dictionary<uint, int>(supplies);
        if (owner == 0) return result;
        foreach (var goal in Profile.Goals.Where(g => g.Enabled && g.Quantity > 0))
            result[goal.Item] = Math.Max(result.GetValueOrDefault(goal.Item), goal.Quantity);
        foreach (var item in Profile.BookItems)
            result[item] = Math.Max(result.GetValueOrDefault(item), books.GetValueOrDefault(item));
        foreach (var cell in Profile.Cells.Where(c => c.Owned && c.Seed != 0)) result[cell.Seed] = Math.Max(1, result.GetValueOrDefault(cell.Seed));
        if (catalog != null)
            foreach (var (soil, count) in GardenPolicy.SoilReserve(Profile, catalog.Crops, catalog.SeedCrops, catalog.Crosses, stock))
                result[soil] = Math.Max(result.GetValueOrDefault(soil), count);
        return result.Where(p => p.Value > 0).ToDictionary();
    }

    private void Refresh(IFramework framework)
    {
        if (DateTime.UtcNow < nextRefresh) return;
        nextRefresh = DateTime.UtcNow + RefreshInterval(false);
        if (visit != null) {
            bookSweep.Reset();
            if (Svc.PlayerState.ContentId == owner) PublishGardenView();
            return;
        }
        var current = Svc.ClientState.IsLoggedIn ? Svc.PlayerState.ContentId : 0;
        if (current != owner) { owner = current; inventoryChecked = false; stock.Clear(); bookCache.Reset(); scopedBookCaches.Clear(); selectedBookNeeds = null; supplies.Clear(); gardenScopes.Clear(); soilVentureSnapshot = "{\"Available\":false}"; gardenView = "{\"Available\":false}"; }
        if (owner == 0) { bookSweep.Reset(); stockKnown = false; displayProfile = null; snapshot = "{\"Available\":false,\"Ready\":false,\"Error\":\"접속 대기\"}"; return; }
        while (pendingConfiguration.TryDequeue(out var change)) Configure(change);
        bool collecting = false, freshStock = false;
        try {
            if (PluginPresence.IsLoaded("TCToolbox") != false) throw new InvalidOperationException("이전 TCToolbox 비활성화 확인 필요");
            if (catalog == null) {
                catalog = new();
                GardenCropData.AddKnownCropNames(catalog.Crops.Values.Select(c => (c.Name, c.Item)));
            }
            if (!CollectBookScope()) {
                collecting = true;
                nextRefresh = DateTime.MinValue;
                return;
            }
            stockKnown = false;
            RefreshGardenBooks();
            var ids = catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Concat(new uint[] { Profile.Soil, 16026, 14011, 14012, 14013 })
                .Where(i => i > 0).Distinct().ToArray();
            using var response = JsonDocument.Parse(Svc.PluginInterface.GetIpcSubscriber<string, string>("GatherBuddyReborn.GetHarvestStock")
                .InvokeFunc(JsonSerializer.Serialize(ids)));
            var root = response.RootElement;
            if (!root.GetProperty("Available").GetBoolean() && root.TryGetProperty("Pending", out var pending) && pending.GetBoolean())
                nextRefresh = DateTime.UtcNow + RefreshInterval(true);
            if (!root.GetProperty("Available").GetBoolean() || root.GetProperty("Character").GetUInt64() != owner
                || !root.GetProperty("RetainerKnown").GetBoolean()) throw new InvalidOperationException("가방·집사 재고 확인 대기");
            var previousStock = stock;
            stock = root.GetProperty("Rows").EnumerateArray().ToDictionary(r => r.GetProperty("ItemId").GetUInt32(),
                r => new Stock(r.GetProperty("Inventory").GetInt32(), r.GetProperty("Retainer").GetInt32(), r.GetProperty("CanProcure").GetBoolean(),
                    !r.TryGetProperty("CanProcureNow", out var due) || due.GetBoolean()));
            if (GardenPolicy.InvalidateHarvestedCells(Profile.Cells, previousStock, stock)) config.Save();
            if (!inventoryChecked) {
                var previousBag = stock.ToDictionary(p => p.Key, p => p.Value.Inventory);
                ReadVisitInventory(ids); inventoryChecked = true;
                Svc.Log.Information($"Garden native inventory checked: rows={ids.Length}, differences={stock.Count(p => p.Value.Inventory != previousBag[p.Key])}");
            }
            stockKnown = freshStock = true;
            if (Profile.BookItems.Count > 0 && !bookReady) {
                if (bookUpdating) throw new GardenBookPendingException();
                throw new InvalidOperationException(bookError);
            }
            error = "";
            Replan();
            var visitReady = HasDueVisit() && Profile.Cells.Any(c => Profile.BatchFor(c) != null && c.Enabled && Due(c)
                && ReadyForVisit(c));
            var withdrawals = supplies.Where(p => stock.GetValueOrDefault(p.Key) is { Retainer: > 0 } s && s.Inventory < p.Value)
                .Select(p => new { ItemId = p.Key, TargetCount = p.Value }).ToArray();
            var supply = supplies.Where(p => !visitReady && stock.GetValueOrDefault(p.Key) is { CanProcureNow: true } s && s.Inventory < p.Value)
                .OrderByDescending(p => stock[p.Key].Retainer > 0)
                .Select(p => new { ItemId = p.Key, TargetCount = p.Value, Withdrawals = withdrawals }).FirstOrDefault();
            var ready = Profile.RetryAt <= DateTime.UtcNow && (supply != null || visitReady);
            snapshot = JsonSerializer.Serialize(new { Available = true, Character = owner, Ready = ready,
                Supply = supply,
                Detail = Profile.Batch == null ? (error.Length > 0 ? error : "필요한 재배 없음")
                    : !ready && supplies.Any(p => stock.GetValueOrDefault(p.Key)?.Inventory < p.Value)
                        ? "재배 재료 확보 조건 대기 · 다른 작업 진행" : (!ready ? "성장·다음 관리 대기 · " : "") + string.Join(" · ", Profile.ActiveBatches.Select(b => catalog.Names.GetValueOrDefault(b.Output) ?? "재배").Distinct()),
                Goals = Profile.Goals, BookItems = Profile.BookItems, Growable = catalog.Crops.Keys,
                Searchable = catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct() });
        } catch (Exception ex) {
            stockKnown = freshStock;
            if (ex is GardenBookPendingException) nextRefresh = DateTime.UtcNow + RefreshInterval(true);
            error = ex is GardenBookPendingException ? "" : ex.Message;
            var care = catalog != null && Profile.Batch != null && HasCareDue()
                && (Profile.Goals.Any(g => g.Enabled && !g.Complete && g.Quantity > 0) || Profile.BookItems.Count > 0);
            snapshot = JsonSerializer.Serialize(new { Available = care, Character = owner, Ready = care,
                Detail = ex is GardenBookPendingException ? "제작수첩 재료 갱신 완료 확인 중" : error,
                CareOnly = care, Supply = (object?)null, Error = error, Growable = catalog?.Crops.Keys,
                Searchable = catalog?.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct() });
        } finally {
            if (!collecting) {
                bookSweep.Reset();
                PublishReadiness();
            }
        }
    }

    private void PublishReadiness()
    {
        PublishGardenView();
        using var state = JsonDocument.Parse(snapshot);
        var r = state.RootElement;
        var readiness = $"available={r.GetProperty("Available")}, ready={r.GetProperty("Ready")}, bookReady={bookReady}, bookUpdating={bookUpdating}, bookSnapshot={bookCache.HasSnapshot}, stockKnown={stockKnown}, error={error}, batches={string.Join(',', Profile.ActiveBatches.Select(b => b.Output))}, soilReserve={soilVentureSnapshot}, supplies={string.Join(',', supplies.Select(p => $"{p.Key}:{p.Value}[bag={stock.GetValueOrDefault(p.Key)?.Inventory},retainer={stock.GetValueOrDefault(p.Key)?.Retainer},now={stock.GetValueOrDefault(p.Key)?.CanProcureNow}]"))}";
        if ((!bookUpdating || error.Length > 0) && readiness != lastReadiness) { lastReadiness = readiness; Svc.Log.Information("Garden readiness: " + readiness); }
    }

    private static string ReadBookNeeds(uint scope)
    {
        var needs = Svc.PluginInterface.GetIpcSubscriber<uint, string>("GatherBuddyReborn.GetHarvestBookNeeds");
        if (needs.HasFunction) return needs.InvokeFunc(scope);
        return scope == 6 ? Svc.PluginInterface.GetIpcSubscriber<string>("GatherBuddyReborn.GetHarvestBookMaterials").InvokeFunc()
            : Svc.PluginInterface.GetIpcSubscriber<uint, string>("GatherBuddyReborn.GetWorkflowBookMaterials").InvokeFunc(scope);
    }

    private bool CollectBookScope()
    {
        var scopes = Profile.BookItems.SelectMany(Profile.BookExpansions).Append(6u).Distinct().Order().ToArray();
        return bookSweep.ReadNext(owner, scopes, ReadBookNeeds);
    }

    private void RefreshGardenBooks()
    {
        try {
            bookCache.Update(bookSweep.Results[6], owner);
            var requiredScopes = Profile.BookItems.SelectMany(Profile.BookExpansions).Where(scope => scope != 6).Distinct().ToArray();
            foreach (var stale in scopedBookCaches.Keys.Except(requiredScopes).ToArray()) scopedBookCaches.Remove(stale);
            foreach (var scope in requiredScopes) {
                if (!scopedBookCaches.TryGetValue(scope, out var cache)) scopedBookCaches[scope] = cache = new();
                cache.Update(bookSweep.Results[scope], owner, scope);
            }
            if (bookReady) {
                var caches = new Dictionary<uint, GardenBookCache>(scopedBookCaches) { [6] = bookCache };
                selectedBookNeeds = GardenBookCache.SelectedNeeds(Profile, caches);
            }
            if (bookReady && !bookViewVerified) {
                bookViewVerified = true;
                Svc.Log.Information($"Garden book materials ready: materials={books.Count}, gardenItems={books.Count(p => p.Value > 0 && (catalog!.Crops.ContainsKey(p.Key) || catalog.SeedCrops.ContainsKey(p.Key)))}");
            }
        } catch (Exception ex) { bookCache.Fail(ex.Message); }
    }

    private void PublishGardenView()
    {
        var soils = owner != 0 && catalog != null
            ? GardenPolicy.SoilReserve(Profile, catalog.Crops, catalog.SeedCrops, catalog.Crosses, stock) : new Dictionary<uint,int>();
        soilVentureSnapshot = JsonSerializer.Serialize(new { Available = owner != 0 && catalog != null && stockKnown, Character = owner,
            Targets = soils.Select(p => new { ItemId = p.Key, Name = catalog!.Names.GetValueOrDefault(p.Key) ?? p.Key.ToString(), TargetCount = p.Value }).ToArray() });
        displayProfile = JsonSerializer.Deserialize<HarvestProfile>(JsonSerializer.Serialize(Profile));
        if (catalog == null || owner == 0) return;
        gardenView = JsonSerializer.Serialize(new { Available = true, Character = owner, Expansion = 6, Error = error,
            BookReady = bookReady, BookError = bookError, BookHasSnapshot = bookCache.HasSnapshot,
            Items = GardenView.Items(Profile, catalog.Crops, catalog.SeedCrops, stock,
                bookCache.Needs.Concat(books).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value), stockKnown),
            Cells = Profile.Cells, Batch = Profile.Batch, Batches = Profile.ActiveBatches, Routes = Profile.WalkRoutes.Values, Registration = RegistrationTarget() });
        var scope = (uint)requestedGardenExpansion;
        if (scope >= 6) return;
        try {
            using var doc = JsonDocument.Parse(ReadBookNeeds(scope));
            var b = doc.RootElement;
            if (!b.GetProperty("Available").GetBoolean() || b.GetProperty("Character").GetUInt64() != owner
                || b.GetProperty("Expansion").GetUInt32() != scope
                || b.GetProperty("Error").GetString() != "") throw new InvalidOperationException("확장팩 제작 재료 계산 중");
            var scoped = b.GetProperty("Rows").EnumerateArray().ToDictionary(r => r.GetProperty("ItemId").GetUInt32(), r => r.GetProperty("Needed").GetInt32());
            gardenScopes[scope] = JsonSerializer.Serialize(new { Available = true, Character = owner, Expansion = scope, Error = error,
                BookReady = !b.GetProperty("Pending").GetBoolean(), BookError = "", BookHasSnapshot = true,
                Items = GardenView.Items(Profile, catalog.Crops, catalog.SeedCrops, stock, scoped, stockKnown, scope),
                Cells = Profile.Cells, Batch = Profile.Batch, Batches = Profile.ActiveBatches, Routes = Profile.WalkRoutes.Values, Registration = RegistrationTarget() });
        } catch (Exception ex) { gardenScopes[scope] = JsonSerializer.Serialize(new { Available = false, Error = ex.Message }); }
    }

    private void Replan()
    {
        supplies = [];
        var dirty = false;
        foreach (var goal in Profile.Goals.Where(g => g.Enabled && !g.Complete && g.Quantity > 0))
            if (stock.GetValueOrDefault(goal.Item)?.Total >= goal.Quantity) { goal.Complete = true; dirty = true; }
        var targets = Profile.Goals.Where(g => g.Enabled && !g.Complete && g.Quantity > 0)
            .GroupBy(g => g.Item).ToDictionary(g => g.Key, g => g.Max(x => x.Quantity));
        foreach (var id in Profile.BookItems) if (books.GetValueOrDefault(id) > 0)
            targets[id] = Math.Max(targets.GetValueOrDefault(id), books[id]);
        foreach (var id in targets.Keys.ToArray()) if (stock.GetValueOrDefault(id)?.Total >= targets[id]) targets.Remove(id);
        var now = DateTime.UtcNow;
        var planted = Profile.ActiveBatches.Where(b => b.Cross && !b.Clearing && b.Cells.Count == 2
                && Profile.Cells.Count(c => b.Cells.Contains(c.Id) && c.Enabled && c.Owned && c.Pending.Length == 0
                    && c.State is "growing" or "mature") == 2)
            .DistinctBy(b => b.Output).ToDictionary(b => b.Output, b => new Cross(b.Output, b.SeedA, b.SeedB));
        var plans = targets.Select(t => {
            var isSeed = catalog!.SeedCrops.ContainsKey(t.Key);
            var product = isSeed ? null : catalog.Crops.GetValueOrDefault(t.Key);
            var seedCount = t.Value;
            if (product != null) {
                var started = Profile.Cells.Count(c => c.Enabled && (c.Owned || c.Pending == "Plant")
                    && Profile.BatchFor(c) is { Cross: false } b && b.Goal == t.Key);
                var capacity = Profile.Cells.Count(c => c.Enabled && (!product.Flower || c.Kind == "pot"));
                seedCount = Math.Clamp(GardenPolicy.PlantingCells(t.Value, stock.GetValueOrDefault(t.Key)?.Total ?? 0, product.Yield)
                    - started, 0, Math.Max(1, capacity - started));
            }
            var plan = seedCount == 0 ? new SeedPlan([], new Dictionary<uint, int>(), "") : isSeed || product != null
                ? SeedPlanner.Build(product?.Seed ?? t.Key, seedCount, stock, catalog.Crosses, planted) : null;
            return (Item: t.Key, Quantity: t.Value, IsSeed: isSeed, Product: product, Plan: plan);
        }).Where(p => p.Plan != null).OrderByDescending(p => p.Plan!.Stages.Count > 0)
            .ThenByDescending(p => p.Plan!.Stages.Sum(s => Math.Max(
                catalog!.Crops.GetValueOrDefault(catalog.SeedCrops.GetValueOrDefault(s.A))?.Hours ?? 0,
                catalog.Crops.GetValueOrDefault(catalog.SeedCrops.GetValueOrDefault(s.B))?.Hours ?? 0))).ToArray();
        var retained = new List<GardenBatch>();
        foreach (var batch in Profile.ActiveBatches) {
            dirty |= GardenPolicy.AlignCrossAnchor(batch, Profile.Cells, catalog!.Crops, catalog.SeedCrops);
            if (!batch.Cross && targets.TryGetValue(batch.Goal, out var required))
                dirty |= GardenPolicy.LimitOrdinaryBatch(batch, Profile.Cells, required, stock.GetValueOrDefault(batch.Output)?.Total ?? 0, catalog!.Crops.GetValueOrDefault(batch.Output)?.Yield ?? 1);
            if (!batch.Cross) {
                var remaining = Profile.Cells.Where(c => batch.Cells.Contains(c.Id) && (c.Owned || c.Pending.Length > 0 || c.Enabled && (c.Crop == 0 || c.Observed == DateTime.MinValue) && CanPlant(c))).Select(c => c.Id).ToList();
                dirty |= !batch.Cells.SequenceEqual(remaining); batch.Cells = remaining;
                if (remaining.Count == 0) continue;
            }
            var clearing = batch.Clearing;
            batch.Clearing |= !targets.ContainsKey(batch.Goal) || stock.GetValueOrDefault(batch.Output)?.Total >= batch.Required;
            batch.Clearing |= batch.Cross && catalog!.SeedCrops.ContainsKey(batch.Output)
                && plans.Any(p => p.Item == batch.Goal && p.Product != null && p.Plan!.Error.Length == 0 && p.Plan.Stages.Count == 0);
            var cells = Profile.Cells.Where(c => batch.Cells.Contains(c.Id)).ToArray();
            batch.Clearing |= batch.Cross && (cells.Length != batch.Cells.Count || cells.Any(c => !c.Enabled));
            // Rebuild the ancestor stage after an irreplaceable parent seed is exhausted.
            batch.Clearing |= cells.Any(c => !c.Owned && c.State == "empty"
                && stock.TryGetValue(c.Seed, out var seedStock) && seedStock.Total == 0 && !seedStock.CanProcure);
            dirty |= clearing != batch.Clearing;
            if (batch.Clearing) {
                var occupied = cells.Where(c => c.Owned || c.Pending.Length > 0).ToArray();
                dirty |= occupied.Length != batch.Cells.Count;
                batch.Cells = occupied.Select(c => c.Id).ToList(); cells = occupied;
            }
            if (batch.Clearing && cells.All(c => !c.Owned && c.Pending.Length == 0 && c.State is "empty" or "unscanned")) dirty = true;
            else retained.Add(batch);
        }
        Profile.SetBatches(retained);
        var forecast = GardenLookahead.Build(Profile, catalog!.Crops, catalog.SeedCrops, plans.Select(p => p.Plan!), now);
        foreach (var batch in retained.Where(b => !b.Cross)) {
            var hours = catalog.Crops.GetValueOrDefault(batch.Output)?.Hours ?? 0;
            var remaining = batch.Cells.Where(id => Profile.Cells.Any(c => c.Id == id && (c.Owned || c.Pending.Length > 0
                || GardenLookahead.Fits(forecast, c, hours, now)))).ToList();
            dirty |= !batch.Cells.SequenceEqual(remaining); batch.Cells = remaining;
        }
        retained.RemoveAll(b => b.Cells.Count == 0);
        Profile.SetBatches(retained);
        var reserved = retained.SelectMany(b => b.Cells).ToHashSet();
        var flowerDemand = targets.Keys.Any(id => catalog!.Crops.GetValueOrDefault(id)?.Flower == true);
        var waitingForPair = false;
        int UnreservedSeedCount(uint seed) => (stock.GetValueOrDefault(seed)?.Total ?? 0)
            - Profile.Cells.Count(c => reserved.Contains(c.Id) && !c.Owned && c.Pending.Length == 0 && c.Seed == seed);
        bool Available(GardenCell cell, bool crossing, double hours = 0) => cell.Enabled && !cell.Owned && cell.Pending.Length == 0 && (cell.Crop == 0 || cell.Observed == DateTime.MinValue) && !reserved.Contains(cell.Id)
            && (crossing || GardenLookahead.Fits(forecast, cell, hours, now))
            && (cell.Kind != "plot" || !retained.Where(b => crossing || b.Cross && !b.Clearing)
                .Any(b => (crossing || !GardenPolicy.FitsGrowthWindow(b, Profile.Cells, catalog!.Crops, catalog.SeedCrops, hours, DateTime.UtcNow))
                    && b.Cells.Any(id => Profile.Cells.Any(c => c.Id == id && GardenPolicy.AdjacentSlots(cell, c, Profile.Cells)))));

        foreach (var (item, quantity, isSeed, product, seedPlan) in plans) {
            var growing = retained.FirstOrDefault(b => b.Goal == item && !b.Clearing);
            if (growing?.Cross == true) continue;
            var seed = product?.Seed ?? item;
            var plan = seedPlan!;
            if (plan.Error.Length > 0) { error = plan.Error; continue; }
            var stage = plan.Stages.FirstOrDefault(s => stock.GetValueOrDefault(s.Output)?.Total < s.Required || !stock.ContainsKey(s.Output));
            GardenCell[] selected;
            GardenBatch next;
            if (stage != null) {
                if (retained.Any(b => b.Output == stage.Output && !b.Clearing)) continue;
                if (new[] { stage.A, stage.B }.GroupBy(id => id).Any(g => stock.GetValueOrDefault(g.Key)?.CanProcure != true
                    && UnreservedSeedCount(g.Key) < g.Count())) continue;
                var allocation = forecast.FirstOrDefault(r => r.Output == stage.Output && r.PlantAt <= now);
                selected = allocation?.Pair ?? [];
                if (selected.Any(c => !Available(c, true)) || selected.Any(c => !GardenPolicy.CrossPlantingClear(
                        new GardenBatch { Cross = true, Cells = selected.Select(x => x.Id).ToList() }, c, Profile.Cells))) selected = [];
                if (selected.Length != 2) {
                    waitingForPair = true;
                    if (!GardenPolicy.CrossPairs(Profile.Cells).Any()) error = "교배할 같은 텃밭의 인접한 재배 칸 2개를 등록·선택해야 합니다.";
                    continue;
                }
                next = new() { Goal = item, Output = stage.Output, Required = stage.Required,
                    SeedA = stage.A, SeedB = stage.B, Cross = true, Cells = selected.Select(c => c.Id).ToList() };
                var aHours = catalog.Crops.GetValueOrDefault(catalog.SeedCrops.GetValueOrDefault(stage.A))?.Hours ?? 0;
                var bHours = catalog.Crops.GetValueOrDefault(catalog.SeedCrops.GetValueOrDefault(stage.B))?.Hours ?? 0;
                if (aHours > 0 && bHours > 0 && aHours > bHours) (next.SeedA, next.SeedB) = (next.SeedB, next.SeedA);
            } else if (isSeed) {
                foreach (var (id, amount) in plan.Supplies) supplies[id] = Math.Max(supplies.GetValueOrDefault(id), amount);
                continue;
            }
            else {
                var slots = GardenPolicy.PlantingCells(quantity, stock.GetValueOrDefault(item)?.Total ?? 0, product!.Yield)
                    - Profile.Cells.Count(c => c.Enabled && growing?.Cells.Contains(c.Id) == true);
                if (slots <= 0) continue;
                var seeds = stock.GetValueOrDefault(seed) is { CanProcure: false }
                    ? Math.Max(0, UnreservedSeedCount(seed)) : 24;
                selected = Profile.Cells.Where(c => Available(c, false, product.Hours) && (product.Flower ? c.Kind == "pot" : c.Kind != "pot" || !flowerDemand))
                    .OrderBy(c => c.Kind == "pot" ? 0 : 1).Take(Math.Min(slots, seeds)).ToArray();
                if (selected.Length == 0) {
                    if (!Profile.Cells.Any(c => c.Enabled && (!product!.Flower || c.Kind == "pot")))
                        error = product!.Flower ? "꽃을 재배할 실내 화분 칸을 등록·선택해야 합니다." : "재배할 칸을 등록·선택해야 합니다.";
                    continue;
                }
                next = growing ?? new() { Goal = item, Output = item, Required = quantity, SeedA = seed, Cells = [] };
                next.Cells.AddRange(selected.Select(c => c.Id));
            }
            for (var i = 0; i < selected.Length; i++) selected[i].Seed = i % 2 == 1 && next.Cross ? next.SeedB : next.SeedA;
            error = "";
            if (!retained.Contains(next)) retained.Add(next);
            reserved.UnionWith(next.Cells);
            Profile.SetBatches(retained); dirty = true;
        }
        if (waitingForPair && retained.Count == 0 && error.Length == 0) error = "??? ?? ?? ??? ???? ?";
        var ordered = retained.OrderByDescending(b => b.Cross && !b.Clearing).ToArray();
        dirty |= !Profile.ActiveBatches.SequenceEqual(ordered);
        Profile.SetBatches(ordered);
        foreach (var batch in Profile.ActiveBatches) ReserveBatch(batch);
        if (dirty) config.Save();
    }

    private void ReserveBatch(GardenBatch batch)
    {
        foreach (var (item, count) in BatchMaterials(batch)) supplies[item] = supplies.GetValueOrDefault(item) + count;
    }

    private bool HasDueVisit()
    {
        if (!Profile.ActiveBatches.Any()) return false;
        var planted = Profile.Cells.Any(c => Profile.BatchFor(c) != null && c.Enabled && (c.Owned || c.Pending.Length > 0));
        if (!planted && !Profile.Goals.Any(g => g.Enabled && !g.Complete && g.Quantity > 0)
            && Profile.BookItems.All(i => books.GetValueOrDefault(i) <= stock.GetValueOrDefault(i)?.Total)) return false;
        return Profile.Cells.Any(c => Profile.BatchFor(c) != null && c.Enabled && Due(c));
    }
    private bool HasCareDue() => Profile.Cells.Any(c => c.Enabled && c.Owned && c.State is "growing" or "mature" && c.RetryAt <= DateTime.UtcNow
        && (c.Pending == "Tend" || c.Tended.AddHours(12) <= DateTime.UtcNow));
    private bool CanHarvest(GardenCell cell) => cell.Owned && Profile.BatchFor(cell) is { } batch && !GardenPolicy.IsAnchor(batch, cell)
        && (!batch.Cross || batch.Clearing || Profile.Cells.Any(c => batch.Cells.Contains(c.Id) && c.Id != cell.Id && c.Owned && c.State != "empty"));
    private bool MaturityDue(GardenCell c) => catalog != null && catalog.SeedCrops.TryGetValue(c.Seed, out var item)
        && catalog.Crops.TryGetValue(item, out var crop) && crop.Hours > 0 && c.Planted != DateTime.MinValue
        && c.Planted.AddHours(crop.Hours) <= DateTime.UtcNow && c.Observed.AddMinutes(30) <= DateTime.UtcNow;
    private bool ReadyForVisit(GardenCell c)
    {
        if (!CanVisit(c)) return false;
        if (c.Pending.Length > 0 || !c.Owned || c.Observed == DateTime.MinValue || c.State == "mature"
            || c.Tended.AddHours(12) <= DateTime.UtcNow || MaturityDue(c)) return true;
        return c.ColorStep < Colors(c).Length && stock.GetValueOrDefault(Colors(c)[c.ColorStep])?.Inventory > 0;
    }
    private bool Due(GardenCell c) => c.RetryAt <= DateTime.UtcNow
        && !(c.Pending.Length == 0 && c.Observed != DateTime.MinValue && c.State == "withered")
        && !(c.Observed != DateTime.MinValue && c.State == "mature" && !CanHarvest(c)
            && c.Pending.Length == 0 && c.Tended.AddHours(12) > DateTime.UtcNow)
        && (c.Pending.Length > 0 || c.Observed == DateTime.MinValue || c.Owned && c.State == "mature"
            || c.State == "empty" && Profile.BatchFor(c)?.Clearing == false
            || c.Owned && (c.Tended.AddHours(12) <= DateTime.UtcNow || MaturityDue(c)
                || c.ColorStep < Colors(c).Length && c.ColorFed.AddHours(1) <= DateTime.UtcNow));

    public void Dispose()
    {
        disposed = true;
        routeRecording?.Cancel("플러그인 재로드로 기록 취소");
        Svc.Framework.Update -= RecordRoutePosition;
        Svc.Framework.Update -= Refresh;
        endpoint.Dispose();
        snapshotGate.UnregisterFunc(); configGate.UnregisterFunc(); protectionGate.UnregisterFunc(); soilVentureGate.UnregisterFunc();
        gardenViewGate.UnregisterFunc();
        gardenScopeGate.UnregisterFunc();
        routeRecordingGate.UnregisterFunc();
    }
}
