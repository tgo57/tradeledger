using System.Security.Cryptography;
using System.Text;
using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Tastytrade;

namespace TradeLedger.Web.Services;

/// <summary>
/// Web-friendly TastyTrade import service.
/// Wraps the existing TastytradeCsvImporter, groups executions into TradeGroups,
/// and saves everything to the database.
/// </summary>
public sealed class TastyTradeImporter
{
    private readonly AppDbContext _db;
    public TastyTradeImporter(AppDbContext db) => _db = db;

    public sealed record ImportResult(int Inserted, int Skipped, List<string> Errors);

    public async Task<ImportResult> ImportAsync(Stream csvStream, string account)
    {
        var inserted = 0;
        var skipped = 0;
        var errors = new List<string>();

        // Write stream to temp file so TastytradeCsvImporter (file-based) can read it
        var tempPath = Path.Combine(Path.GetTempPath(), $"tasty_{Guid.NewGuid():N}.csv");

        try
        {
            await using (var fs = File.Create(tempPath))
                await csvStream.CopyToAsync(fs);

            // Use existing importer to parse executions
            var importer = new TastytradeCsvImporter();
            var executions = importer.ImportExecutions(account, tempPath, out var summary);

            foreach (var w in summary.Warnings)
                errors.Add(w);

            if (!executions.Any())
            {
                errors.Add("No executions found in file.");
                return new ImportResult(0, 0, errors);
            }

            // Filter to option executions only (have a symbol and action)
            var optionExecs = executions
                .Where(e => !string.IsNullOrWhiteSpace(e.Symbol) && !string.IsNullOrWhiteSpace(e.Action))
                .Where(e => IsOptionAction(e.Action))
                .ToList();

            if (!optionExecs.Any())
            {
                errors.Add("No option executions found — only equity options are supported.");
                return new ImportResult(0, 0, errors);
            }

            // Group by order: TastyTrade CSV has no Order # in TastytradeCsvImporter output,
            // so we cluster by time (legs executed within 60 seconds = same spread)
            var groups = GroupIntoTradeGroups(optionExecs, account, errors);

            foreach (var (group, legs) in groups)
            {
                try
                {
                    // Duplicate check: look for existing group with same account/underlying/expiry/strike/open date
                    var exists = _db.TradeGroups.Any(g =>
                        g.Account == account &&
                        g.Underlying == group.Underlying &&
                        g.Expiration == group.Expiration &&
                        g.ShortStrike == group.ShortStrike &&
                        g.OpenDate.Date == group.OpenDate.Date);

                    if (exists) { skipped++; continue; }

                    _db.TradeGroups.Add(group);
                    await _db.SaveChangesAsync();

                    foreach (var exec in legs)
                    {
                        // Ensure fingerprint
                        if (string.IsNullOrWhiteSpace(exec.Fingerprint))
                            exec.Fingerprint = MakeFingerprint(exec);

                        // Skip duplicate executions
                        if (_db.Executions.Any(e => e.Fingerprint == exec.Fingerprint))
                            continue;

                        _db.Executions.Add(exec);
                        await _db.SaveChangesAsync();

                        _db.TradeGroupExecutions.Add(new TradeGroupExecution
                        {
                            TradeGroupId = group.Id,
                            ExecutionId = exec.Id
                        });
                    }

                    await _db.SaveChangesAsync();
                    inserted++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Save error for {group.Underlying} {group.Expiration}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return new ImportResult(inserted, skipped, errors);
    }

    // ── Trade grouping ────────────────────────────────────────────────────────

    private static List<(TradeGroup Group, List<Execution> Legs)> GroupIntoTradeGroups(
    List<Execution> executions,
    string account,
    List<string> errors)
    {
        var result = new List<(TradeGroup, List<Execution>)>();
        var sorted = executions.OrderBy(e => e.ExecutedAt).ToList();

        // Cluster legs executed within 60 seconds = one spread order
        var clusters = new List<List<Execution>>();
        var current = new List<Execution>();

        foreach (var exec in sorted)
        {
            if (current.Count == 0 ||
                Math.Abs((exec.ExecutedAt - current.Last().ExecutedAt).TotalSeconds) < 60)
                current.Add(exec);
            else
            {
                clusters.Add(new List<Execution>(current));
                current = new List<Execution> { exec };
            }
        }
        if (current.Any()) clusters.Add(current);

        foreach (var cluster in clusters)
        {
            if (cluster.Count == 0) continue;

            // Parse OCC symbols for all legs
            var parsed = cluster
                .Select(e => (exec: e, occ: TryParseOcc(e.Symbol)))
                .Where(x => x.occ != null)
                .ToList();

            if (!parsed.Any()) continue;

            // Sell leg = short strike, Buy leg = long strike
            var sellLeg = parsed.FirstOrDefault(x => IsSell(x.exec.Action));
            var buyLeg = parsed.FirstOrDefault(x => IsBuy(x.exec.Action));

            if (sellLeg.exec == null) sellLeg = parsed[0];
            if (buyLeg.exec == null) buyLeg = parsed.Count > 1 ? parsed[1] : parsed[0];

            var first = parsed.OrderBy(x => x.exec.ExecutedAt).First();
            var openDate = first.exec.ExecutedAt;
            var netPL = cluster.Sum(e => e.NetAmount);

            DateTime? closeDate = null;
            if (first.occ!.Expiration.ToDateTime(TimeOnly.MinValue) < DateTime.Today)
                closeDate = first.occ.Expiration.ToDateTime(TimeOnly.MinValue);

            var group = new TradeGroup
            {
                Broker = "TastyTrade",
                Account = account,
                Underlying = first.occ.Underlying,
                Right = first.occ.Right,
                StrategyType = "CreditSpread",
                ShortStrike = sellLeg.occ?.Strike ?? 0m,
                LongStrike = buyLeg.occ?.Strike ?? 0m,
                Expiration = first.occ.Expiration,
                OpenDate = openDate,
                CloseDate = closeDate,
                NetPL = netPL,
                Setup = first.occ.Right == "Put" ? "BullPutSpread" : "BearCallSpread"
            };

            result.Add((group, cluster));
        }

        return result;
    }

    // ── OCC symbol parser ─────────────────────────────────────────────────────
    // TastyTrade format: "SPXW  260424P07135000"
    // = underlying(padded) + YYMMDD + C/P + strike*1000 (8 digits)

    private sealed record OccContract(string Underlying, DateOnly Expiration, decimal Strike, string Right);

    private static OccContract? TryParseOcc(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        symbol = symbol.Trim();
        if (symbol.Length < 15) return null;

        try
        {
            // Underlying is left-padded to 6 chars, then 6-digit date, 1 char right, 8-digit strike
            // Find where digits start (after underlying letters)
            var i = 0;
            while (i < symbol.Length && !char.IsDigit(symbol[i])) i++;

            if (i >= symbol.Length - 14) return null;

            var underlying = symbol[..i].Trim();
            var dateStr = symbol.Substring(i, 6);      // YYMMDD
            var rightChar = symbol[i + 6].ToString().ToUpper();
            var strikeStr = symbol.Substring(i + 7, 8);  // strike * 1000

            if (!DateOnly.TryParseExact($"20{dateStr}", "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var exp))
                return null;

            if (!decimal.TryParse(strikeStr, out var strikeRaw)) return null;
            var strike = strikeRaw / 1000m;
            var right = rightChar == "P" ? "Put" : "Call";

            return new OccContract(underlying, exp, strike, right);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ImportResult> ImportFromExecutionsAsync(List<Execution> executions, string account)
    {
        var inserted = 0;
        var skipped = 0;
        var errors = new List<string>();

        // Filter to option open executions (same logic as CSV importer)
        var optionExecs = executions
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol) && !string.IsNullOrWhiteSpace(e.Action))
            .Where(e => IsOptionAction(e.Action))
            .ToList();

        if (!optionExecs.Any())
        {
            errors.Add("No option open executions found in API response.");
            return new ImportResult(0, 0, errors);
        }

        var groups = GroupIntoTradeGroups(optionExecs, account, errors);

        foreach (var (group, legs) in groups)
        {
            try
            {
                var exists = _db.TradeGroups.Any(g =>
                    g.Account == account &&
                    g.Underlying == group.Underlying &&
                    g.Expiration == group.Expiration &&
                    g.ShortStrike == group.ShortStrike &&
                    g.OpenDate.Date == group.OpenDate.Date);

                if (exists) { skipped++; continue; }

                _db.TradeGroups.Add(group);
                await _db.SaveChangesAsync();

                foreach (var exec in legs)
                {
                    if (string.IsNullOrWhiteSpace(exec.Fingerprint))
                        exec.Fingerprint = MakeFingerprint(exec);

                    if (_db.Executions.Any(e => e.Fingerprint == exec.Fingerprint))
                        continue;

                    if (_db.Executions.Any(e => e.Fingerprint == exec.Fingerprint))
                        continue;

                    _db.Executions.Add(exec);
                    await _db.SaveChangesAsync();

                    _db.TradeGroupExecutions.Add(new TradeGroupExecution
                    {
                        TradeGroupId = group.Id,
                        ExecutionId = exec.Id
                    });
                }

                await _db.SaveChangesAsync();
                inserted++;
            }
            catch (Exception ex)
            {
                errors.Add($"Save error for {group.Underlying} {group.Expiration}: {ex.Message}");
            }
        }

        return new ImportResult(inserted, skipped, errors);
    }


    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsOptionAction(string? action) =>
    action?.Contains("_TO_OPEN", StringComparison.OrdinalIgnoreCase) == true ||
    action?.Contains("to open", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSell(string? action) =>
        action?.Contains("Sell", StringComparison.OrdinalIgnoreCase) == true ||
        action?.StartsWith("SELL", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsBuy(string? action) =>
        action?.Contains("Buy", StringComparison.OrdinalIgnoreCase) == true ||
        action?.StartsWith("BUY", StringComparison.OrdinalIgnoreCase) == true;

    private static string MakeFingerprint(Execution e)
    {
        var s = $"TastyTrade|{e.Account}|{e.ExecutedAt:O}|{e.Action}|{e.Symbol}|{e.Quantity}|{e.Price}|{e.NetAmount}";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}