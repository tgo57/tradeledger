using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using TradeLedger.Core.Models;
using TradeLedger.Importers.Csv;
using System.Security.Cryptography;
using System.Text;


namespace TradeLedger.Importers.Schwab;

public sealed class SchwabCsvImporter
{
    public sealed record ImportResult(int RowsRead, int ExecutionsCreated, List<string> Warnings);

    public ImportResult Import(string accountLabel, string csvPath)
    {
        var warnings = new List<string>();

        if (!File.Exists(csvPath))
            throw new FileNotFoundException("CSV not found.", csvPath);

        using var reader = new StreamReader(csvPath);

        // CsvHelper will read headers, handle quotes/commas properly.
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectDelimiter = true,
            TrimOptions = TrimOptions.Trim,
        };

        using var csv = new CsvReader(reader, cfg);

        csv.Read();
        csv.ReadHeader();

        var headers = csv.HeaderRecord?.ToList() ?? throw new InvalidOperationException("No CSV headers found.");

        // Common Schwab header variants (these vary by export type)
        int dateIdx = HeaderMap.FindIndex(headers, "Date", "Trade Date", "Transaction Date");
        int timeIdx = HeaderMap.FindIndex(headers, "Time", "Trade Time", "Transaction Time");
        int actionIdx = HeaderMap.FindIndex(headers, "Action", "Type", "Transaction Type");
        int symbolIdx = HeaderMap.FindIndex(headers, "Symbol", "Ticker");
        int descIdx = HeaderMap.FindIndex(headers, "Description", "Security Description", "Name");
        int qtyIdx = HeaderMap.FindIndex(headers, "Quantity", "Qty");
        int priceIdx = HeaderMap.FindIndex(headers, "Price", "Trade Price");
        int feesIdx = HeaderMap.FindIndex(
            headers,
            "Fees & Comm",          // ✅ exact Schwab header
            "Fees & Commissions",
            "Fees and Comm",
            "Fees and Commissions",
            "Commissions & Fees",
            "Commission & Fees",
            "Fees",
            "Commission"
        );

        if (feesIdx < 0)
            warnings.Add("Could not find a Fees column (expected something like 'Fees & Comm'). Fees will be 0.");


        int amountIdx = HeaderMap.FindIndex(headers, "Amount", "Net Amount", "Value", "Proceeds");

        // Minimum workable set:
        if (dateIdx < 0 && timeIdx < 0)
            warnings.Add("Could not find a Date/Time column. ExecutedAt will default to file read time per row (not ideal).");

        if (symbolIdx < 0)
            warnings.Add("Could not find a Symbol column. Symbol will be blank unless derivable from Description.");

        var results = new List<Execution>();
        int rowNum = 1; // header row = 1

        while (csv.Read())
        {
            rowNum++;

            string? dateStr = dateIdx >= 0 ? csv.GetField(dateIdx) : null;
            string? timeStr = timeIdx >= 0 ? csv.GetField(timeIdx) : null;

            DateTime executedAt = ParseDateTimeBestEffort(dateStr, timeStr, rowNum, warnings);

            var action = actionIdx >= 0 ? (csv.GetField(actionIdx) ?? "").Trim() : "";
            var symbol = symbolIdx >= 0 ? (csv.GetField(symbolIdx) ?? "").Trim() : "";
            var desc = descIdx >= 0 ? (csv.GetField(descIdx) ?? "").Trim() : "";

            decimal? qty = ParseNullableDecimal(qtyIdx >= 0 ? csv.GetField(qtyIdx) : null);
            decimal? price = ParseNullableDecimal(priceIdx >= 0 ? csv.GetField(priceIdx) : null);
            decimal fees = ParseNullableDecimal(feesIdx >= 0 ? csv.GetField(feesIdx) : null) ?? 0m;
            decimal netAmt = ParseNullableDecimal(amountIdx >= 0 ? csv.GetField(amountIdx) : null) ?? 0m;

            // capture the row for audit/debug
            var raw = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                raw[h] = csv.GetField(h);

            results.Add(new Execution
            {
                Broker = "Schwab",
                Account = accountLabel,
                ExecutedAt = executedAt,
                Action = action,
                Symbol = symbol,
                Description = desc,
                Quantity = qty,
                Price = price,
                Fees = fees,
                NetAmount = netAmt,
                Fingerprint = ComputeFingerprint(
                    "Schwab",
                    accountLabel,
                    executedAt,
                    action,
                    symbol,
                    qty,
                    price,
                    netAmt,
                    fees,
                    desc),

                SourceFile = Path.GetFileName(csvPath),
                SourceRowNumber = rowNum,
                RawRowJson = JsonSerializer.Serialize(raw)
            });
        }

