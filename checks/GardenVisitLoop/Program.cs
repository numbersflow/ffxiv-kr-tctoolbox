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
run.Profile.Cells.AddRange(Enumerable.Range(2,6).Select(i=>new GardenCell{Group="outdoor",Index=i,State="empty"}));
run.Catalog.Crops[400]=new(400,401,"other outdoor crop",false,24,[]);run.Catalog.SeedCrops[401]=400;
run.Profile.Goals.Add(new(){Item=400,Quantity=6});run.Inventory[401]=6;run.Initialize();
Check(run.Profile.ActiveBatches.Count()==4 && run.Profile.Cells.Skip(4).Where(c=>run.Profile.BatchFor(c)!=null).Select(c=>c.Index).SequenceEqual(new[]{2,3,4,5,6,7}),
    "A short crop uses all six empty slots during a confirmed long crossing growth window");
Check(run.Profile.Batch!.Cross && run.Profile.Cells.Take(2).All(c=>c.Owned) && run.Supplies[401]==6 && run.Supplies[16026]==8,
    "The extra crop reserves its actual free capacity without overwriting parents or the two flower plans");
run=Parallel();run.Catalog.Crops[500]=new(500,501,"waiting flower",true,24,[]);run.Catalog.SeedCrops[501]=500;
run.Profile.Goals.Add(new(){Item=500,Quantity=1});run.Inventory[501]=1;run.Initialize();
Check(run.Error.Length==0 && run.Profile.ActiveBatches.Count()==3,"Waiting for occupied pots must not report that indoor pots are unregistered");
VisitDriver.SetConditions(Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted,Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight);
Check(!VisitDriver.TryGetBusyReason(out _,allowMountedTravel:true) && VisitDriver.TryGetBusyReason(out _),
    "The workflow may acquire its home-travel phase while mounted; direct crop automation still rejects mounted interaction");
foreach(var blocked in new[]{Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat,Dalamud.Game.ClientState.Conditions.ConditionFlag.Fishing,
    Dalamud.Game.ClientState.Conditions.ConditionFlag.Crafting,Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas,
    Dalamud.Game.ClientState.Conditions.ConditionFlag.Diving,Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell}) {
    VisitDriver.SetConditions(blocked);
    Check(VisitDriver.TryGetBusyReason(out _,allowMountedTravel:true),"Travel admission retains the safety gate for "+blocked);
}
VisitDriver.SetConditions();
VisitDriver TimedCross() {
    var d=new VisitDriver();
    d.Catalog.Crops[10]=new(10,11,"parent A",false,96,[]);d.Catalog.Crops[20]=new(20,21,"parent B",false,96,[]);
    d.Catalog.Crops[30]=new(30,31,"cross goal",false,96,[]);d.Catalog.Crops[400]=new(400,401,"short crop",false,24,[]);
    d.Catalog.SeedCrops=new(){[11]=10,[21]=20,[31]=30,[401]=400};
    d.Profile.Cells=Enumerable.Range(0,8).Select(i=>new GardenCell{Index=i,Group="outdoor",State="empty",Observed=DateTime.UtcNow}).ToList();
    var a=d.Profile.Cells[0];var b=d.Profile.Cells[1];var timed=d.Profile.Cells[7];
    a.Seed=11;b.Seed=21;b.Owned=true;b.Crop=20;b.State="growing";b.Planted=b.Tended=DateTime.UtcNow;
    timed.Seed=401;timed.Crop=400;timed.Owned=true;timed.State="growing";timed.Planted=timed.Tended=DateTime.UtcNow;
    d.Profile.Batch=new(){Goal=31,Output=31,Cross=true,SeedA=11,SeedB=21,Required=1,Cells=[a.Id,b.Id]};
    d.Profile.AdditionalBatches=[new(){Goal=400,Output=400,Required=1,SeedA=401,Cells=[timed.Id]}];
    d.Profile.Goals=[new(){Item=31,Quantity=1},new(){Item=400,Quantity=1}];
    d.Inventory=new(){[11]=1,[7766]=1};d.Initialize();return d;
}
run=TimedCross();
Check(!run.Ready() && !run.Start(),"A prepared crossing cannot replant beside an unfinished temporary crop, even if its estimate has expired");
var temporary=run.Profile.Cells[7];temporary.State="mature";run.Patches[8]=(Harvest.Core.PatchState.Ripe,400);
Check(run.Ready() && run.Start(),"The ripe temporary crop makes the blocked crossing visit actionable");result=run.Run();
Check(result.Result=="Succeeded" && run.Profile.Cells[0].Owned && run.Profile.Cells[1].Owned && !temporary.Owned
    && run.Calls.FindIndex(c=>c.Id==8&&c.Action=="Harvest")<run.Calls.FindIndex(c=>c.Id==1&&c.Action=="Plant"),
    "Harvesting the temporary crop frees its neighbor before the crossing seed is planted in the same visit");
