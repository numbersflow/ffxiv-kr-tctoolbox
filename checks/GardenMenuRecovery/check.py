"""Exercise the actual Harvest queue and menu cleanup with fake game UI and events."""
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[2] / "Harvest"
module = (root / "Modules/AutoGardensWork.cs").read_text(encoding="utf-8-sig")
queue = (root / "Core/TaskQueue.cs").read_text(encoding="utf-8-sig")

def section(start, end):
    i = module.index(start)
    return module[i:module.index(end, i)]

program = r'''
using System;
using System.Collections.Generic;
using Harvest.Core;
Case(true, () => Svc.PlayerState.ContentId++);
Case(true, () => Svc.ClientState.TerritoryType++);
Case(true, () => EventFramework.SetObject(8));
Case(false, () => {});
var stop = new Garden();
Reset();
stop.Begin(false);
stop.StopBatch();
stop.StopBatch();
Check(stop.Busy,"Repeated stop cannot discard pending dialog cleanup");
stop.Tick();
Check(UiHelper.TalkClicks == 1,"Manual stop can drain its own opening dialog");
Console.WriteLine("PASS Harvest timeout, cancel, verified exit, and foreign dialog protection");

void Case(bool foreign, Action change) {
    Reset();
    var garden = new Garden();
    garden.Begin(true);
    change();
    garden.Tick(); // Timeout aborts the initial operation and schedules cleanup.
    Check(garden.Busy,"The worker cannot report idle during cleanup");
    garden.Tick();
    if (foreign) {
        Check(!garden.Busy && UiHelper.TalkClicks == 0 && UiHelper.MenuCancels == 0,"Foreign dialog remains untouched");
        return;
    }
    Check(garden.Busy && UiHelper.TalkClicks == 1,"Advance the verified garden Talk until its menu opens");
    UiHelper.Talk = false;
    UiHelper.Menu = true;
    UiHelper.AcceptCancel = false;
    garden.Tick();
    Check(garden.Busy && UiHelper.MenuCancels == 0,"An undelivered cancel can be retried");
    UiHelper.AcceptCancel = true;
    garden.Tick();
    garden.Tick();
    Check(garden.Busy && UiHelper.MenuCancels == 1,"A sent cancel is attempted only once and waits for closure");
    UiHelper.Menu = false;
    Svc.Condition.Occupied = false;
    garden.Tick();
    Check(!garden.Busy && garden.Actions == 0,"Only after closure can another job start; no crop action was submitted");
}
void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
void Reset() {
    Svc.ClientState.TerritoryType = 100;
    Svc.PlayerState.ContentId = 1;
    Svc.Condition.Occupied = true;
    EventFramework.SetObject(7);
    UiHelper.Talk = true; UiHelper.Menu = false; UiHelper.AcceptCancel = true;
    UiHelper.TalkClicks = 0; UiHelper.MenuCancels = 0;
}

unsafe class Garden {
    private readonly TaskQueue queue = new();
    private const string CloseOpeningPatchMenuStep = "close";
    private PatchJob? openingPatchMenu;
    private ulong openingPatchOwner;
    private uint openingPatchTerritory;
    private int doneCount, skippedCount;
    private string lastSummary = "", textCancel = "Cancel";
    private bool TalkAndMenuExclusive = true;
    public int Actions;
    public bool Busy => queue.IsBusy;
    private class PatchJob { public bool Interacted = true; public ulong GameObjectId = 7; }
    public void Begin(bool timeout) {
        openingPatchMenu = new();
        openingPatchOwner = 1;
        openingPatchTerritory = 100;
        ON_TIMEOUT
        queue.Enqueue("wait", () => false, timeout ? -1 : 8000);
        queue.Enqueue("act", () => { Actions++; return true; });
    }
    public void Tick() => queue.Tick();
    private void StopWalkIfMoving() {}
    CLEANUP_AND_STOP
}
enum ConditionFlag { OccupiedInQuestEvent }
class Conditions { public bool Occupied; public bool this[ConditionFlag _] => Occupied; }
unsafe struct EventFramework {
    public ObjectId SceneGameObjectId;
    public EventState EventState1, EventState2;
    private static EventFramework* instance = (EventFramework*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)sizeof(EventFramework));
    public static EventFramework* Instance() => instance;
    public static void SetObject(ulong id) => instance->SceneGameObjectId.Id = id;
}
struct EventState { public ObjectId ObjectId; }
struct ObjectId { public ulong Id; }
static class UiHelper {
    public const string TalkAddonName = "Talk";
    public static bool Talk,Menu,AcceptCancel;
    public static int TalkClicks,MenuCancels;
    public static bool ClickTalkIfOpen() { TalkClicks++; return true; }
    public static int GetAddon(string _) => 1;
    public static bool IsReady(int _) => Menu;
    public static bool LooksMidUpdate(List<string> _) => false;
    public static List<string> GetSelectStringEntries(int _) => ["Harvest", "Cancel"];
    public static bool TryFireCallback(int _,bool __,int index) {
        if(index != 1) throw new Exception("Unexpected menu selection");
        if(!AcceptCancel) return false;
        MenuCancels++;
        return true;
    }
}
static class TextAdvanceLock { public static bool MayClickTalk = true; }
static class Svc {
    public static Conditions Condition = new();
    public static class ClientState { public static bool IsLoggedIn = true; public static uint TerritoryType; }
    public static class PlayerState { public static ulong ContentId; }
    public static class GameGui {
        public static (bool IsVisible,int Other) GetAddonByName(string name) => (name=="Talk" ? UiHelper.Talk : UiHelper.Menu,0);
    }
    public static class Chat { public static void PrintError(string _) {} }
    public static class Log { public static void Warning(string _) {} public static void Information(string _) {}
        public static void Error(Exception _,string __) {} }
}
'''
program = program.replace("ON_TIMEOUT", section("queue.OnTimeout = step =>", "// 走位會發起"))
program = program.replace("CLEANUP_AND_STOP", section("private void QueueCloseOpeningPatchMenu()", "    #endregion"))
with tempfile.TemporaryDirectory(prefix="harvest-menu-") as folder:
    target = Path(folder)
    (target / "TaskQueue.cs").write_text(queue, encoding="utf-8")
    (target / "Program.cs").write_text(program, encoding="utf-8")
    (target / "Check.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks>'
        '<ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>', encoding="utf-8")
    subprocess.run(["dotnet", "run", "--project", str(target / "Check.csproj"), "-c", "Release"], check=True)
