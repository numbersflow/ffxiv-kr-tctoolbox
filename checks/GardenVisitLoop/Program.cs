using Harvest.Planning;
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) => {
    var path=Path.Combine(Environment.GetEnvironmentVariable("DALAMUD_HOME") ?? "", name.Name+".dll");
    return File.Exists(path)?System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path)):null;
};
int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
VisitDriver Ordinary(int required=1,bool ripe=false,bool flower=false) {
    var d=new VisitDriver();
    d.Catalog.Crops[100]=new(100,101,"crop",flower,24,flower?[14011u]:[],1);
    d.Catalog.SeedCrops[101]=100;d.Catalog.Names[100]="crop";
    var c=new GardenCell{Index=0,Kind=flower?"pot":"plot",Seed=101,Owned=ripe,Crop=ripe?100u:0,State=ripe?"mature":"empty",Observed=DateTime.UtcNow,Planted=DateTime.UtcNow.AddDays(-2),Tended=DateTime.UtcNow};
    d.Profile.Cells=[c];d.Profile.Goals=[new(){Item=100,Quantity=required}];d.Profile.Batch=new(){Goal=100,Output=100,Required=required,SeedA=101,Cells=[c.Id]};
    d.Inventory=new(){[101]=2,[16026]=2,[14011]=2};d.Initialize();return d;
}
var run=Ordinary(flower:true);Check(run.Start(),"Flower visit starts with all prepared materials");var result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Plant")==1 && run.Calls.Count(c=>c.Action=="Fertilize")==1 && run.Teleports==1,"Planting and immediately due flower color complete in the same visit");
Check(result.Completed==1 && result.Progress==2,"Multiple verified actions on one cell retain correct progress and cell counts");
run=Ordinary(2,ripe:true);Check(run.Start(),"Ripe visit starts");result=run.Run();
Check(run.Calls.Count(c=>c.Action=="Harvest")==1 && run.Calls.Count(c=>c.Action=="Plant")==1 && run.Teleports==1 && result.Result=="Succeeded","Harvest replans the remaining target and replants in one visit");
run=Ordinary(1,ripe:true);run.Profile.Goals.Add(new(){Item=200,Quantity=1});run.Catalog.Crops[200]=new(200,201,"next",false,24,[]);run.Catalog.SeedCrops[201]=200;run.Inventory[201]=1;run.Initialize();run.Start();result=run.Run();
Check(run.Profile.Batch?.Goal==200 && run.Calls.Count(c=>c.Action=="Plant")==1 && run.Teleports==1,"Finishing one target plants the next prepared target without leaving for another job");
run=Ordinary();var first=run.Profile.Cells[0];first.Owned=true;first.State="growing";first.Crop=100;first.Planted=DateTime.UtcNow;first.Tended=DateTime.UtcNow.AddHours(-13);
var second=new GardenCell{Index=1,Owned=true,State="growing",Seed=101,Crop=100,Planted=DateTime.UtcNow,Tended=DateTime.UtcNow};run.Profile.Cells.Add(second);run.Profile.Batch!.Cells.Add(second.Id);run.Profile.Goals[0].Quantity=2;run.Profile.Batch.Required=2;run.Patches.Clear();run.Initialize();run.Start();result=run.Run();
Check(run.Calls.Count(c=>c.Action=="Tend")==2 && run.Teleports==1,"A due care visit sweeps both owned cells and synchronizes their care times");
run=Ordinary(flower:true);first=run.Profile.Cells[0];first.Owned=true;first.State="growing";first.Crop=100;first.Planted=DateTime.UtcNow;first.Tended=DateTime.UtcNow;run.Inventory[14011]=0;run.Initialize();
Check(!run.Ready() && run.Supplies.GetValueOrDefault(14011u)==1,"A missing color material requests preparation instead of repeated empty visits");
run=Ordinary(2);second=new(){Index=1,State="empty",Seed=101};run.Profile.Cells.Add(second);run.Profile.Batch!.Cells.Add(second.Id);run.Inventory[16026]=1;run.Initialize();
Check(!run.Ready() && !run.Start(),"Two-cell planting waits for both soils and rejects a stale start instead of making a partial visit");
run=Ordinary();run.Start();while(run.Calls.Count==0)run.Tick();run.GardenBusy=true;run.Cancel();
Check(run.Tick().Busy,"Cancellation waits for the current garden action to settle");run.GardenBusy=false;result=run.Run();
Check(result.Result=="Cancelled" && run.Profile.Cells[0].Pending=="Plant","Cancelled unverified planting retains its recovery record");
Check(run.Start(),"A later visit recovers the pending result");result=run.Run();
Check(run.Profile.Cells[0].Owned && run.Calls.Count(c=>c.Action=="Plant")==1,"Recovery confirms planting without sending a second seed");
run=new();
run.Catalog.Crops[10]=new(10,11,"parent A",false,72,[]);run.Catalog.Crops[20]=new(20,21,"parent B",false,72,[]);run.Catalog.Crops[30]=new(30,31,"next crop",false,72,[]);
run.Catalog.SeedCrops=new(){[11]=10,[21]=20,[31]=30};
var producer=new GardenCell{Index=0,Owned=true,Seed=11,Crop=10,State="mature",Observed=DateTime.UtcNow,Tended=DateTime.UtcNow};
var anchor=new GardenCell{Index=1,Owned=true,Seed=21,Crop=20,State="mature",Observed=DateTime.UtcNow,Tended=DateTime.UtcNow.AddHours(-13)};
run.Profile.Cells=[anchor,producer];run.Profile.Goals=[new(){Item=31,Quantity=1}];run.Profile.Batch=new(){Goal=31,Output=31,SeedA=11,SeedB=21,Required=1,Cross=true,Cells=[producer.Id,anchor.Id]};
run.Inventory=new(){[11]=1,[21]=1,[7766]=2};run.Rewards[1]=new(){[31]=1};run.Initialize();
Check(run.Ready() && run.Start(),"Mature crossing parents can be managed");result=run.Run();
Check(run.Calls.Count(c=>c.Action=="Harvest")==2 && run.Calls.Any(c=>c.Id==2&&c.Action=="Tend") && run.Teleports==1 && run.Profile.Batch==null,
    "A tended anchor is revisited and harvested after its partner produces the required seed; the whole crossing clears in one visit");
