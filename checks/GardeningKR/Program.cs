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
var menuTypes = new bool[11]; menuTypes[8]=menuTypes[9]=menuTypes[10]=true;
Check(ContextMenuLayoutDerivation.ValidateEntries(8,3,menuTypes), "KR context-menu candidate is confirmed by the live eight-field header and complete text entries.");
Check(!ContextMenuLayoutDerivation.ValidateEntries(7,3,menuTypes) && !ContextMenuLayoutDerivation.ValidateEntries(9,2,menuTypes), "An off-by-one menu candidate cannot send the adjacent item's callback.");
Check(!ContextMenuLayoutDerivation.ValidateEntries(8,4,menuTypes) && !ContextMenuLayoutDerivation.ValidateEntries(8,-1,menuTypes), "Truncated or invalid menu counts remain fail-closed.");
var resetPattern = ContextMenuLayoutDerivation.Sources[0].Variants[0].Signature;
var resetBytes = resetPattern.Split(' ').Select(t=>t=="??" ? (byte)0 : Convert.ToByte(t,16)).ToArray();
resetBytes[6]=8; BitConverter.GetBytes(48).CopyTo(resetBytes,15); BitConverter.GetBytes(50).CopyTo(resetBytes,21);
byte[]? SingleReset(string signature,int length) => signature==resetPattern ? resetBytes : null;
Check(ContextMenuLayoutDerivation.Derive(SingleReset,out _) == ContextMenuLayoutDerivation.NotDerived
    && ContextMenuLayoutDerivation.DeriveForRuntimeValidation(SingleReset,out _) == 8,
    "The observed KR single-source signature remains insufficient alone; only the runtime-validated menu path accepts its candidate.");
Check(GardenPolicy.Soil(false, 7766) == 16026 && GardenPolicy.Soil(true, 7766) == 7766,
    "Ordinary planting uses vendor potting soil; crossing preserves the configured crossing soil.");
var partialStock = new Dictionary<uint, Stock> { [17547]=new(8,0,true), [16026]=new(1,0,true), [7766]=new(0,0,true,false) };
var plantingCell = new GardenCell { Seed=17547 };
Check(GardenPolicy.CanVisit(plantingCell,partialStock,16026), "One available soil allows one planting without waiting for all eight supplies.");
partialStock[16026] = new(0,0,true);
Check(!GardenPolicy.CanVisit(plantingCell,partialStock,16026), "After consuming the last soil, empty cells return to material preparation.");
plantingCell.Owned=true;
Check(GardenPolicy.CanVisit(plantingCell,partialStock,16026), "Owned crops can be tended or harvested even while new planting materials are missing.");
plantingCell.Owned=false; plantingCell.Pending="Plant";
Check(GardenPolicy.CanVisit(plantingCell,partialStock,16026), "An uncertain previous operation is reconciled before any new material preparation.");
bool ReadPlanningResult(Func<bool, TimeSpan> interval)
{
    var cache = new GardenBookCache();
    long now = 0, completedAt = -1;
    for (var attempt = 0; attempt < 8; attempt++) {
        if (completedAt < 0 || now >= completedAt + 1000) completedAt = now + 500;
        cache.Update(now < completedAt ? "{\"Available\":false,\"Pending\":true}"
            : "{\"Available\":true,\"Pending\":false,\"Character\":10,\"Expansion\":6,\"Error\":\"\",\"Rows\":[{\"ItemId\":17548,\"Needed\":8}]}", 10);
        if (cache.Ready) return cache.Needs.GetValueOrDefault(17548u) == 8;
        now += (long)interval(cache.Updating).TotalMilliseconds;
    }
    return false;
}
Check(!ReadPlanningResult(_ => TimeSpan.FromSeconds(5)) && ReadPlanningResult(HarvestService.RefreshInterval),
    "The old five-second polling repeatedly starts a new count refresh; pending retries must consume the completed plan and restore readiness.");
