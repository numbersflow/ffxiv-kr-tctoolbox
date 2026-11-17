using System.Numerics;
using System.Text.Json;
using Harvest.Core;
using Harvest.Planning;
namespace LocalAutomation {
    public record AutomationObservation(bool Busy,int Completed,int Total,string Phase,string Result="",string Detail="",long Progress=0);
    internal record AutomationWork(Func<bool> Start,Action Stop,Func<AutomationObservation> Observe,Func<bool>? CanCancel=null,Action? Unload=null,Action? Disconnect=null,Action<bool,bool>? Pause=null,Func<string>? PauseState=null);
}
namespace Harvest.Modules {
    internal class AutoGardensWork {
        internal enum GardenAction { Scan,Plant,Tend,Harvest,Fertilize }
        public bool IsBusy,IsEnabled;public int DoneCount,SkippedCount;
        public const float InteractRange=3;
        public void Enable()=>IsEnabled=true;
        public void Disable()=>IsEnabled=false;
        public float GetPatchDistance(ulong id)=>0;
        public (PatchState State,uint Crop,DateTime At) ReadObservation(ulong id)=>VisitDriver.Current.Seen[id];
        public string EnqueueSingle(GardenAction action,ulong id,uint item,uint seed,uint soil) {
            var run=VisitDriver.Current;
            if(action==GardenAction.Scan){DoneCount=0;SkippedCount=0;var p=run.Patches[id];run.Seen[id]=(p.State,p.Crop,DateTime.UtcNow.AddTicks(1));return "";}
            DoneCount=action==GardenAction.Tend && run.FailTendReceipt?0:1;
            run.Calls.Add((id,action.ToString()));
            var state=run.Patches[id];
            if(action==GardenAction.Plant){run.Inventory[seed]--;run.Inventory[soil]--;run.Patches[id]=(PatchState.Growing,run.Catalog.SeedCrops[seed]);}
            if(action==GardenAction.Tend && state.State==PatchState.NeedsCare)run.Patches[id]=(PatchState.Growing,state.Crop);
            if(action==GardenAction.Fertilize)run.Inventory[item]--;
            if(action==GardenAction.Harvest){
                foreach(var p in run.Rewards.GetValueOrDefault(id) ?? new Dictionary<uint,int>{{state.Crop,1}})
                    run.Inventory[p.Key]=run.Inventory.GetValueOrDefault(p.Key)+p.Value;
                run.Patches[id]=(PatchState.Empty,0);
            }
            return "";
        }
    }
}
namespace Harvest.Planning {
    public class Catalog {
        public Dictionary<uint,Crop> Crops=[];public Dictionary<uint,uint> SeedCrops=[];
        public Dictionary<uint,string> Names=[];public List<Cross> Crosses=[];
    }
    internal class Configuration {
        internal class GardenSettings {public bool AutoLoopEnabled;}
        internal GardenSettings GardensWork=new();public void Save() { }
    }
    internal static class ExternalNav {
        internal static bool TryGetActiveMover(out string? mover){mover=null;return false;}
        internal static bool IsVnavmeshPathRunning()=>false;
        internal static bool IsVnavmeshPathfindInProgress()=>false;
        internal static void TryStopMovement() { }
        internal static bool TryMoveTo(Vector3 p,bool fly,out bool started,string owner){started=true;return true;}
    }
    internal class Gate<T>(string name) {
        internal bool HasFunction=>true;internal bool HasAction=>true;
        internal T InvokeFunc(params object[] args) {
            if(name=="Lifestream.IsWorkflowPaused")return (T)(object)false;
            var ids=JsonSerializer.Deserialize<uint[]>((string)args[0])!;
            return (T)(object)JsonSerializer.Serialize(new{Available=true,Character=10ul,Rows=ids.Select(id=>new{ItemId=id,Inventory=VisitDriver.Current.Inventory.GetValueOrDefault(id)}).ToArray()});
        }
        internal void InvokeAction(params object[] args) { }
    }
    internal class Ipc {
        internal Gate<T> GetIpcSubscriber<T>(string name)=>new(name);
        internal Gate<T> GetIpcSubscriber<A,T>(string name)=>new(name);
    }
    internal static class Svc {
        internal class Player {internal ulong ContentId=10;internal Vector3 Position=Vector3.Zero;}
        internal class ObjectTable {internal Player? LocalPlayer=new();}
        internal class LogService {internal void Information(string message) { }}
        internal static Player PlayerState=new();internal static ObjectTable Objects=new();
        internal static LogService Log=new();internal static Ipc PluginInterface=new();
    }
    public sealed partial class VisitDriver {
        public static VisitDriver Current=null!;
        private readonly Configuration config=new();private readonly Harvest.Modules.AutoGardensWork garden=new();
        public Catalog Catalog=new();private Catalog catalog=>Catalog;
        public HarvestProfile Profile=new();private readonly ulong owner=10;
        public Dictionary<uint,int> Inventory=[];
        public Dictionary<ulong,(PatchState State,uint Crop)> Patches=[];
        public Dictionary<ulong,(PatchState State,uint Crop,DateTime At)> Seen=[];
        public Dictionary<ulong,Dictionary<uint,int>> Rewards=[];
        public List<(ulong Id,string Action)> Calls=[];
        public int Teleports,Free=200;public bool FailTendReceipt;
        private Dictionary<uint,Stock> stock=[];private Dictionary<uint,int> books=[],supplies=[];
        private Visit? visit;private DateTime nextRefresh;private string error="",snapshot="";
        private class Recording {internal bool Active;}
        private Recording? routeRecording=null;
        private LocalAutomation.AutomationWork work=null!;
        public VisitDriver(){Current=this;Svc.PlayerState.ContentId=10;}
        public void Initialize() {
            stock=Catalog.Crops.Keys.Concat(Catalog.SeedCrops.Keys).Concat(Inventory.Keys).Append(16026u).Append(7766u).Distinct()
                .ToDictionary(id=>id,id=>new Stock(Inventory.GetValueOrDefault(id),0,Catalog.SeedCrops.ContainsKey(id)));
            foreach(var c in Profile.Cells){c.LayoutInstanceKey=1;c.LayoutSubId=(uint)c.Index;var id=(ulong)c.Index+1;
                Patches.TryAdd(id,(c.Owned?c.State=="mature"?PatchState.Ripe:PatchState.Growing:PatchState.Empty,c.Crop));}
            Replan();
        }
        public bool Ready()=>Profile.ActiveBatches.Any() && HasDueVisit() && Profile.Cells.Any(c=>Profile.BatchFor(c)!=null&&c.Enabled&&Due(c)&&ReadyForVisit(c));
        public bool Start(bool careOnly=false){using var args=JsonDocument.Parse(JsonSerializer.Serialize(new{CareOnly=careOnly}));work=CreateWork("Gardening",args.RootElement);return work.Start();}
        public LocalAutomation.AutomationObservation Tick()=>work.Observe();
        public LocalAutomation.AutomationObservation Run(){for(int i=0;i<300;i++){var r=Tick();if(!r.Busy)return r;}throw new Exception("Visit did not settle: "+error);}
        public void ChangeCharacter(ulong id)=>Svc.PlayerState.ContentId=id;
        public void Disconnect()=>work.Disconnect!();
        public void Cancel()=>work.Stop();public void Pause(bool paused)=>work.Pause!(paused,false);
        public bool GardenBusy {get=>garden.IsBusy;set=>garden.IsBusy=value;}
        public Dictionary<uint,int> Supplies=>supplies;
        public bool CachedReady=>JsonDocument.Parse(snapshot).RootElement.GetProperty("Ready").GetBoolean();
        private static int FreeSlots()=>Current.Free;
        private int HarvestSlots(GardenCell cell)=>1;
        private static bool AtCell(GardenCell cell)=>true;
        private GardenPatch[] NearbyPatches()=>Profile.Cells.Select(c=>new GardenPatch((ulong)c.Index+1,c.Kind,1,(uint)c.Index,c.Position,c.Position)).ToArray();
        private bool WalkingRouteInProgress(Visit run,GardenCell cell,out string phase){phase="teleport";if(run.HomeArrivals.Add(cell.Group)){Teleports++;return true;}return false;}
    }
}
