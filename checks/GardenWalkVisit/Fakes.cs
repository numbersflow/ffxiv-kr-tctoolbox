using System.Numerics;
namespace Harvest.Core {
 internal sealed class FakeObjects { public FakePlayer LocalPlayer=new(); }
 internal sealed class FakePlayer { public Vector3 Position; }
 internal sealed class FakeIpc {
  internal int Moves,HomeRequests; internal bool? IgnoreY; internal bool Busy; internal List<Vector3>? Path; internal string Travel=""; internal int? TravelMode; internal int HouseOverride=2;
  internal BoolGate GetIpcSubscriber<T>(string name)=>new(this);
  internal TravelGate GetIpcSubscriber<T1,T2>(string name)=>new(this);
  internal PropertyGate GetIpcSubscriber<T1,T2,T3>(string name)=>new(this);
  internal MoveGate GetIpcSubscriber<T1,T2,T3,T4,T5>(string name)=>new(this);
 }
 internal sealed class BoolGate(FakeIpc owner) {public bool InvokeFunc()=>owner.Busy;}
 internal sealed class TravelGate(FakeIpc owner) {public void InvokeAction(string command){owner.HomeRequests++;owner.Travel=command;owner.TravelMode=owner.HouseOverride;}}
 internal sealed class PropertyGate(FakeIpc owner) {public void InvokeAction(int property,int? mode){if(property!=1)throw new Exception("Only the personal home may be used");owner.HomeRequests++;owner.Travel="home";owner.TravelMode=mode;}}
 internal sealed class MoveGate(FakeIpc owner) {public void InvokeAction(List<Vector3> path,bool? ignoreY,float? destination,float? tolerance){owner.Moves++;owner.IgnoreY=ignoreY;owner.Path=path;}}
 internal static class ExternalNav {internal static bool TryGetActiveMover(out string? name){name=null;return false;}}
}
namespace Harvest.Planning {
 internal sealed class FakeLog {internal void Information(string message) { }}
 internal static class Svc { internal static FakeLog Log=new(); internal static Core.FakeObjects Objects=new(); internal static Core.FakeIpc PluginInterface=new(); }
 internal sealed partial class HarvestService {
  private sealed class Visit {public HashSet<string> WalkedRoutes=[],HomeArrivals=[],FailedRoutes=[];public bool Travel,RouteWalking;public DateTime StepAt,TravelAt,ArrivalCheckAt;}
  private readonly Visit work=new();
  private readonly HarvestProfile Profile=new();
  internal HarvestService(GardenWalkRoute route){Profile.WalkRoutes[route.Group]=route;}
  private GardenRouteLocation CurrentRouteLocation()=>new(1,2,1,3);
  internal string CurrentKind="plot";
  private bool AtCell(GardenCell cell)=>cell.Kind==CurrentKind;
  internal bool Run(GardenCell cell)=>WalkingRouteInProgress(work,cell,out _);
  internal void TeleportFinished()=>work.TravelAt=DateTime.UtcNow.AddSeconds(-2);
  internal void RemoveRoute()=>Profile.WalkRoutes.Clear();
  internal void ArrivalTimedOut()=>work.ArrivalCheckAt=DateTime.UtcNow.AddSeconds(-11);
  internal void MarkFailed()=>work.FailedRoutes.Add(Profile.WalkRoutes.Keys.First());
  internal bool Finished=>work.WalkedRoutes.Contains("밭");
 }
}