Check(HarvestService.RefreshInterval(false) == TimeSpan.FromSeconds(5), "Completed planning retains the normal bounded refresh interval.");
var origin = new System.Numerics.Vector3(54.96415f, 25.99997f, -67.922775f);
var offsets = new[] { (-1f,-1f), (0f,-1f), (1f,-1f), (1f,0f), (1f,1f), (0f,1f), (-1f,1f), (-1f,0f) };
var observedPatches = offsets.Select((v,i) => new GardenPatch((ulong)(100+i), "plot", 4294639623u,
    (uint)(7+i) << 24, origin, origin + new System.Numerics.Vector3(v.Item1,0,v.Item2))).Reverse().ToArray();
var orderedPatches = GardenRegistration.Nearest(observedPatches, origin);
Check(orderedPatches.Length == 8 && orderedPatches.Select(p=>p.SubId).SequenceEqual(observedPatches.OrderBy(p=>p.SubId).Select(p=>p.SubId)), "Captured Korean garden: eight objects share one origin but have distinct stable layout slots in ring order.");
var rotation = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, 2);
var rotatedPatches = observedPatches.Select(p => p with { Slot = origin + System.Numerics.Vector3.Transform(p.Slot-origin,rotation) }).ToArray();
Check(GardenRegistration.Nearest(rotatedPatches,origin).Select(p=>p.SubId).SequenceEqual(orderedPatches.Select(p=>p.SubId)), "Rotating furniture must not change automatic slot numbering.");
var autoCells = orderedPatches.Select((p,i) => new GardenCell { Group="밭", Index=i, World=1, Territory=2, House=3,
    X=origin.X, Y=origin.Y, Z=origin.Z, LayoutInstanceKey=p.InstanceKey, LayoutSubId=p.SubId }).ToArray();
Check(autoCells.All(c=>observedPatches.Count(p=>GardenRegistration.Matches(c,p))==1), "Actual visit matching uniquely resolves each slot despite identical object positions.");
Check(autoCells.All(c=>observedPatches.Select(p=>p with { Object=p.Object+1000 }).Count(p=>GardenRegistration.Matches(c,p))==1), "Recreated object IDs must not invalidate saved layout identity.");
Check(autoCells.All(c=>observedPatches.Select(p=>p with {Object=p.Object+1000,InstanceKey=p.InstanceKey+1}).Count(p=>GardenRegistration.Matches(c,p))==1), "Zone re-entry can recreate layout instance keys without changing the registered facility and slot.");
Check(!GardenRegistration.Matches(autoCells[0],observedPatches[0] with {Origin=origin+new Vector3(4,0,0)}),"A matching sub-ID in another bed must not match the registered bed.");

