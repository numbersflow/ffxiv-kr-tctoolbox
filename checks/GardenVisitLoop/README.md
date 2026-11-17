# Garden visit lifecycle regression

Run `dotnet run --project checks/GardenVisitLoop/Check.csproj -c Release` with `DALAMUD_HOME` set.

The fixture compiles the actual visit loop, action selection, reconciliation, inventory reader, due checks, and planner from `HarvestVisit.cs` and `HarvestService.cs`. It uses the real public model and planting policies. Game interaction, movement, inventory IPC, and executor receipts are simulated.

Scenarios cover planting and immediate color, tending all owned cells in one visit, harvest and replant, next-target planting, crossing-anchor cleanup, missing planting/color materials, care-only operation, insufficient bag space, pause/resume, cancellation during an action, pending planting recovery, character changes, manual harvest, foreign/withered crops, blocked crossing partners, and unconfirmed tending receipts. It also checks terminal ready-cache invalidation.

`VisitSourceDirectory` can point at an isolated copy of the two source files. Restoring the old one-action completion branch causes the flower continuation check to fail. Production source is not modified for that regression demonstration. Actual Lifestream route handling is exercised separately in `checks/GardenWalkVisit`; actual game menus and long growth periods remain outside these simulated checks.
