using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Harvest;
using Harvest.Core;
using Harvest.Planning;
using System.Collections.Generic;
using System.Numerics;

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(Environment.GetEnvironmentVariable("DALAMUD_HOME") ?? "", name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)) : null;
};
Run();

static void Run()
{
var passed = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
    passed++;
}
string[] korean = ["밭에 심은 작물이 없습니다.", "취소", "이랑을 만들고 씨 뿌리기", "비료 주기", "관리하기", "폐기하기", "수확하기", " 바짝 말랐습니다…….", " 무럭무럭 자라고 있습니다.", " 기운이 없어 보입니다…….", " 충분히 무르익었습니다."];
var read = GardenTextData.Read(id => korean[id]);
Check(read.SequenceEqual(korean), "Korean menu and leading status spaces must survive loading.");
foreach (var missing in new[] { 0u, 2u, 6u, 10u })
{
    try { GardenTextData.Read(id => id == missing ? null : korean[id]); throw new InvalidDataException("Incomplete menu accepted."); }
    catch (InvalidOperationException) { passed++; }
}
try { GardenTextData.Read(id => id == 4 ? " " : korean[id]); throw new InvalidDataException("Blank menu accepted."); }
catch (InvalidOperationException) { passed++; }
var fresh = new Configuration();
Check(!fresh.GardensWork.AutoLoopEnabled && fresh.EnabledModules.Count == 0, "Install must not start automatic gardening.");
var restored = JsonConvert.DeserializeObject<Configuration>("{\"Version\":6,\"EnabledModules\":[\"AutoGardensWork\"],\"GardensWork\":{\"SeedItemId\":14007,\"AutoLoopEnabled\":true},\"UnrelatedModule\":{\"Keep\":42}}")!;
var roundTrip = JObject.Parse(JsonConvert.SerializeObject(restored));
Check(restored.GardensWork.SeedItemId == 14007 && restored.GardensWork.AutoLoopEnabled, "Existing gardening settings must survive.");
Check((int?)roundTrip["UnrelatedModule"]?["Keep"] == 42, "Omitted toolbox settings must survive round trip.");
GardenDecision Decide(PatchState state, uint crop = 7770) => GardenDecisionMaker.Decide(state, crop, 7770,
    MatureCropPolicy.HarvestAndReplant, MatureCropPolicy.Skip, FertilizePolicy.None,
    WitheredPolicy.Leave, true, true, true, false);
Check(Decide(PatchState.Unknown).Action == null, "Unknown cells must stay untouched.");
Check(Decide(PatchState.Ripe, 7774).Action == null, "Other ripe crops must stay protected.");
Check(Decide(PatchState.Ripe).Action == AutoGardenAction.Harvest && Decide(PatchState.Ripe).Replant, "Target harvest/replant must remain available.");
Check(Decide(PatchState.NeedsCare).Action == AutoGardenAction.Tend, "Care must remain available.");
var roots = new Dictionary<uint, Stock> { [1] = new(0, 0, true), [2] = new(0, 0, true), [3] = new(0, 0, true) };
Cross[] chain = [new(10, 1, 2), new(20, 10, 3)];
var plan = SeedPlanner.Build(20, 4, roots, chain);
Check(plan.Error == "" && plan.Stages.Select(s => s.Output).SequenceEqual(new uint[] { 10, 20 }), "Ancestors must precede the requested hybrid.");
Check(plan.Stages.Last().Required == 4 && plan.Supplies.Count == 3, "Requested seed quantity and leaf supplies survive planning.");
Check(SeedPlanner.Build(1, 8, roots, chain).Supplies[1] == 8, "Direct procurement keeps the target stock count.");
roots[20] = new(0, 4, false);
Check(SeedPlanner.Build(20, 4, roots, chain).Stages.Count == 0, "Retainer stock avoids unnecessary ancestor production.");
Check(SeedPlanner.Build(20, 5, roots, chain).Stages.Last().Output == 20, "Insufficient stocked hybrids must still find their parents.");
Check(SeedPlanner.Build(30, 1, roots, [new(30, 31, 1), new(31, 30, 2)]).Error.Length > 0, "A seed cycle without a starting seed cannot run.");
var same = SeedPlanner.Build(40, 1, roots, [new(40, 1, 1)]);
Check(same.Supplies[1] == 2, "Same-seed crossbreeding needs two seeds.");
var insufficient = new Dictionary<uint, Stock> { [1] = new(1, 0, false) };
Check(SeedPlanner.Build(40, 1, insufficient, [new(40, 1, 1)]).Error.Length > 0, "One unavailable parent cannot fill two cells.");
var alternative = SeedPlanner.Build(40, 1, new Dictionary<uint, Stock> { [1] = new(1, 0, false), [2] = new(0, 0, true) },
    [new(40, 1, 1), new(40, 1, 2)]);
