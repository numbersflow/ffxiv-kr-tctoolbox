using System.Numerics;
using Harvest.Planning;
using Harvest.Core;
var checks=0;
void Check(bool condition,string message){if(!condition)throw new Exception(message);checks++;}
var cell=new GardenCell {Group="밭",World=1,Territory=2,House=3};
var route=new GardenWalkRoute {Group="밭",Travel="home",Start=new(1,2,1,3),DestinationHouse=3,
    Points=[new(0,0,0),new(0,1,2),new(2,1,2)]};
var service=new HarvestService(route);
Svc.Objects.LocalPlayer.Position=Vector3.Zero;
Check(service.Run(cell) && Svc.PluginInterface.Travel=="home" && Svc.PluginInterface.Moves==0,"A visit must teleport even when already at the saved house arrival point.");
Check(Svc.PluginInterface.TravelMode==0,"Recorded outdoor route must explicitly request teleport-only mode even when Lifestream is configured to enter the house.");
service.TeleportFinished();
Check(service.Run(cell),"A recorded ground route starts from its saved arrival context.");
Check(Svc.PluginInterface.Moves==1 && Svc.PluginInterface.IgnoreY==true,"Recorded stairs must use Lifestream ground movement; false selects flying behavior and freezes uphill walking.");
Check(Svc.PluginInterface.Path!.SequenceEqual(route.Points.Select(p=>p.Position)),"Ground mode preserves the recorded three-dimensional corner path.");
Svc.PluginInterface.Busy=true;
Check(service.Run(cell) && Svc.PluginInterface.Moves==1,"A running owned path is observed rather than restarted.");
Svc.PluginInterface.Busy=false; Svc.Objects.LocalPlayer.Position=new(2,1,2);
Check(!service.Run(cell) && service.Finished,"Endpoint arrival and housing identity release the path for cell interaction.");
Check(!service.Run(cell) && Svc.PluginInterface.Moves==1,"The same route is not replayed for each remaining cell in a visit.");
Check(Svc.PluginInterface.HomeRequests==1,"Remaining cells in one visit must not request another home teleport.");
service=new(route); Svc.Objects.LocalPlayer.Position=new(20,0,20);
Check(service.Run(cell) && Svc.PluginInterface.Travel=="home","An off-route start uses the configured Lifestream arrival command.");
service.TeleportFinished();
Check(service.Run(cell),"An unsettled post-teleport position must wait instead of immediately failing the first cell.");
var arrivalsBefore=Svc.PluginInterface.HomeRequests;
Check(service.Run(cell) && Svc.PluginInterface.HomeRequests==arrivalsBefore,"Arrival verification must not start another teleport.");
service.ArrivalTimedOut();
try { service.Run(cell); throw new Exception("An invalid teleport destination must not start a cross-zone walk."); }
catch(InvalidOperationException) {checks++;}
var rejectedRequests=Svc.PluginInterface.HomeRequests;
try { service.Run(cell); throw new Exception("A failed arrival must not restart the same visit's teleport."); }
catch(InvalidOperationException) {Check(Svc.PluginInterface.HomeRequests==rejectedRequests,"A failed facility is not teleported to repeatedly during one visit");}