VisitDriver TimeCapacity(double hours=24,double elapsed=0,bool started=false) {
    var d=Parallel();
    foreach(var parent in d.Profile.Cells.Take(2))parent.Planted=DateTime.UtcNow.AddHours(-elapsed);
    var extra=Enumerable.Range(2,6).Select(i=>new GardenCell{Group="outdoor",Index=i,State=started?"growing":"empty",Seed=401,
        Crop=started?400u:0u,Owned=started,Observed=DateTime.UtcNow,Planted=started?DateTime.UtcNow:DateTime.MinValue,Tended=DateTime.UtcNow}).ToArray();
    d.Profile.Cells.AddRange(extra);d.Catalog.Crops[400]=new(400,401,"temporary crop",false,hours,[]);d.Catalog.SeedCrops[401]=400;
    d.Profile.Goals.Add(new(){Item=400,Quantity=6});d.Inventory[401]=6;
    if(started)d.Profile.AdditionalBatches.Add(new(){Goal=400,Output=400,Required=6,SeedA=401,Cells=extra.Select(c=>c.Id).ToList()});
    d.Initialize();return d;
}
run=TimeCapacity(96);
Check(run.Profile.ActiveBatches.Single(b=>b.Goal==400).Cells.Count==4,
    "A crop longer than the crossing window may use distant slots but cannot reserve the two adjacent slots");
run=TimeCapacity();
foreach(var parent in run.Profile.Cells.Take(2))parent.Planted=DateTime.UtcNow.AddHours(-60);
run.Initialize();
Check(run.Profile.ActiveBatches.Single(b=>b.Goal==400).Cells.Count==4 && run.Profile.Cells.Skip(4).Where(c=>c.Index is 2 or 7).All(c=>run.Profile.BatchFor(c)==null),
    "Delayed procurement releases unplanted temporary reservations when their harvest no longer fits before the crossing");
run=TimeCapacity(elapsed:60,started:true);
Check(run.Profile.ActiveBatches.Single(b=>b.Goal==400).Cells.Count==6 && run.Profile.Cells.Skip(4).All(c=>c.Owned),
    "An already planted crop is preserved when higher-priority work outlasts the forecast");
run=TimeCapacity(0);
Check(run.Profile.ActiveBatches.Single(b=>b.Goal==400).Cells.Count==4,"Unknown grow time never qualifies for borrowing an adjacent crossing slot");
run=TimeCapacity();var budgetNow=DateTime.UtcNow;
foreach(var parent in run.Profile.Cells.Take(2))parent.Planted=budgetNow;
Check(GardenPolicy.FitsGrowthWindow(run.Profile.Batch!,run.Profile.Cells,run.Catalog.Crops,run.Catalog.SeedCrops,71,budgetNow)
    && !GardenPolicy.FitsGrowthWindow(run.Profile.Batch!,run.Profile.Cells,run.Catalog.Crops,run.Catalog.SeedCrops,72,budgetNow),
    "Forecast reserves an hour for travel and timed work rather than filling the entire remaining growth time");
run.Profile.Cells.Single(c=>c.Id==run.Profile.Batch!.Cells[0]).State="mature";
Check(!GardenPolicy.FitsGrowthWindow(run.Profile.Batch!,run.Profile.Cells,run.Catalog.Crops,run.Catalog.SeedCrops,1,budgetNow),
    "Observed early maturity overrides the base-duration estimate and closes the temporary planting window");
run=Parallel();run.Profile.Cells[0].Tended=DateTime.UtcNow.AddHours(-13);
run.StockUnavailable=true;run.BagUnavailable=true;
Check(run.Start(careOnly:true),"Due care starts while procurement route and bag snapshots are unavailable");result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Tend")==2 && run.Calls.All(c=>c.Action=="Tend"),
    "Care-only work ignores empty planting plans and cannot be blocked by their procurement or inventory reads");