Check(alternative.Error == "" && alternative.Stages.Last().B == 2, "An unusable shortest pairing must not hide an available alternative.");
var a = new GardenCell { Id = "a", Seed = 1 }; var b = new GardenCell { Id = "b", Seed = 1 };
var batch = new GardenBatch { Cross = true, SeedA = 1, SeedB = 1, Cells = [a.Id, b.Id] };
Check(!GardenPolicy.IsAnchor(batch, a) && GardenPolicy.IsAnchor(batch, b), "Same-seed crosses must preserve only the anchor cell.");
batch.Clearing = true;
Check(!GardenPolicy.IsAnchor(batch, b), "A finished stage releases the anchor for harvest.");
var profile = new HarvestProfile { Goals = [new() { Item = 7770, Quantity = 3 }], Cells = [a, b], Batch = batch };
a.Owned = true; a.Pending = "Harvest";
profile.SelectCell(a.Id, false);
Check(!a.Enabled && b.Enabled && a.Owned && a.Pending == "Harvest", "Deselecting a cell preserves its crop and uncertain operation, and does not deselect another cell.");
profile.SelectCell(a.Id, true);
Check(a.Enabled && profile.Goals.Single().Quantity == 3 && profile.Batch == batch, "Reselecting a cell preserves quantity and the active plan.");
try { profile.SelectCell("other-character-cell", false); throw new InvalidDataException("Unknown cell accepted."); }
catch (ArgumentException) { passed++; }
var selectedProfile = JsonConvert.DeserializeObject<HarvestProfile>(JsonConvert.SerializeObject(profile))!;
Check(selectedProfile.Cells.All(c => c.Enabled) && selectedProfile.Cells[0].Pending == "Harvest", "Cell choices and pending actions survive reload together.");
var viewCrops = new Dictionary<uint, Crop> { [100] = new(100, 200, "꽃", true, 24, []) };
var viewSeeds = new Dictionary<uint, uint> { [200] = 100 };
var viewStock = new Dictionary<uint, Stock> { [100] = new(2, 3, false), [200] = new(1, 0, true) };
var viewBooks = new Dictionary<uint, int> { [100] = 8 };
var viewProfile = new HarvestProfile();
var viewRows = GardenView.Items(viewProfile, viewCrops, viewSeeds, viewStock, viewBooks, true);
Check(viewRows.Single(r => r.Item == 100) is { Seed: 200, Needed: 8, BookSelected: false, Inventory: 2, Retainer: 3, Known: true }, "Book candidates and seed icon IDs must be visible before any goal is selected.");
Check(viewRows.Single(r => r.Item == 200) is { Seed: 200, Flower: true, Hours: 24 }, "Seed rows retain their own icon and plant conditions.");
viewProfile.BookItems.Add(100); viewProfile.Goals.Add(new() { Item = 100, Quantity = 9 });
var combined = GardenView.Items(viewProfile, viewCrops, viewSeeds, viewStock, viewBooks, false).Single(r => r.Item == 100);
Check(combined is { Selected: true, BookSelected: true, Quantity: 9, Needed: 8, Known: false }, "Direct and book selections are independent and unknown stock must not appear confirmed.");
viewProfile.Goals[0].Complete = true;
viewProfile.SelectItems([100, 200, 200], true, false);
Check(viewProfile.Goals.Count == 2 && viewProfile.Goals.Single(g => g.Item == 100) is { Quantity: 9, Complete: true }, "Select-all preserves existing quantities and completion, and deduplicates items.");
Check(viewProfile.Goals.Single(g => g.Item == 200).Quantity == 1, "New bulk-selected items use a per-item default of one.");
viewProfile.SelectItems([200], false, false);
Check(viewProfile.Goals.Single().Item == 100 && viewProfile.BookItems.Contains(100), "Filtered deselection leaves hidden and book-linked items intact.");
viewProfile.SelectItems([100, 200], true, true);
Check(viewProfile.BookItems.SetEquals(new uint[] {100, 200}) && viewProfile.Goals.Single().Quantity == 9, "Bulk book selection cannot overwrite direct targets.");
viewProfile.SelectItems([200], false, true);
Check(viewProfile.BookItems.SetEquals(new uint[] {100}), "Bulk book deselection affects only the displayed IDs.");
var scopedView = GardenView.Items(viewProfile, viewCrops, viewSeeds, viewStock, new Dictionary<uint, int> { [100] = 3 }, true);
Check(scopedView.Single(r => r.Item == 100) is { Needed: 3, Quantity: 9, BookSelected: true } && viewBooks[100] == 8,
    "Expansion views change displayed demand without changing all-expansion demand or selections.");
