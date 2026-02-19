namespace TradeLedger.Importers.Csv;

internal static class HeaderMap
{
    public static int FindIndex(IReadOnlyList<string> headers, params string[] candidates)
    {
        for (int i = 0; i < headers.Count; i++)
            foreach (var c in candidates)
                if (string.Equals(headers[i].Trim(), c, StringComparison.OrdinalIgnoreCase))
                    return i;

        return -1;
    }
}