        return new ImportResult(rowNum - 1, results.Count, warnings)
        {
            // Note: executions are returned via results list in CLI step below; keeping result minimal.
        };
    }

    public List<Execution> ImportExecutions(string accountLabel, string csvPath, out ImportResult summary)
    {
        var warnings = new List<string>();

        using var reader = new StreamReader(csvPath);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            DetectDelimiter = true,
            TrimOptions = TrimOptions.Trim,
        };
        using var csv = new CsvReader(reader, cfg);

        csv.Read();
        csv.ReadHeader();

        var headers = csv.HeaderRecord?.ToList() ?? throw new InvalidOperationException("No CSV headers found.");

        int dateIdx = HeaderMap.FindIndex(headers, "Date", "Trade Date", "Transaction Date");
        int timeIdx = HeaderMap.FindIndex(headers, "Time", "Trade Time", "Transaction Time");
        int actionIdx = HeaderMap.FindIndex(headers, "Action", "Type", "Transaction Type");
        int symbolIdx = HeaderMap.FindIndex(headers, "Symbol", "Ticker");
        int descIdx = HeaderMap.FindIndex(headers, "Description", "Security Description", "Name");
        int qtyIdx = HeaderMap.FindIndex(headers, "Quantity", "Qty");
        int priceIdx = HeaderMap.FindIndex(headers, "Price", "Trade Price");

        int feesIdx = HeaderMap.FindIndex(
            headers,
            "Fees & Comm",          // ✅ exact Schwab header
            "Fees & Commissions",
            "Fees and Comm",
            "Fees and Commissions",
            "Commissions & Fees",
            "Commission & Fees",
            "Fees",
            "Commission"
        );

        if (feesIdx < 0)
            warnings.Add("Could not find a Fees column (expected something like 'Fees & Comm'). Fees will be 0.");


        int amountIdx = HeaderMap.FindIndex(headers, "Amount", "Net Amount", "Value", "Proceeds");

        if (dateIdx < 0 && timeIdx < 0)
            warnings.Add("Could not find a Date/Time column. ExecutedAt defaults to DateTime.MinValue.");

        var results = new List<Execution>();
        int rowNum = 1;

        while (csv.Read())
        {
            rowNum++;

            string? dateStr = dateIdx >= 0 ? csv.GetField(dateIdx) : null;
            string? timeStr = timeIdx >= 0 ? csv.GetField(timeIdx) : null;

            DateTime executedAt = ParseDateTimeBestEffort(dateStr, timeStr, rowNum, warnings, allowMinValue: true);

            var action = actionIdx >= 0 ? (csv.GetField(actionIdx) ?? "").Trim() : "";
            var symbol = symbolIdx >= 0 ? (csv.GetField(symbolIdx) ?? "").Trim() : "";
            var desc = descIdx >= 0 ? (csv.GetField(descIdx) ?? "").Trim() : "";

            decimal? qty = ParseNullableDecimal(qtyIdx >= 0 ? csv.GetField(qtyIdx) : null);
            decimal? price = ParseNullableDecimal(priceIdx >= 0 ? csv.GetField(priceIdx) : null);
            decimal fees = ParseNullableDecimal(feesIdx >= 0 ? csv.GetField(feesIdx) : null) ?? 0m;
            decimal netAmt = ParseNullableDecimal(amountIdx >= 0 ? csv.GetField(amountIdx) : null) ?? 0m;

            var raw = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                raw[h] = csv.GetField(h);

            results.Add(new Execution
            {
                Broker = "Schwab",
                Account = accountLabel,
                ExecutedAt = executedAt,

                Action = action,
                Symbol = symbol,
                Description = desc,
                Quantity = qty,
                Price = price,
                Fees = fees,
                NetAmount = netAmt,

                // ✅ IMPORTANT: set fingerprint here too
                Fingerprint = ComputeFingerprint(
                    "Schwab",
                    accountLabel,
                    executedAt,
                    action,
                    symbol,
                    qty,
                    price,
                    netAmt,
                    fees,
                    desc),

                SourceFile = Path.GetFileName(csvPath),
                SourceRowNumber = rowNum,
                RawRowJson = JsonSerializer.Serialize(raw)
            });
        }


        summary = new ImportResult(rowNum - 1, results.Count, warnings);
        return results;
    }
    private static double ParseMoneyToDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0d;

        s = s.Trim();

        // Handle ($1,234.56) format
        var isParenNeg = s.StartsWith("(") && s.EndsWith(")");
        if (isParenNeg) s = s[1..^1];

        // Remove $ and commas
        s = s.Replace("$", "").Replace(",", "").Trim();

        if (!double.TryParse(s, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture,
                             out var v))
            return 0d;

        return isParenNeg ? -v : v;
    }

    private static DateTime ParseDateTimeBestEffort(
        string? dateStr,
        string? timeStr,
        int rowNum,
        List<string> warnings,
        bool allowMinValue = false)
    {
        dateStr = string.IsNullOrWhiteSpace(dateStr) ? null : dateStr.Trim();
        timeStr = string.IsNullOrWhiteSpace(timeStr) ? null : timeStr.Trim();

        if (dateStr is null && timeStr is null)
            return allowMinValue ? DateTime.MinValue : DateTime.UtcNow;

        // Handle Schwab strings like: "11/17/2025 as of 11/14/2025"
        // Keep the "as of" date, but DO NOT return yet (we still want to apply timeStr).
        if (!string.IsNullOrWhiteSpace(dateStr))
        {
            const string asOfMarker = " as of ";
            var idx = dateStr.IndexOf(asOfMarker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = dateStr[(idx + asOfMarker.Length)..].Trim();
                var before = dateStr[..idx].Trim();
                dateStr = !string.IsNullOrWhiteSpace(after) ? after : before;
            }
        }




        // Try combined parse
        if (dateStr is not null && timeStr is not null)
        {
            if (DateTime.TryParse($"{dateStr} {timeStr}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                return dt;
        }

        // Try date-only
        if (dateStr is not null)
        {
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                return d;
        }

        warnings.Add($"Row {rowNum}: Could not parse date/time (date='{dateStr}', time='{timeStr}').");
        return allowMinValue ? DateTime.MinValue : DateTime.UtcNow;
    }

    private static string ComputeFingerprint(
    string broker,
    string account,
    DateTime executedAt,
    string action,
    string symbol,
    decimal? qty,
    decimal? price,
    decimal netAmount,
    decimal fees,
    string desc)
    {
        //var s = $"{broker}|{account}|{executedAt:O}|{action}|{symbol}|{qty}|{price}|{netAmount}|{fees}|{desc}";
        var s = $"{broker}|{account}|{executedAt:O}|{action}|{symbol}|{qty}|{price}|{netAmount}|{desc}";

        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash); // 64 hex chars
    }


    private static decimal? ParseNullableDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;

        // Remove $ and commas, handle parentheses for negatives
        s = s.Trim().Replace("$", "").Replace(",", "");

        bool negativeParen = s.StartsWith("(") && s.EndsWith(")");
        if (negativeParen) s = s[1..^1];

        if (decimal.TryParse(s, NumberStyles.Number | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var v))
        {
            return negativeParen ? -v : v;
        }

        return null;
    }
}