var straight = Enumerable.Range(0, 101).Select(i => new Vector3(i / 10f, 0, 0)).ToArray();
var bookCache = new GardenBookCache();
string BookReply(ulong character, bool pending, int needed = 8, uint expansion = 6) => JsonConvert.SerializeObject(new {
    Available=true, Character=character, Expansion=expansion, Pending=pending, Error="", Rows=new[] {new {ItemId=100u, Needed=needed}}
});
bookCache.Update("{\"Available\":false,\"Pending\":true,\"Error\":\"전체 제작수첩 재료 계산 중\"}",10);
Check(bookCache.Updating && !bookCache.Ready && !bookCache.HasSnapshot && bookCache.Error == "", "Initial calculation is pending, not a global error.");
bookCache.Update(BookReply(10,false),10);
Check(bookCache.Ready && bookCache.HasSnapshot && bookCache.Needs[100] == 8, "A completed snapshot becomes available for rendering and execution.");
bookCache.Update(BookReply(10,true,99),10);
Check(bookCache.Updating && !bookCache.Ready && bookCache.HasSnapshot && bookCache.Needs[100] == 8 && bookCache.Error == "",
    "Background refresh retains the confirmed display snapshot without certifying stale data as ready to execute.");
bookCache.Update("{\"Available\":false,\"Pending\":true,\"Error\":\"전체 제작수첩 재료 계산 중\"}",10);
Check(bookCache.HasSnapshot && bookCache.Needs[100] == 8 && bookCache.Error == "", "Repeated polling cannot erase the list or turn pending into a warning.");
bookCache.Update("{\"Available\":false,\"Error\":\"IPC failure\"}",10);
Check(!bookCache.Ready && !bookCache.Updating && bookCache.Error == "IPC failure", "Real query errors remain visible and prevent starting work.");
bookCache.Update(BookReply(11,false,22),10);
Check(!bookCache.Ready && bookCache.Needs[100] == 8 && bookCache.Error.Length > 0, "Another character cannot replace the snapshot.");
bookCache.Update(BookReply(10,false,22,1),10);
Check(!bookCache.Ready && bookCache.Needs[100] == 8, "A scoped reply cannot replace the full-book execution cache.");
bookCache.Update(BookReply(10,false,3),10);
Check(bookCache.Ready && bookCache.Needs[100] == 3 && !bookCache.Updating && bookCache.Error == "", "The next completed refresh replaces the old quantity.");
bookCache.Update("{\"Available\":true,\"Character\":10,\"Expansion\":6,\"Pending\":false,\"Error\":\"\",\"Rows\":[]}",10);
Check(bookCache.Ready && bookCache.HasSnapshot && bookCache.Needs.Count == 0, "An empty completed crafting book is not a perpetual loading state.");
bookCache.Reset();
Check(!bookCache.Ready && !bookCache.HasSnapshot && bookCache.Needs.Count == 0, "Logout and character changes clear cached demand.");
Check(GardenRecordingPolicy.Blocker(GardenRecordingMotion.Ground) == null
    && GardenRecordingPolicy.Blocker(GardenRecordingMotion.Mounted) == null, "Passive coordinate capture must allow both walking and ground-mounted movement.");