var autoProfile = new HarvestProfile { Cells=[new() { Group="밭", World=1, Territory=2, House=3, X=origin.X, Y=origin.Y, Z=origin.Z }] };
autoProfile.Batch = new() { Cells=[autoProfile.Cells[0].Id] };
Check(autoProfile.RegisterPatches(autoCells,()=>{}) == 8 && autoProfile.Cells.Count==8 && autoProfile.Batch==null, "Automatic registration replaces the unused ambiguous legacy record and clears only its unstarted batch.");
autoProfile.Cells[0].Owned=true; autoProfile.Cells[0].Seed=123; autoProfile.Cells[0].Enabled=false;
Check(autoProfile.RegisterPatches(autoCells,()=>{})==0 && autoProfile.Cells.Count==8 && autoProfile.Cells[0].Owned && autoProfile.Cells[0].Seed==123 && !autoProfile.Cells[0].Enabled, "Repeated automatic registration is idempotent and preserves crop records and disabled slots.");
var recreatedCells=autoCells.Select(c=>new GardenCell {Group=c.Group,Index=c.Index,World=c.World,Territory=c.Territory,House=c.House,Kind=c.Kind,X=c.X,Y=c.Y,Z=c.Z,LayoutInstanceKey=c.LayoutInstanceKey+1,LayoutSubId=c.LayoutSubId}).ToArray();
Check(autoProfile.RegisterPatches(recreatedCells,()=>{})==0 && autoProfile.Cells.Count==8 && autoProfile.Cells[0].Owned && autoProfile.Cells[0].Seed==123,"Re-entering a zone must not duplicate slots or overwrite the planted crop during registration");
{
    var service=(HarvestService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HarvestService));
    var visitType=typeof(HarvestService).GetNestedType("Visit",System.Reflection.BindingFlags.NonPublic)!;
    var work=Activator.CreateInstance(visitType,true)!;
    var arrivals=(HashSet<string>)visitType.GetField("HomeArrivals")!.GetValue(work)!;
    var walked=(HashSet<string>)visitType.GetField("WalkedRoutes")!.GetValue(work)!;
    var failed=(HashSet<string>)visitType.GetField("FailedRoutes")!.GetValue(work)!;
    arrivals.Add("bed");walked.Add("bed");failed.Add("bed");
    var stop=typeof(HarvestService).GetMethod("StopOwnedTravel",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
    stop.Invoke(service,new object[]{work,false});
    Check(arrivals.Contains("bed") && walked.Contains("bed") && failed.Contains("bed"),"A cell failure must preserve completed travel and cannot trigger a fresh teleport for the next slot");
    stop.Invoke(service,new object[]{work,true});
    Check(arrivals.Count==0 && walked.Count==0 && failed.Count==0,"Cancellation and pause invalidate the route so the next visit starts with a fresh home teleport");
}
var rollbackProfile = new HarvestProfile();
try { rollbackProfile.RegisterPatches(autoCells,()=>throw new IOException("save failed")); throw new Exception("Failed bulk save accepted."); }
catch(IOException) { Check(rollbackProfile.Cells.Count==0, "Bulk registration rolls back every new slot on persistence failure."); }
try { GardenRegistration.Nearest(observedPatches,origin+new System.Numerics.Vector3(20,0,0)); throw new Exception("Remote garden accepted."); }
catch(InvalidOperationException) { passed++; }
try { GardenRegistration.Nearest(observedPatches.Take(7).ToArray(),origin); throw new Exception("Incomplete garden accepted."); }
catch(InvalidOperationException) { passed++; }
var registered = new HarvestProfile();
var firstCell = new GardenCell { Group = "밭", Index = 0, World = 1, Territory = 2, House = 3, X = 10 };
var saves = 0;
registered.RegisterCell(firstCell, () => saves++);
Check(registered.Cells.Single() == firstCell && saves == 1, "RegisterCell persists the selected cell exactly once.");
try { registered.RegisterCell(new() { Group = "밭", Index = 0, World = 1, Territory = 2, House = 3, X = 12 }, () => saves++); throw new Exception("Duplicate index accepted."); }
catch (ArgumentException) { Check(registered.Cells.Count == 1 && saves == 1, "Duplicate index must not mutate or save registration."); }
try { registered.RegisterCell(new() { Group = "다른 밭", Index = 1, World = 1, Territory = 2, House = 3, X = 10 }, () => saves++); throw new Exception("Duplicate physical cell accepted."); }
catch (ArgumentException) { Check(registered.Cells.Count == 1 && saves == 1, "The same physical cell cannot be assigned another group or index."); }
try { registered.RegisterCell(new() { Group = "밭", Index = 1, World = 1, Territory = 2, House = 3, X = 12 }, () => throw new IOException("save failed")); throw new Exception("Failed save accepted."); }
catch (IOException) { Check(registered.Cells.Count == 1 && registered.Cells[0] == firstCell, "Failed persistence rolls back only the new registration."); }
registered.RegisterCell(new() { Group = "밭", Index = 1, World = 1, Territory = 2, House = 3, X = 12 }, () => saves++);
Check(registered.Cells.Count == 2 && saves == 2, "A different physical cell can be registered under the next index.");
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
var scopeProfile = new HarvestProfile { BookItems = [17548] };
Check(scopeProfile.SetLegacyBookScope(1), "Legacy selections inherit the user's active HW scope once");
Check(!scopeProfile.SetLegacyBookScope(3), "Migration cannot overwrite an explicitly saved expansion");
GardenBookCache ScopeCache(uint expansion, int needed) {
    var cache = new GardenBookCache();
    cache.Update(JsonConvert.SerializeObject(new { Available=true,Character=10ul,Expansion=expansion,Pending=false,Error="",Rows=new[]{new {ItemId=17548u,Needed=needed}} }),10,expansion);
    return cache;
}
var scopeCaches = new Dictionary<uint,GardenBookCache> { [6]=ScopeCache(6,8),[1]=ScopeCache(1,2),[3]=ScopeCache(3,6) };
Check(GardenBookCache.SelectedNeeds(scopeProfile,scopeCaches)[17548] == 2, "HW-only cloud acorns must exclude the ShB chandelier's six acorns");
Check(scopeProfile.BookSelected(17548,1) && !scopeProfile.BookSelected(17548,3), "Expansion views show only that scope's selection");
scopeProfile.SelectBook(17548,true,3);
Check(GardenBookCache.SelectedNeeds(scopeProfile,scopeCaches)[17548] == 8, "Only explicitly selected expansions are combined");
scopeProfile.SelectBook(17548,false,1);
Check(GardenBookCache.SelectedNeeds(scopeProfile,scopeCaches)[17548] == 6 && scopeProfile.BookItems.Contains(17548), "Removing HW preserves the independently selected ShB goal");
scopeProfile.SelectBook(17548,true,6);
Check(GardenBookCache.SelectedNeeds(scopeProfile,scopeCaches)[17548] == 8, "Selecting all uses the full snapshot once, without double counting");
scopeProfile.SelectBook(17548,false,3);
Check(scopeProfile.BookSelected(17548,1) && !scopeProfile.BookSelected(17548,3), "An individual expansion can be excluded from an all-expansion selection");
var restoredScopes = JsonConvert.DeserializeObject<HarvestProfile>(JsonConvert.SerializeObject(scopeProfile))!;
Check(restoredScopes.BookScopes[17548].SetEquals(scopeProfile.BookScopes[17548]), "Saved per-item expansion scopes survive reload");
var reservedCells=Enumerable.Range(0,8).Select(i=>new GardenCell {Index=i}).ToArray();
var reservedBatch=new GardenBatch {Required=8,Cells=reservedCells.Select(c=>c.Id).ToList()};
Check(GardenPolicy.LimitOrdinaryBatch(reservedBatch,reservedCells,2,0) && reservedBatch.Cells.Count==2 && reservedBatch.Required==2,"Reducing the scope releases surplus unplanted cells");
foreach(var cell in reservedCells)cell.Owned=true;
reservedBatch.Cells=reservedCells.Select(c=>c.Id).ToList();
GardenPolicy.LimitOrdinaryBatch(reservedBatch,reservedCells,2,0);
Check(reservedBatch.Cells.Count==8 && reservedCells.All(c=>c.Owned),"Already planted cells survive a narrower scope");
reservedBatch.Cross=true; reservedBatch.Required=8;
Check(!GardenPolicy.LimitOrdinaryBatch(reservedBatch,reservedCells,2,0) && reservedBatch.Required==8,"Crossbreeding parent stages are not truncated like ordinary crops");
{
    var owned = new GardenCell {Owned=true,State="growing",Tended=DateTime.UtcNow.AddHours(-13)};
    var p = new HarvestProfile {BookItems=[17548],Cells=[owned],Batch=new(){Cells=[owned.Id],Clearing=true}};
    var service=(HarvestService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HarvestService));
    const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    void Set(string name,object value)=>typeof(HarvestService).GetField(name,flags)!.SetValue(service,value);
    Set("owner",10ul);Set("config",new Configuration{Profiles=new(){[10]=p}});Set("bookCache",new GardenBookCache());
    Set("stock",new Dictionary<uint,Stock>{[17548]=new(2,0,false)});
    bool DueVisit()=>(bool)typeof(HarvestService).GetMethod("HasDueVisit",flags)!.Invoke(service,null)!;
    Check(DueVisit(),"Meeting the reduced goal must not abandon already planted owned crops");
    owned.Owned=false;owned.State="empty";
    Check(!DueVisit(),"Once owned crops are cleared, meeting the goal stops further garden visits");
}
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
