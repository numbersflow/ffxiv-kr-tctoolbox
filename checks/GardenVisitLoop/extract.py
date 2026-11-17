"""Compile the actual visit, reconciliation and planner; replace only game/IPC boundaries."""
from pathlib import Path
import sys
root = Path(sys.argv[1])
visit = (root / "HarvestVisit.cs").read_text(encoding="utf-8-sig")
service = (root / "HarvestService.cs").read_text(encoding="utf-8-sig")
parts = [visit[visit.index("    private sealed class Visit"):visit.index("    private static unsafe int FreeSlots()")],
         visit[visit.index("    private void StopOwnedTravel("):visit.rindex("\n}")],
         service[service.index("    private void Replan()"):service.index("    public void Dispose()")]]
for part in parts:
    assert part.count("{") == part.count("}"), "Actual visit extraction boundaries changed"
target = Path(sys.argv[2])
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text("#nullable enable\nusing System; using System.Linq; using System.Collections.Generic; using System.Numerics; using System.Text.Json;\n"
                  "using Harvest.Core; using Harvest.Modules; using LocalAutomation;\n"
                  "namespace Harvest.Planning; public sealed partial class VisitDriver {\n"
                  + "\n".join(parts) + "}\n", encoding="utf-8")