service=new(route); Svc.Objects.LocalPlayer.Position=new(2,1,2); Svc.PluginInterface.Travel="";
Check(service.Run(cell) && Svc.PluginInterface.Travel=="home","A new visit must teleport even when already standing beside the garden.");
service=new(route);Svc.PluginInterface.HouseOverride=1;Svc.PluginInterface.TravelMode=null;
Check(service.Run(cell) && Svc.PluginInterface.TravelMode==0,"A walk-to-door override must also be bypassed because the saved path starts at the teleport spawn.");
service=new(route);service.RemoveRoute();Svc.PluginInterface.HouseOverride=2;
Check(service.Run(cell) && Svc.PluginInterface.TravelMode==2,"Visits without a recorded outdoor walk retain the existing home-entry command for indoor pots.");
service=new(route);Svc.Objects.LocalPlayer.Position=new(20,0,20);
service.Run(cell);service.TeleportFinished();
Check(service.Run(cell),"A delayed arrival keeps the same cell pending");
var recoverRequests=Svc.PluginInterface.HomeRequests;var recoverMoves=Svc.PluginInterface.Moves;
Svc.Objects.LocalPlayer.Position=Vector3.Zero;
Check(service.Run(cell) && Svc.PluginInterface.HomeRequests==recoverRequests && Svc.PluginInterface.Moves==recoverMoves+1,"Once arrival information settles, the same visit starts the saved walk without another teleport or skipped cell");
service=new HarvestService(route);service.MarkFailed();var failedHomeRequests=Svc.PluginInterface.HomeRequests;
try{service.Run(cell);throw new Exception("A failed group restarted travel");}catch(InvalidOperationException){checks++;}
Check(Svc.PluginInterface.HomeRequests==failedHomeRequests,"A failed route cannot teleport again for another cell during the same visit");
var pot=new GardenCell{Group="indoor A",Kind="pot",World=1,Territory=20,House=3};
var otherPot=new GardenCell{Group="indoor B",Kind="pot",World=1,Territory=20,House=3};
service=new(route);service.RemoveRoute();Svc.PluginInterface.HouseOverride=0;
var beforeIndoor=Svc.PluginInterface.HomeRequests;
Check(service.Run(pot) && Svc.PluginInterface.TravelMode==2,"Indoor visits explicitly enter the house even when the default is teleport-only");
service.CurrentKind="pot";service.TeleportFinished();
Check(!service.Run(pot) && !service.Run(otherPot) && Svc.PluginInterface.HomeRequests==beforeIndoor+1,
    "Two independently named pots in the same house share one indoor arrival");
service.CurrentKind="plot";
Check(service.Run(pot) && Svc.PluginInterface.HomeRequests==beforeIndoor+2,
    "Returning indoors after an outdoor task must reacquire indoor arrival instead of trusting the old cache");
service=new(route);service.RemoveRoute();service.Run(pot);service.TeleportFinished();
var delayedIndoorRequests=Svc.PluginInterface.HomeRequests;
Check(service.Run(pot) && service.Run(pot) && Svc.PluginInterface.HomeRequests==delayedIndoorRequests,
    "Lifestream can finish its entrance confirmation before the indoor zone loads; wait without another teleport");
service.CurrentKind="pot";
Check(!service.Run(pot) && !service.Run(otherPot) && Svc.PluginInterface.HomeRequests==delayedIndoorRequests,
    "A settled indoor arrival releases both pots after the same pending teleport");
service=new(route);service.RemoveRoute();service.Run(pot);service.TeleportFinished();service.Run(pot);service.ArrivalTimedOut();
try{service.Run(pot);throw new Exception("Wrong indoor house accepted");}catch(InvalidOperationException){checks++;}
service=new(route);service.InRegisteredPlot=false;Svc.Objects.LocalPlayer.Position=Vector3.Zero;
var beforeWalkRequests=Svc.PluginInterface.HomeRequests;
service.Run(cell);service.TeleportFinished();service.Run(cell);
Svc.PluginInterface.Busy=true;
Check(service.Run(cell) && Svc.PluginInterface.HomeRequests==beforeWalkRequests+1,
    "A recorded walk may pass outside the registered plot while its Lifestream path is still running");
Svc.PluginInterface.Busy=false;Svc.Objects.LocalPlayer.Position=route.Points[^1].Position;service.InRegisteredPlot=true;
Check(!service.Run(cell) && Svc.PluginInterface.HomeRequests==beforeWalkRequests+1,
    "Finishing an outdoor walk must keep its arrival record instead of teleporting again before interaction");
Console.WriteLine($"PASS {checks} actual garden walk visit checks; no native game actions executed.");