run=Ordinary();run.StockUnavailable=true;run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Profile.Cells[0].Owned,"Prepared planting and its consumption receipt do not wait for GBR procurement route refreshes");
run=Ordinary();run.BagUnavailable=true;run.Start();result=run.Tick();
Check(result.Busy && run.Calls.Count==0,"A loading inventory waits before consuming a prepared seed");
run.Pause(true);run.AgePausedWait(60);run.Pause(false);result=run.Tick();
Check(result.Busy && run.Calls.Count==0,"Pause does not consume the inventory loading deadline");
run.BagUnavailable=false;result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Plant")==1,"Fresh bag data resumes the same owned planting once");
run=Ordinary();run.BagUnavailable=true;run.Start();run.Tick();run.Cancel();result=run.Run();
Check(result.Result=="Cancelled" && run.Calls.Count==0,"Timed cancellation during inventory loading starts no seed or foreign queue");
run=Ordinary();run.BagUnavailable=true;run.Start();run.Tick();run.ExpireInventoryWait();result=run.Tick();
Check(!result.Busy && result.Result=="Partial" && run.Calls.Count==0,"A permanently unavailable bag cannot hold the workflow indefinitely");
run=Parallel();run.Profile.Cells[0].Tended=DateTime.UtcNow.AddHours(-13);run.Profile.Cells[2].Pending="Plant";
run.StockUnavailable=true;run.BagUnavailable=true;run.Start(careOnly:true);result=run.Run();
Check(result.Result=="Succeeded" && run.Profile.Cells[2].Pending=="Plant" && run.Calls.All(c=>c.Action=="Tend"),
    "Care-only recovery leaves another pot's uncertain planting receipt untouched");
VisitDriver Crossing(bool existing=false) {
    var d=new VisitDriver();
    d.Catalog.Crops[10]=new(10,11,"fast parent",false,24,[]);d.Catalog.Crops[20]=new(20,21,"long parent",false,96,[]);
    d.Catalog.Crops[30]=new(30,31,"crossed seed crop",false,96,[]);d.Catalog.SeedCrops=new(){[11]=10,[21]=20,[31]=30};
    d.Catalog.Crosses=[new(31,21,11)];d.UnavailableSeeds=[31];
    var a=new GardenCell{Index=0,State="empty",Observed=DateTime.UtcNow};var b=new GardenCell{Index=1,State="empty",Observed=DateTime.UtcNow};
    d.Profile.Cells=[a,b];d.Profile.Goals=[new(){Item=31,Quantity=1}];d.Inventory=new(){[11]=1,[21]=1,[7766]=2};
    if(existing){
        a.Seed=21;a.Crop=20;a.Owned=true;a.State="growing";a.Planted=DateTime.UtcNow.AddHours(-25);a.Tended=DateTime.UtcNow;
        b.Seed=11;b.Crop=10;b.Owned=true;b.State="mature";b.Planted=DateTime.UtcNow.AddHours(-24);b.Tended=DateTime.UtcNow;
        d.Profile.Batch=new(){Goal=31,Output=31,Cross=true,SeedA=21,SeedB=11,Required=1,Cells=[a.Id,b.Id]};
        d.Rewards[2]=new(){[31]=1};
    }
    d.Initialize();return d;
}
run=Crossing();Check(run.Start(),"A fully prepared new crossing starts");result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.First(c=>c.Action=="Plant").Id==2 && run.Profile.Cells[1].Seed==21 && run.Profile.Cells[0].Seed==11,
    "The long-lived anchor is planted first, then the fast producer is planted beside it for the first possible crossed seed");
run=Crossing(existing:true);
Check(run.Profile.Batch!.Cells[1]==run.Profile.Cells[0].Id,"An existing crossing retains the parent actually planted first");
run.Start();result=run.Run();
Check(run.Calls.Any(c=>c.Id==2&&c.Action=="Harvest") && !run.Calls.Any(c=>c.Id==1&&c.Action=="Harvest") && run.Inventory[31]==1,
    "The later-planted fast parent is harvested as soon as ready instead of waiting for the uncrossed first parent's long maturity");