Check(GardenRecordingPolicy.Blocker(GardenRecordingMotion.Mounted | GardenRecordingMotion.Flying) != null,
    "Ground mounting is distinct from an unreplayable flight path.");
Check(GardenRecordingPolicy.Blocker(GardenRecordingMotion.Diving) != null,
    "A diving path cannot silently become a ground route.");
Check(CornerRoute.Simplify(straight).Count == 2, "Straight walks store only start and destination.");
Vector3[] turn = [new(0,0,0), new(1,0,0), new(2,0,0), new(2,0,1), new(2,0,2)];
var corners = CornerRoute.Simplify(turn);
Check(corners.Count == 3 && corners[1].Position == new Vector3(2,0,0), "A bend is retained in the saved route.");
Check(CornerRoute.Simplify([new(0,0,0), new(2,0,0), new(2,2,0), new(4,2,0)]).Count == 4, "Height-changing corners cannot be flattened.");
Check(CornerRoute.Simplify([new(0,0,0), new(2,0,0), new(0,0,0)]).Count == 3, "Returning to the start retains the turnaround.");
var remaining = CornerRoute.Remaining(corners, new(1,0,0))!;
Check(remaining.Count == 3 && remaining[1] == new Vector3(2,0,0), "Resume from a segment retains the next corner instead of skipping to the end.");
Check(CornerRoute.Remaining(corners, new(50,0,50)) == null, "An unrelated position cannot join a saved path.");
Check(CornerRoute.Remaining(corners, new(2,0,2))!.Count == 0, "A player already at the destination does not replay the route.");
Check(CornerRoute.Remaining(corners, new(float.NaN,0,0)) == null, "Invalid positions cannot reach the movement IPC.");
var outdoor = new GardenRouteLocation(1, 339, 0, 0);
var destination = outdoor with { House = 123 };
ManualGardenRoute Recording(string token = "one") => new(token, 10, "밭1", "home", outdoor, Vector3.Zero, 0);
var recording = Recording();
Check(!recording.Touch("wrong", 10, 1000) && !recording.Touch("one", 11, 1000), "Other tokens and characters cannot renew a recording lease.");
Check(recording.Sample(10, outdoor, new(1,0,0), 1000) && recording.Touch("one",10,1000), "The owner can record and renew its own session.");
Check(recording.Sample(10, destination, new(2,0,0), 2000), "Walking from the street into the plot stays in the same route.");
var savedRoute = recording.Finish("one", 10, destination, new(3,0,0), 2000);
Check(!recording.Active && savedRoute.Points.Count == 2 && savedRoute.Start.House == 0 && savedRoute.DestinationHouse == 123,
    "Saving keeps endpoint ownership and only reduced vertices.");
