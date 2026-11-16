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
service.TeleportFinished();
Check(service.Run(cell),"A recorded ground route starts from its saved arrival context.");
Check(Svc.PluginInterface.Moves==1 && Svc.PluginInterface.IgnoreY==true,"Recorded stairs must use Lifestream ground movement; false selects flying behavior and freezes uphill walking.");
Check(Svc.PluginInterface.Path!.SequenceEqual(route.Points.Select(p=>p.Position)),"Ground mode preserves the recorded three-dimensional corner path.");
Svc.PluginInterface.Busy=true;
Check(service.Run(cell) && Svc.PluginInterface.Moves==1,"A running owned path is observed rather than restarted.");
Svc.PluginInterface.Busy=false; Svc.Objects.LocalPlayer.Position=new(2,1,2);
Check(!service.Run(cell) && service.Finished,"Endpoint arrival and housing identity release the path for cell interaction.");
Check(!service.Run(cell) && Svc.PluginInterface.Moves==1,"The same route is not replayed for each remaining cell in a visit.");
service=new(route); Svc.Objects.LocalPlayer.Position=new(20,0,20);
Check(service.Run(cell) && Svc.PluginInterface.Travel=="home","An off-route start uses the configured Lifestream arrival command.");
service.TeleportFinished();
try { service.Run(cell); throw new Exception("An invalid teleport destination must not start a cross-zone walk."); }
catch(InvalidOperationException) {checks++;}
service=new(route); Svc.Objects.LocalPlayer.Position=new(2,1,2); Svc.PluginInterface.Travel="";
Check(service.Run(cell) && Svc.PluginInterface.Travel=="home","A new visit must teleport even when already standing beside the garden.");
Console.WriteLine($"PASS {checks} actual garden walk visit checks; no native game actions executed.");
