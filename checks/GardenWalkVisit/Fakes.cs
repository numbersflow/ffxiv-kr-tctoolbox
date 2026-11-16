using System.Numerics;
namespace Harvest.Core {
 internal sealed class FakeObjects { public FakePlayer LocalPlayer=new(); }
 internal sealed class FakePlayer { public Vector3 Position; }
 internal sealed class FakeIpc {
  internal int Moves; internal bool? IgnoreY; internal bool Busy; internal List<Vector3>? Path; internal string Travel="";
  internal BoolGate GetIpcSubscriber<T>(string name)=>new(this);
  internal TravelGate GetIpcSubscriber<T1,T2>(string name)=>new(this);
  internal MoveGate GetIpcSubscriber<T1,T2,T3,T4,T5>(string name)=>new(this);
 }
 internal sealed class BoolGate(FakeIpc owner) {public bool InvokeFunc()=>owner.Busy;}
 internal sealed class TravelGate(FakeIpc owner) {public void InvokeAction(string command)=>owner.Travel=command;}
 internal sealed class MoveGate(FakeIpc owner) {public void InvokeAction(List<Vector3> path,bool? ignoreY,float? destination,float? tolerance){owner.Moves++;owner.IgnoreY=ignoreY;owner.Path=path;}}
 internal static class ExternalNav {internal static bool TryGetActiveMover(out string? name){name=null;return false;}}
}
namespace Harvest.Planning {
 internal static class Svc { internal static Core.FakeObjects Objects=new(); internal static Core.FakeIpc PluginInterface=new(); }
 internal sealed partial class HarvestService {
  private sealed class Visit {public HashSet<string> WalkedRoutes=[],HomeArrivals=[];public bool Travel,RouteWalking;public DateTime StepAt,TravelAt;}
  private readonly Visit work=new();
  private readonly HarvestProfile Profile=new();
  internal HarvestService(GardenWalkRoute route){Profile.WalkRoutes[route.Group]=route;}
  private GardenRouteLocation CurrentRouteLocation()=>new(1,2,1,3);
  private bool AtCell(GardenCell cell)=>true;
  internal bool Run(GardenCell cell)=>WalkingRouteInProgress(work,cell,out _);
  internal void TeleportFinished()=>work.TravelAt=DateTime.UtcNow.AddSeconds(-2);
  internal bool Finished=>work.WalkedRoutes.Contains("밭");
 }
}
