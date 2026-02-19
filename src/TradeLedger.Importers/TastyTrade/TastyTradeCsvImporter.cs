using System.Globalization;
using TradeLedger.Core.Models;

namespace TradeLedger.Importers.Tastytrade;

public sealed class TastytradeCsvImporter
{
    public List<Execution> ImportExecutions(string account, string csvPath, out ImportSummary summary)
    {
        summary = new ImportSummary();

        if (!File.Exists(csvPath))
        {
            summary.Warnings.Add($"CSV not found: {csvPath}");
            return new();
        }

        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2)
        {
            summary.Warnings.Add("CSV has no data rows.");
            return new();
        }

        summary.RowsRead = lines.Length;

        var header = SplitCsvLine(lines[0]).ToArray();

        int Find(params string[] names) =>
            Array.FindIndex(header, h => names.Any(n => string.Equals(Norm(h), Norm(n), StringComparison.OrdinalIgnoreCase)));

        var iTime = Find("Date", "Time", "Date/Time", "Executed At", "Execution Time");
        var iSymbol = Find("Symbol", "Instrument", "Underlying", "Description");
        var iAction = Find("Action", "Side", "Buy/Sell", "Transaction Type");
        var iQty = Find("Quantity", "Qty", "Size");
        var iPrice = Find("Price", "Fill Price");
        var iFees = Find("Fees", "Commission", "Commissions", "Fees & Commissions");
        var iNet = Find("Net Amount", "Net", "Amount", "Value", "Proceeds");

        if (iSymbol < 0) summary.Warnings.Add("Missing Symbol/Instrument column (cannot import).");
        if (iAction < 0) summary.Warnings.Add("Missing Action/Side column.");
        if (iQty < 0) summary.Warnings.Add("Missing Quantity/Qty column.");
        if (iPrice < 0) summary.Warnings.Add("Missing Price column.");
        if (iTime < 0) summary.Warnings.Add("Missing Date/Time column.");

        if (iSymbol < 0 || iAction < 0 || iQty < 0 || iPrice < 0 || iTime < 0)
            return new();

        var execs = new List<Execution>();

        for (int r = 1; r < lines.Length; r++)
        {
            var row = SplitCsvLine(lines[r]);
            if (row.Count == 0) continue;

            var rawTime = Safe(row, iTime);
            if (!TryParseDateTime(rawTime, out var executedAt))
            {
                summary.Warnings.Add($"Row {r + 1}: could not parse time '{rawTime}'");
                continue;
            }

            var symbol = Safe(row, iSymbol);
            var action = Safe(row, iAction);

            var qty = TryDec(Safe(row, iQty));         // decimal?
            var price = TryDec(Safe(row, iPrice));     // decimal?
            var fees = iFees >= 0 ? (TryDec(Safe(row, iFees)) ?? 0m) : 0m;
            var net = iNet >= 0 ? (TryDec(Safe(row, iNet)) ?? 0m) : 0m;

            var e = new Execution
            {
                Broker = "tastytrade",
                Account = account,

                ExecutedAt = executedAt,
                Symbol = symbol,
                Description = "",

                Action = action,

                Quantity = qty,
                Price = price,
                Fees = fees,
                NetAmount = net,

                Currency = "USD",

                SourceFile = Path.GetFileName(csvPath),
                SourceRowNumber = r + 1,
                RawRowJson = "{}" // TODO: later store raw row JSON (optional)
            };

            e.Fingerprint = ExecutionFingerprint.Make(e);

            if (!string.IsNullOrWhiteSpace(e.Fingerprint))
                execs.Add(e);
        }

        summary.ExecutionsCreated = execs.Count;
        return execs;
    }

    // --- helpers ---
    static string Norm(string s) => (s ?? "").Trim().Replace("\u00A0", " ");

    static string Safe(List<string> row, int i) => (i >= 0 && i < row.Count) ? row[i].Trim() : "";

    static decimal? TryDec(string s)
        => decimal.TryParse(
            s.Replace("$", "").Replace(",", "").Trim(),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out var d
        ) ? d : null;

    static bool TryParseDateTime(string s, out DateTime dt)
    {
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt)
            || DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out dt);
    }

    static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(line)) return result;

        bool inQuotes = false;
        var cur = new System.Text.StringBuilder();

        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else inQuotes = !inQuotes;

                continue;
            }

            if (c == ',' && !inQuotes)
            {
                result.Add(cur.ToString());
                cur.Clear();
                continue;
            }

            cur.Append(c);
        }

        result.Add(cur.ToString());
        return result;
    }
}