Check(!run.CachedReady,"Terminal status invalidates the previous ready snapshot before another job can start the stale visit");
run=Ordinary();first=run.Profile.Cells[0];first.Owned=true;first.State="growing";first.Crop=100;first.Planted=DateTime.UtcNow;first.Tended=DateTime.UtcNow.AddHours(-13);run.Patches.Clear();run.Initialize();run.Start(careOnly:true);result=run.Run();
Check(run.Calls.All(c=>c.Action=="Tend"),"Care-only recovery does not plant, harvest or consume color material");
run=Ordinary(1,ripe:true);run.Free=0;run.Start();result=run.Run();
Check(result.Result=="Partial" && result.Phase.StartsWith("Gardening:BagSpace/") && run.Calls.All(c=>c.Action!="Harvest"),"Insufficient bag capacity preserves the ripe crop without uncertain harvesting");
run=Ordinary();run.Start();run.Pause(true);var pausedCalls=run.Calls.Count;
Check(run.Tick().Busy && run.Calls.Count==pausedCalls,"Pause does not start a new garden action");run.Pause(false);result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Plant")==1,"Resume finishes the same planned planting once");
run=Ordinary();run.Start();run.ChangeCharacter(99);result=run.Tick();
Check(result.Busy && result.Phase=="Gardening:Owner" && run.Calls.Count==0,"Character change blocks actions instead of operating another character's garden");run.Disconnect();
run=Ordinary();first=run.Profile.Cells[0];first.Owned=true;first.State="withered";first.Crop=100;first.Tended=DateTime.UtcNow.AddHours(-13);run.Patches.Clear();run.Initialize();
Check(!run.Ready(),"A withered crop cannot create an endless travel loop or automatic discard");
run=Ordinary(1,ripe:true);run.Start();run.Patches[1]=(Harvest.Core.PatchState.Empty,0);run.Inventory[100]=1;result=run.Run();
Check(run.Calls.All(c=>c.Action!="Plant" && c.Action!="Harvest") && run.Profile.Goals[0].Complete,"A manual harvest during an active visit refreshes the goal before any replacement seed is sent");
run=Ordinary();run.Start();run.Patches[1]=(Harvest.Core.PatchState.Ripe,999);result=run.Run();
Check(result.Result=="Partial" && run.Calls.Count==0 && !run.Ready(),"A foreign ripe crop stays protected and cannot trigger a repeated visit loop");
run=Ordinary();first=run.Profile.Cells[0];first.Owned=true;first.State="growing";first.Crop=100;first.Planted=DateTime.UtcNow;first.Tended=DateTime.UtcNow.AddHours(-13);var previousTend=first.Tended;run.Patches.Clear();run.FailTendReceipt=true;run.Initialize();run.Start();result=run.Run();
Check(result.Result=="Partial" && first.Pending=="Tend" && first.Tended==previousTend,"An unconfirmed tending command cannot falsely advance the successful-care clock");
first.RetryAt=DateTime.MinValue;run.FailTendReceipt=false;run.Start();result=run.Run();
Check(first.Pending=="" && first.Tended>previousTend,"Uncertain tending is safely repeated and confirmed before renewing the clock");
run=Ordinary(2,ripe:true);run.Catalog.Crops[200]=new(200,201,"missing parent",false,72,[]);run.Catalog.SeedCrops[201]=200;
var missingParent=new GardenCell{Index=1,Seed=201,State="empty",Observed=DateTime.UtcNow};run.Profile.Cells.Add(missingParent);run.Profile.Batch!.Cross=true;run.Profile.Batch.SeedB=201;run.Profile.Batch.Cells.Add(missingParent.Id);run.Inventory[201]=0;run.Inventory[7766]=0;run.Initialize();
Check(!run.Ready(),"A ripe producer waiting for its missing crossing partner cannot cause an endless tending/teleport loop");
run=Ordinary(1,ripe:true);first=run.Profile.Cells[0];first.Pending="Tend";run.Start();run.Patches[1]=(Harvest.Core.PatchState.Empty,0);run.Inventory[100]=1;result=run.Run();
Check(run.Profile.Goals[0].Complete && run.Calls.Count==0,"Manual harvest during uncertain tending is reconciled before any new seed is planted");
VisitDriver Parallel(bool secondSeed=true) {
    var d=new VisitDriver();
    d.Catalog.Crops[10]=new(10,11,"parent A",false,72,[]);
    d.Catalog.Crops[20]=new(20,21,"parent B",false,72,[]);
    d.Catalog.Crops[30]=new(30,31,"cross goal",false,72,[]);
    d.Catalog.Crops[100]=new(100,101,"flower A",true,24,[14011u]);
    d.Catalog.Crops[200]=new(200,201,"flower B",true,24,[14012u]);
    d.Catalog.SeedCrops=new(){[11]=10,[21]=20,[31]=30,[101]=100,[201]=200};
    var a=new GardenCell{Group="outdoor",Index=0,Owned=true,Seed=11,Crop=10,State="growing",Observed=DateTime.UtcNow,Planted=DateTime.UtcNow,Tended=DateTime.UtcNow};
    var b=new GardenCell{Group="outdoor",Index=1,Owned=true,Seed=21,Crop=20,State="growing",Observed=DateTime.UtcNow,Planted=DateTime.UtcNow,Tended=DateTime.UtcNow};
    d.Profile.Cells=[a,b,new(){Group="pot A",Kind="pot",Index=2,X=2,State="empty"},new(){Group="pot B",Kind="pot",Index=3,X=3,State="empty"}];
    d.Profile.Batch=new(){Goal=31,Output=31,Cross=true,SeedA=11,SeedB=21,Required=1,Cells=[a.Id,b.Id]};
    d.Profile.Goals=[new(){Item=31,Quantity=1},new(){Item=100,Quantity=1},new(){Item=200,Quantity=1}];
    d.Inventory=new(){[101]=1,[201]=secondSeed?1:0,[16026]=2,[14011]=1,[14012]=1};
    d.Initialize();return d;
}
run=Parallel();var originalCross=run.Profile.Batch!;
Check(run.Profile.ActiveBatches.Count()==3 && ReferenceEquals(run.Profile.Batch,originalCross) && run.Profile.ActiveBatches.SelectMany(b=>b.Cells).Distinct().Count()==4,
    "An existing crossing and two distinct flower goals reserve independent cells concurrently");