run=Ordinary();run.Start();while(!run.Calls.Any(c=>c.Action=="Plant"))run.Tick();run.BagUnavailable=true;result=run.Run();
var confirmedPlantTime=run.Profile.Cells[0].Planted;
Check(run.Profile.Cells[0].Owned && run.Profile.Cells[0].Pending=="Plant","A verified crop with an unavailable consumption receipt keeps its recovery record");
run.BagUnavailable=false;run.Profile.Cells[0].RetryAt=DateTime.MinValue;run.Start();run.Run();
Check(run.Profile.Cells[0].Planted==confirmedPlantTime && run.Calls.Count(c=>c.Action=="Plant")==1,
    "Recovering a consumption receipt preserves the already confirmed planting time and never resends the seed");
run=TimeCapacity();var producerCell=run.Profile.Cells.Single(c=>c.Id==run.Profile.Batch!.Cells[0]);var anchorCell=run.Profile.Cells.Single(c=>c.Id==run.Profile.Batch!.Cells[1]);
anchorCell.State="mature";producerCell.Planted=DateTime.UtcNow;
Check(GardenPolicy.FitsGrowthWindow(run.Profile.Batch!,run.Profile.Cells,run.Catalog.Crops,run.Catalog.SeedCrops,24,DateTime.UtcNow),
    "A retained mature anchor does not close the fast producer's next growth window");
run=Crossing(existing:true);run.Profile.Goals[0].Item=30;run.Profile.Batch!.Goal=30;run.Inventory[16026]=1;run.Initialize();
run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Profile.ActiveBatches.Count()==2 && run.Profile.Cells[0].Owned && run.Profile.Cells[1].Owned
    && run.Profile.Cells[1].Seed==31 && run.Profile.ActiveBatches.SelectMany(b=>b.Cells).Distinct().Count()==2,
    "A produced ancestor seed starts the next growing stage before the retained long parent matures, without sharing cell ownership");
run=Ordinary(2,ripe:true);run.Profile.Cells[0].Group="outdoor";
run.Catalog.Crops[200]=new(200,201,"indoor flower",true,24,[]);run.Catalog.SeedCrops[201]=200;
var indoorReady=new GardenCell{Group="indoor",Index=1,Kind="pot",Seed=201,Crop=200,Owned=true,State="mature",Observed=DateTime.UtcNow,Planted=DateTime.UtcNow.AddDays(-2),Tended=DateTime.UtcNow};
run.Profile.Cells.Add(indoorReady);run.Profile.Goals.Add(new(){Item=200,Quantity=2});
run.Profile.AdditionalBatches.Add(new(){Goal=200,Output=200,Required=2,SeedA=201,Cells=[indoorReady.Id]});run.Inventory[201]=2;run.Initialize();
run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Teleports==2 && run.Calls.FindIndex(c=>c.Id==1&&c.Action=="Plant")<run.Calls.FindIndex(c=>c.Id==2&&c.Action=="Harvest"),
    "A prepared outdoor replant finishes before travelling indoors, so two facilities need two arrivals rather than four");
run=Ordinary(2);run.Profile.Cells.Add(new(){Index=1,State="empty",Observed=DateTime.UtcNow});run.Initialize();
Check(run.Profile.Batch!.Cells.Count==2 && run.Supplies[101]==2,"An existing incomplete crop goal expands into a newly available plot instead of leaving it idle");
run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Calls.Count(c=>c.Action=="Plant")==2,"Expanded reservations are planted in the same owned visit");
run=new();run.Catalog.Crops[100]=new(100,101,"food",false,24,[]);run.Catalog.Crops[200]=new(200,201,"flower",true,24,[]);
run.Catalog.SeedCrops=new(){[101]=100,[201]=200};run.Profile.Cells=Enumerable.Range(0,4).Select(i=>new GardenCell{Index=i,Kind=i==3?"pot":"plot",State="empty",Observed=DateTime.UtcNow}).ToList();
run.Profile.Goals=[new(){Item=100,Quantity=4},new(){Item=200,Quantity=1}];run.Inventory=new(){[101]=4,[201]=1,[16026]=4};run.Initialize();
Check(run.Profile.ActiveBatches.Count()==2 && run.Profile.BatchFor(run.Profile.Cells[3])?.Goal==200,
    "Ordinary food cannot occupy the only indoor pot while a selected flower goal needs it");
run=Ordinary(2);run.Profile.Cells.AddRange(new GardenCell[]{new(){Index=1,State="empty"},new(){Index=2,State="empty"}});
run.Profile.Cells[0].Enabled=false;run.Initialize();
Check(run.Profile.Batch!.Cells.Count==2 && !run.Profile.Batch.Cells.Contains(run.Profile.Cells[0].Id),
    "A disabled empty reservation is released so enabled free plots can meet the target");
