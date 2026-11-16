using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TCToolbox;
using TCToolbox.Core;

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
Console.WriteLine($"PASS {passed} gardening compatibility checks; no native game actions executed.");
}
