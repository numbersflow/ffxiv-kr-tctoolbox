using System.Collections.Generic;

namespace Harvest.Core;

public static class CropNameMatching
{
    public static uint Resolve(string name, IReadOnlyDictionary<string, uint> known)
    {
        name = name.Trim();
        if (known.TryGetValue(name, out var exact)) return exact;
        // Korean item macros can append a grammatical particle before the status suffix.
        foreach (var particle in new[] { "이", "가", "은", "는" })
            if (name.EndsWith(particle, System.StringComparison.Ordinal) && known.TryGetValue(name[..^particle.Length], out var item)) return item;
        return 0;
    }
}