Check(run.Supplies[16026]==2 && !run.Supplies.ContainsKey(7766) && run.Supplies[101]==1 && run.Supplies[201]==1,
    "Parallel flower supplies use potting soil without mixing the crossing soil or overwriting counts");
Check(run.Ready() && run.Start(),"Flower planting is ready while the outdoor crossing grows");result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Plant")==2 && run.Calls.Count(c=>c.Action=="Fertilize")==2 && run.Calls.All(c=>c.Id>=3),
    "Both indoor flower plans plant and feed in the same visit without visiting the not-due crossing");
Check(run.Profile.Cells.Take(2).All(c=>c.Owned) && ReferenceEquals(run.Profile.Batch,originalCross) && run.Profile.Batch!.Cells.Count==2,
    "Flower progress preserves both parents and the original crossing stage");
run=Parallel(false);Check(run.Ready() && run.Start(),"One unavailable seed must not block another prepared flower plan");result=run.Run();
Check(run.Calls.Any(c=>c.Id==3&&c.Action=="Plant") && run.Calls.All(c=>c.Id!=4) && result.Result=="Partial",
    "A prepared flower completes while the other plan retains its seed request");
run=Parallel(false);run.Profile.Cells[0].Tended=DateTime.UtcNow.AddHours(-13);run.Inventory[101]=0;run.Initialize();run.Start();result=run.Run();
Check(run.Calls.Count(c=>c.Action=="Tend")==2 && run.Calls.All(c=>c.Id<3),"Missing flower supplies cannot block due care of the outdoor parents");
run=Parallel();run.Start();while(!run.Calls.Any(c=>c.Action=="Plant"))run.Tick();run.Cancel();result=run.Run();
var saved=System.Text.Json.JsonSerializer.Serialize(run.Profile);
var restored=new VisitDriver{Profile=System.Text.Json.JsonSerializer.Deserialize<HarvestProfile>(saved)!,Catalog=run.Catalog,Inventory=new(run.Inventory),Patches=new(run.Patches)};
restored.Initialize();Check(restored.Profile.ActiveBatches.Count()==3 && restored.Profile.Cells.Any(c=>c.Pending=="Plant"),
    "Reload persists all plans, the crossing parents and the unverified indoor planting");