var lease = Recording(); lease.Sample(10, outdoor, new(1,0,0), 6001);
Check(!lease.Active && lease.Error.Length > 0, "A reload or lost UI heartbeat cancels unsaved recording.");
var ward = Recording(); ward.Sample(10, outdoor with { Ward = 1 }, new(1,0,0), 1000);
Check(!ward.Active, "Ward changes invalidate even identical territory IDs and coordinates.");
var division = Recording(); division.Sample(10, outdoor with { Division = 1 }, new(1,0,0),1000);
Check(!division.Active, "The main ward and its subdivision cannot share an unverified route.");
var teleport = Recording(); teleport.Sample(10, outdoor, new(50,0,0), 1000);
Check(!teleport.Active, "Teleport-sized discontinuities cannot become walk segments.");
var characterSwap = Recording(); characterSwap.Sample(11, outdoor, new(1,0,0), 1000);
Check(!characterSwap.Active, "A character change never appends to the previous route.");
var routeProfile = new HarvestProfile { Cells = [new() { Group="밭1", World=1, Territory=339, House=123 }] };
routeProfile.WalkRoutes["밭1"] = savedRoute;
var cancelled = Recording("two"); cancelled.Cancel("cancel");
try { routeProfile.SaveWalkRoute(cancelled,"two",10,destination,new(1,0,0),1000); throw new InvalidDataException("Cancelled recording saved."); }
catch (InvalidOperationException) { passed++; }
Check(ReferenceEquals(routeProfile.WalkRoutes["밭1"],savedRoute), "Failed replacement preserves the previous route.");
var replacement = Recording("three"); replacement.Sample(10,outdoor,new(1,0,0),1000);
var newRoute = routeProfile.SaveWalkRoute(replacement,"three",10,destination,new(2,0,0),1000);
Check(ReferenceEquals(routeProfile.WalkRoutes["밭1"],newRoute) && newRoute.Matches(routeProfile.Cells[0]), "Successful recording replaces the named route and binds only the matching house.");
Check(!newRoute.CanJoin(outdoor with { Ward=1 },Vector3.Zero), "Replay must reject a different ward, even at the same position.");
var restoredRoute = JsonConvert.DeserializeObject<HarvestProfile>(JsonConvert.SerializeObject(routeProfile))!;
Check(restoredRoute.WalkRoutes["밭1"].Points.Count == 2 && restoredRoute.WalkRoutes["밭1"].Start.World == 1, "Vertices and context survive plugin reload.");
Check(new HarvestProfile().WalkRoutes.Count == 0, "Existing configurations remain valid without recorded routes.");
var io = new ManualGardenRoute("io",10,"밭1","new-home",outdoor,Vector3.Zero,0); io.Sample(10,outdoor,new(1,0,0),1000);
try { routeProfile.SaveWalkRoute(io,"io",10,destination,new(2,0,0),1000,() => throw new IOException("disk")); throw new InvalidDataException("Failed persistence accepted."); }
catch (IOException) { passed++; }
Check(ReferenceEquals(routeProfile.WalkRoutes["밭1"],newRoute) && routeProfile.Cells[0].Travel == "home" && !io.Active,
    "Failed persistence restores both the previous vertices and the linked travel command.");