run=Crossing(existing:true);var oldFast=run.Profile.Cells[0];var oldLong=run.Profile.Cells[1];
oldFast.Seed=11;oldFast.Crop=10;oldFast.State="mature";oldLong.Seed=21;oldLong.Crop=20;oldLong.State="growing";
run.Profile.Batch!.SeedA=11;run.Profile.Batch.SeedB=21;run.Patches.Clear();run.Initialize();run.Start();result=run.Run();
Check(run.Profile.Batch!.Cells[1]==oldLong.Id && run.Calls.Any(c=>c.Id==1&&c.Action=="Harvest") && run.Calls.Any(c=>c.Id==1&&c.Action=="Plant")
    && run.Calls.All(c=>c.Id!=2||c.Action!="Harvest"),
    "A legacy fast parent planted first is harvested and reused beside the long parent instead of being held for the long parent's full duration");
run=Ordinary();run.Start();run.Patches[1]=(Harvest.Core.PatchState.Ripe,999);run.Run();
run.Profile.Cells.Add(new(){Index=1,State="empty",Observed=DateTime.UtcNow});run.Initialize();run.Start();result=run.Run();
Check(run.Profile.Cells[1].Owned && run.Calls.All(c=>c.Id!=1),"A foreign crop releases only its unusable reservation so another empty plot can continue the same target");
run=Crossing(existing:true);run.Profile.Cells.RemoveAt(0);run.Initialize();
Check(run.Profile.ActiveBatches.All(b=>b.Cells.All(id=>run.Profile.Cells.Any(c=>c.Id==id))) && run.Profile.Batch!.Clearing && run.Ready(),
    "Removing a crossing parent retires only that stage and releases the remaining ripe parent's verified harvest");
run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Inventory[31]==1 && run.Calls.All(c=>c.Id!=1),
    "An unregistered parent is never touched while its remaining crossed crop can complete the target");
run=Crossing(existing:true);run.Profile.Cells[0].Enabled=false;run.Initialize();run.Start();result=run.Run();
Check(result.Result=="Succeeded" && run.Profile.Cells[0].Owned && run.Calls.All(c=>c.Id!=1) && !run.Ready(),
    "Disabling a parent preserves its crop record and cannot leave a perpetual actionable crossing or block the other parent's harvest");
VisitDriver Layout(int count, params int[] empty) {
    var d=Crossing(); d.Profile.SetBatches([]);
    d.Profile.Cells=Enumerable.Range(0,count).Select(i=>new GardenCell{Index=i,State=empty.Contains(i)?"empty":"growing",
        Crop=empty.Contains(i)?0u:999u,Observed=DateTime.UtcNow}).ToList();
    return d;
}
run=Layout(4,0,3);foreach(var c in run.Profile.Cells.Where(c=>c.Index is 1 or 2)){c.Crop=0;c.State="empty";c.Enabled=false;}run.Initialize();
Check(run.Profile.Batch is {Cross:true} && run.Profile.Batch.Cells.Count==2,
    "The last and first slots in a four-slot bed form a valid wraparound crossing pair");
run=Layout(8,0,1,3,4,5,6);run.Initialize();
Check(run.Profile.Batch is {Cross:true} && run.Profile.Batch.Cells.All(id=>run.Profile.Cells.Single(c=>c.Id==id).Index is 4 or 5),
    "Choose an uncontaminated pair instead of reserving the first empty pair beside foreign crops");
run=Layout(8);run.Initialize();
Check(!run.Profile.ActiveBatches.Any() && run.Supplies.Count==0 && !run.Ready(),
    "A full bed retains its goal without procurement or a busy visit and never takes over foreign crops");
run.Profile.Cells.ForEach(c=>{c.Crop=0;c.State="empty";});run.Initialize();
Check(run.Profile.Batch is {Cross:true} && run.Profile.Batch.Cells.Count==2,
    "A waiting crossing automatically acquires a pair after space becomes available");
run=Layout(8,0,2);run.Initialize();
Check(!run.Profile.ActiveBatches.Any() && !run.Ready(),
    "Two isolated empty cells are not treated as an adjacent crossing pair");
