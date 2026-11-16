using System;

namespace TCToolbox.Core;

internal static class GardenTextData
{
    internal static string[] Read(Func<uint, string?> read)
    {
        var rows = new string[11];
        for (uint id = 0; id < rows.Length; id++)
        {
            rows[id] = read(id) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rows[id]))
                throw new InvalidOperationException($"재배 메뉴 데이터 {id}행을 읽을 수 없습니다.");
        }
        return rows;
    }
}