Check(GardenPolicy.RequiredSlots([(1u, 1, 99, 1)]) == 0, "Existing stack capacity can absorb an output.");
Check(GardenPolicy.RequiredSlots([(1u, 99, 99, 0), (2u, 5, 99, 0)]) == 2, "Crop and crossbred seed use separate capacity.");
Check(GardenPolicy.RequiredSlots([(1u, 1, 99, 0), (1u, 1, 99, 0)]) == 1, "Identical outputs share a stack.");
Check(CropNameMatching.Resolve("붉은 옛 장미가", new Dictionary<string, uint> { ["붉은 옛 장미"] = 14071 }) == 14071, "Korean crop particles must preserve color identity.");
Check(CropNameMatching.Resolve("알 수 없는 꽃이", new Dictionary<string, uint> { ["꽃"] = 1 }) == 0, "Unknown crop text must not match a name fragment.");
using (var game = new Lumina.GameData(@"C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game\sqpack",
    new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false, DefaultExcelLanguage = (Lumina.Data.Language)7 })) {
    var catalog = new Catalog(game.GetExcelSheet<Lumina.Excel.Sheets.Item>(), game.GetExcelSheet<Lumina.Excel.Sheets.GardeningSeed>());
    (Dictionary<uint,int> Supplies, string Error) Prepare(HarvestProfile p, params uint[] roots)
    {
        var service = (HarvestService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HarvestService));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(HarvestService).GetField(name,flags)!.SetValue(service,value);
        Set("owner",10ul); Set("config",new Configuration { Profiles = new() { [10] = p } }); Set("catalog",catalog);
        Set("bookCache",new GardenBookCache()); Set("error","");
        Set("stock",catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct().ToDictionary(id=>id,id=>new Stock(0,0,roots.Contains(id))));
        typeof(HarvestService).GetMethod("Replan",flags)!.Invoke(service,null);
        return ((Dictionary<uint,int>)typeof(HarvestService).GetField("supplies",flags)!.GetValue(service)!,
            (string)typeof(HarvestService).GetField("error",flags)!.GetValue(service)!);
    }
    var noFacility = Prepare(new() { Goals=[new() { Item=14071, Quantity=1 }] },14007);
    Check(noFacility.Supplies.Count == 0, "Actual Replan must not procure seeds when no target cells are registered.");
    var wrongFacility = Prepare(new() { Goals=[new() { Item=14071, Quantity=1 }], Cells=[new() { Kind="plot" }] },14007);
    Check(wrongFacility.Supplies.Count == 0, "Actual Replan must not buy flower seeds when only outdoor beds are registered.");
    var oneCrossCell = Prepare(new() { Goals=[new() { Item=7755, Quantity=1 }], Cells=[new() { Kind="plot" }] },7744,7742);
    Check(oneCrossCell.Supplies.Count == 0, "Actual Replan requires two adjacent cells before preparing crossbreeding parents.");
    var directSeed = Prepare(new() { Goals=[new() { Item=14007, Quantity=2 }] },14007);
    Check(directSeed.Supplies.GetValueOrDefault(14007u) == 2, "Explicit raw seed targets can still be procured without a garden.");
    Check(catalog.SeedCrops.Count == 108 && catalog.Crosses.Count == 3768, "Installed Korean sheets must validate every embedded crossbreeding row.");
    Check(catalog.Crops[14071].Seed == 14007 && catalog.Crops[14071].Hours == 24, "Oldrose growth data must map to the installed Korean seed.");
    Check(catalog.Crops.Values.Any(c => c.Flower && c.Colors.Length == 3), "Mixed flower colors need all three pomaces.");
    var actualView = GardenView.Items(new(), catalog.Crops, catalog.SeedCrops, new Dictionary<uint, Stock>(), new Dictionary<uint, int>(), false);
    Check(actualView.Length == catalog.Crops.Keys.Concat(catalog.SeedCrops.Keys).Distinct().Count()
        && actualView.All(r => r.Seed > 0 && game.GetExcelSheet<Lumina.Excel.Sheets.Item>()!.GetRow(r.Seed).Icon > 0),
        "Every Korean garden and seed row must carry a real seed icon ID: " + string.Join(",", actualView.Where(r => r.Seed == 0).Select(r => r.Item)));
    var real = SeedPlanner.Build(7755, 1, new Dictionary<uint, Stock> {
        [7744] = new(0, 0, true), [7742] = new(0, 0, true) }, catalog.Crosses);
    Check(real.Error == "" && real.Stages.Last().Output == 7755 && real.Stages.Any(s => s.Output == 7748), "Jute must recursively obtain star anise from root seeds.");
    Console.WriteLine($"KR data: {catalog.Crops.Count} outputs, {catalog.SeedCrops.Count} seeds, {catalog.Crosses.Count} crosses.");
}
Console.WriteLine($"PASS {passed} gardening compatibility checks; no native game actions executed.");
}