run=Layout(8,0,1);run.Profile.Cells[7].Enabled=false;run.Initialize();
Check(!run.Profile.ActiveBatches.Any(),
    "A disabled foreign neighbor remains a physical blocker even though its management is disabled");
run=Layout(8,0,1);run.Profile.Cells[7].X=20;run.Profile.Cells[7].Crop=0;run.Profile.Cells[7].State="empty";run.Initialize();
Check(!run.Profile.ActiveBatches.Any(),
    "An incomplete bed registration does not prove a safe crossing topology");

VisitDriver Ladder(double ordinaryHours=48, bool reservedOrdinary=false) {
    var d=new VisitDriver();
    foreach(var (item,seed,hours) in new (uint,uint,double)[]{(10,11,24),(20,21,96),(30,31,24),(40,41,24),(50,51,24),(60,61,24),(70,71,24),(100,101,ordinaryHours)}) {
        d.Catalog.Crops[item]=new(item,seed,"lookahead",false,hours,[]); d.Catalog.SeedCrops[seed]=item;
    }
    d.Catalog.Crosses=[new(31,11,21),new(41,31,51),new(61,41,71)];d.UnavailableSeeds=[31,41,61];
    d.Inventory=new(){[11]=2,[21]=2,[51]=2,[71]=2,[101]=8,[7766]=8,[16026]=8};
    d.Profile.Cells=Enumerable.Range(0,8).Select(i=>new GardenCell{Index=i,State="empty",Observed=DateTime.UtcNow}).ToList();
    d.Profile.Goals=[new(){Item=100,Quantity=8},new(){Item=60,Quantity=1}];
    if(reservedOrdinary){d.Profile.Batch=new(){Goal=100,Output=100,SeedA=101,Required=8,Cells=d.Profile.Cells.Select(c=>c.Id).ToList()};foreach(var c in d.Profile.Cells)c.Seed=101;}
    d.Initialize();return d;
}
run=Ladder();
Check(run.Profile.Batch is {Cross:true,Output:31} && run.Profile.ActiveBatches.Where(b=>!b.Cross).Sum(b=>b.Cells.Count)<6,
    "A later-selected three-stage crossing gets the bed before an earlier ordinary crop that would delay it");
var forecastNow=DateTime.UtcNow;
var ladderPlan=SeedPlanner.Build(61,1,run.Catalog.SeedCrops.Keys.ToDictionary(id=>id,id=>new Stock(run.Inventory.GetValueOrDefault(id),0,!run.UnavailableSeeds.Contains(id))),run.Catalog.Crosses);
var ladderForecast=GardenLookahead.Build(run.Profile,run.Catalog.Crops,run.Catalog.SeedCrops,[ladderPlan],forecastNow);
Check(ladderForecast.Select(r=>r.Output).SequenceEqual(new uint[]{31,41,61}),
    "The forecast allocates actual adjacent pairs for the current, next and next-next crossing stages");
Check(ladderForecast.All(r=>r.Pair is var pair
    && GardenPolicy.AdjacentSlots(pair[0],pair[1],run.Profile.Cells)),"Every future crossing uses proven same-bed adjacency");
Check(ladderForecast.Single(r=>r.Output==41).PlantAt>=forecastNow.AddHours(24)
    && ladderForecast.Single(r=>r.Output==61).PlantAt>=forecastNow.AddHours(48),
    "Future stages cannot start before their ancestor seeds can first be harvested");
run=Ladder(12);
Check(run.Profile.ActiveBatches.Single(b=>!b.Cross).Cells.Count==4,
    "Short ordinary crops use future-stage cells while still leaving current crossing parents and neighbors clear");
var oldIds=run.Profile.Cells.Select(c=>c.Id).ToArray();run.Initialize();
Check(run.Profile.Cells.Select(c=>c.Id).SequenceEqual(oldIds) && run.Profile.ActiveBatches.Single(b=>!b.Cross).Cells.Count==4,
    "Replanning the same forecast is stable and does not duplicate or lose cell registrations");
run=Ladder(reservedOrdinary:true);
Check(run.Profile.Batch is {Cross:true} && run.Profile.ActiveBatches.Where(b=>!b.Cross).Sum(b=>b.Cells.Count)<6,
    "Unplanted ordinary reservations yield to a newly selected crossing before seeds or soil are spent");
