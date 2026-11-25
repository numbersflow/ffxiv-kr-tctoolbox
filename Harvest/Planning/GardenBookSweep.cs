using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Harvest.Planning;

internal sealed class GardenBookSweep
{
    private readonly Dictionary<uint, string> results = new();
    private uint[] scopes = [];
    private ulong character;
    public IReadOnlyDictionary<uint, string> Results => results;

    public void Reset()
    {
        results.Clear();
        scopes = [];
        character = 0;
    }

    // A complete sweep is consumed on the next frame, apart from the last IPC call.
    public bool ReadNext(ulong owner, uint[] required, Func<uint, string> read)
    {
        if (owner != character || !required.SequenceEqual(scopes)) {
            Reset();
            character = owner;
            scopes = required;
        }
        if (results.Count == scopes.Length) return true;
        var scope = scopes[results.Count];
        try { results.Add(scope, read(scope)); }
        catch (Exception ex) { results.Add(scope, JsonSerializer.Serialize(new { Available = false, Error = ex.Message })); }
        return false;
    }
}