restored.Start();result=restored.Run();
Check(restored.Profile.Cells.Skip(2).All(c=>c.Owned) && restored.Calls.Count(c=>c.Action=="Plant")==1,
    "Reload confirms the first pot without resending its seed, then plants the second pot");
run=Parallel();run.Start();run.Run();
var ripePot=run.Profile.Cells[2];ripePot.State="mature";ripePot.Observed=DateTime.UtcNow;run.Patches[3]=(Harvest.Core.PatchState.Ripe,100);
run.Start();result=run.Run();
Check(run.Profile.Goals.Single(g=>g.Item==100).Complete && run.Profile.ActiveBatches.Count()==2 && run.Profile.Batch!.Cross && run.Profile.Cells[3].Owned,
    "Harvesting one flower retires only its plan while the crossing and other flower remain active");
run=Parallel();
run.Profile.Cells.AddRange(Enumerable.Range(4,4).Select(i=>new GardenCell{Group="outdoor",Index=i,State="empty"}));
run.Catalog.Crops[400]=new(400,401,"other outdoor crop",false,24,[]);run.Catalog.SeedCrops[401]=400;
run.Profile.Goals.Add(new(){Item=400,Quantity=2});run.Inventory[401]=2;run.Initialize();
Check(run.Profile.ActiveBatches.Count()==3 && run.Profile.Cells.Skip(4).All(c=>run.Profile.BatchFor(c)==null),
    "A crossing bed cannot take an unrelated neighboring planting that could contaminate its seed result");
run=Parallel();run.Catalog.Crops[500]=new(500,501,"waiting flower",true,24,[]);run.Catalog.SeedCrops[501]=500;
run.Profile.Goals.Add(new(){Item=500,Quantity=1});run.Inventory[501]=1;run.Initialize();
Check(run.Error.Length==0 && run.Profile.ActiveBatches.Count()==3,"Waiting for occupied pots must not report that indoor pots are unregistered");
Console.WriteLine($"PASS {checks} actual visit/planner lifecycle checks; game, movement and inventory IPC are simulated.");