run=Ladder();run.Profile.Goals.RemoveAll(g=>g.Item==60);run.Initialize();
Check(run.Profile.ActiveBatches.Single().Cross==false && run.Profile.Batch!.Cells.Count==8,
    "Removing a crossing goal releases its unplanted current and future reservations for ordinary production");
run=Ladder();run.Inventory[61]=1;run.Initialize();
Check(run.Profile.ActiveBatches.All(b=>!b.Cross),
    "An externally obtained final seed removes obsolete ancestor crossings rather than following a stale forecast");
run=Ladder();run.Profile.Cells.ForEach(c=>{c.Seed=101;c.Crop=100;c.Owned=true;c.State="growing";c.Planted=DateTime.UtcNow;c.Tended=DateTime.UtcNow;});
run.Profile.SetBatches([new(){Goal=100,Output=100,SeedA=101,Required=8,Cells=run.Profile.Cells.Select(c=>c.Id).ToList()}]);run.Initialize();
Check(run.Profile.Cells.All(c=>c.Owned&&c.Seed==101) && !run.Profile.ActiveBatches.Any(b=>b.Cross) && !run.Ready(),
    "An urgent crossing waits for already planted ordinary crops instead of discarding them or spinning the workflow");
run=Ladder();run.Catalog.Crops[10]=run.Catalog.Crops[10] with{Hours=0};run.Initialize();
var unknownPlan=SeedPlanner.Build(61,1,run.Catalog.SeedCrops.Keys.ToDictionary(id=>id,id=>new Stock(run.Inventory.GetValueOrDefault(id),0,!run.UnavailableSeeds.Contains(id))),run.Catalog.Crosses);
var unknownForecast=GardenLookahead.Build(run.Profile,run.Catalog.Crops,run.Catalog.SeedCrops,[unknownPlan],DateTime.UtcNow);
Check(!unknownForecast.Any(r=>r.Output==41),
    "Unknown parent growth time cannot invent a future-stage start or promise a borrowing window");
run=Ladder();run.Profile.Goals.RemoveAll(g=>g.Item==100);
run.Inventory[11]=1;run.Inventory[21]=1;run.UnavailableSeeds.UnionWith([11u,21u]);run.Initialize();
Check(run.Start(),"A planned crossing starts with one of each non-procurable parent seed");
run.Run();
Check(run.Inventory[11]==0 && run.Inventory[21]==0 && run.Profile.Batch is { Cross:true, Output:31 },
    "Consuming both parent seeds keeps the verified growing stage relevant to its descendants");
foreach(var (output,reward) in new (uint,uint)[]{(31,31),(41,41),(61,61)}) {
    var stage=run.Profile.ActiveBatches.Single(b=>b.Cross && !b.Clearing);
    var producingCell=run.Profile.Cells.Single(c=>c.Id==stage.Cells[0]);
    var patchId=(ulong)producingCell.Index+1;
    Check(stage.Output==output && stage.Cells.All(id=>run.Profile.Cells.Single(c=>c.Id==id).Owned),
        "The expected ancestor and both its parents are planted before its harvest");
    producingCell.State="mature";
    run.Patches[patchId]=(Harvest.Core.PatchState.Ripe,producingCell.Crop);
    run.Rewards[patchId]=new(){[reward]=1};
    run.Initialize();
    Check(run.Start() && run.Run().Result=="Succeeded", "Each verified harvest advances the ladder in the same visit");
}
Check(run.Profile.ActiveBatches.Any(b=>!b.Cross && b.Goal==60 && b.Cells.All(id=>run.Profile.Cells.Single(c=>c.Id==id).Owned)),
    "After three crossed seed receipts, the final goal crop is planted and previous parents are preserved until harvest");
run=Layout(8,0,1,3,4,5,6);var waitingCell=run.Profile.Cells[7];
waitingCell.Owned=true;waitingCell.Seed=21;waitingCell.Crop=20;waitingCell.State="mature";
run.Profile.AdditionalBatches.Add(new(){Goal=20,Output=20,SeedA=21,Clearing=true,Cells=[waitingCell.Id]});run.Initialize();
Check(run.Profile.ActiveBatches.Single(b=>b.Cross).Cells.All(id=>run.Profile.Cells.Single(c=>c.Id==id).Index is 4 or 5),
    "A ready-to-harvest neighbor is still occupied, so a clear pair takes precedence immediately");
Console.WriteLine($"PASS {checks} actual visit/planner lifecycle checks; game, movement and inventory IPC are simulated.");
