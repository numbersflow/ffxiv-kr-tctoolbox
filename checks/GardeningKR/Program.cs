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
Check(GardenPolicy.RequiredSlots([(1u, 1, 99, 1)]) == 0, "Existing stack capacity can absorb an output.");
Check(GardenPolicy.RequiredSlots([(1u, 99, 99, 0), (2u, 5, 99, 0)]) == 2, "Crop and crossbred seed use separate capacity.");
Check(GardenPolicy.RequiredSlots([(1u, 1, 99, 0), (1u, 1, 99, 0)]) == 1, "Identical outputs share a stack.");
Check(CropNameMatching.Resolve("붉은 옛 장미가", new Dictionary<string, uint> { ["붉은 옛 장미"] = 14071 }) == 14071, "Korean crop particles must preserve color identity.");
Check(CropNameMatching.Resolve("알 수 없는 꽃이", new Dictionary<string, uint> { ["꽃"] = 1 }) == 0, "Unknown crop text must not match a name fragment.");
using (var game = new Lumina.GameData(@"C:\Program Files (x86)\FINAL FANTASY XIV - KOREA\game\sqpack",
    new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false, DefaultExcelLanguage = (Lumina.Data.Language)7 })) {
    var catalog = new Catalog(game.GetExcelSheet<Lumina.Excel.Sheets.Item>(), game.GetExcelSheet<Lumina.Excel.Sheets.GardeningSeed>());
    Check(catalog.SeedCrops.Count == 108 && catalog.Crosses.Count == 3768, "Installed Korean sheets must validate every embedded crossbreeding row.");
    Check(catalog.Crops[14071].Seed == 14007 && catalog.Crops[14071].Hours == 24, "Oldrose growth data must map to the installed Korean seed.");
    Check(catalog.Crops.Values.Any(c => c.Flower && c.Colors.Length == 3), "Mixed flower colors need all three pomaces.");
    var real = SeedPlanner.Build(7755, 1, new Dictionary<uint, Stock> {
        [7744] = new(0, 0, true), [7742] = new(0, 0, true) }, catalog.Crosses);
    Check(real.Error == "" && real.Stages.Last().Output == 7755 && real.Stages.Any(s => s.Output == 7748), "Jute must recursively obtain star anise from root seeds.");
    Console.WriteLine($"KR data: {catalog.Crops.Count} outputs, {catalog.SeedCrops.Count} seeds, {catalog.Crosses.Count} crosses.");
}
Console.WriteLine($"PASS {passed} gardening compatibility checks; no native game actions executed.");
}
