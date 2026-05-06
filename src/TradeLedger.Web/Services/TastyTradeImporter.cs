using Microsoft.EntityFrameworkCore;
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

        var tempPath = Path.Combine(Path.GetTempPath(), $"tasty_{Guid.NewGuid():N}.csv");

        try
        {
            await using (var fs = File.Create(tempPath))
                await csvStream.CopyToAsync(fs);

            var importer = new TastytradeCsvImporter();
            var executions = importer.ImportExecutions(account, tempPath, out var summary);

            foreach (var w in summary.Warnings)
                errors.Add(w);

            if (!executions.Any())
            {
                errors.Add("No executions found in file.");
                return new ImportResult(0, 0, errors);
            }

            var optionExecs = executions
                .Where(e => !string.IsNullOrWhiteSpace(e.Symbol) && !string.IsNullOrWhiteSpace(e.Action))
                .Where(e => IsOptionAction(e.Action))
                .ToList();

            if (!optionExecs.Any())
            {
                errors.Add("No option executions found — only equity options are supported.");
                return new ImportResult(0, 0, errors);
            }

            // Split into opening and closing legs upfront
            var openingLegs = optionExecs.Where(e => IsOpen(e.Action)).ToList();
            var closingLegs = optionExecs.Where(e => IsClose(e.Action) || IsAssignmentOrExpiry(e.Action)).ToList();

            var openGroups = GroupIntoTradeGroups(openingLegs, account, errors);

            foreach (var (group, legs) in openGroups)
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

            // Match closing legs to existing open trade groups
            var (closeInserted, closeErrors) = await MatchClosingLegsAsync(closingLegs, account);
            errors.AddRange(closeErrors);

        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return new ImportResult(inserted, skipped, errors);
    }

    public async Task<ImportResult> ImportFromExecutionsAsync(List<Execution> executions, string account)
    {
        var inserted = 0;
        var skipped = 0;
        var errors = new List<string>();

        var optionExecs = executions
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol) && !string.IsNullOrWhiteSpace(e.Action))
            .Where(e => IsOptionAction(e.Action))
            .ToList();

        if (!optionExecs.Any())
        {
            errors.Add("No option executions found in API response.");
            return new ImportResult(0, 0, errors);
        }

        var openingLegs = optionExecs.Where(e => IsOpen(e.Action)).ToList();
        var closingLegs = optionExecs.Where(e => IsClose(e.Action) || IsAssignmentOrExpiry(e.Action)).ToList();

        // ── Pass 1: Opening legs → create new TradeGroups ─────────────────────
        var openGroups = GroupIntoTradeGroups(openingLegs, account, errors);

        foreach (var (group, legs) in openGroups)
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
                    {
                        var existingExec = _db.Executions.First(e => e.Fingerprint == exec.Fingerprint);
                        bool alreadyLinked = _db.TradeGroupExecutions
                            .Any(l => l.ExecutionId == existingExec.Id && l.TradeGroupId == group.Id);
                        if (!alreadyLinked)
                        {
                            _db.TradeGroupExecutions.Add(new TradeGroupExecution
                            {
                                TradeGroupId = group.Id,
                                ExecutionId = existingExec.Id
                            });
                            await _db.SaveChangesAsync();
                        }
                        continue;
                    }

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

        // ── Pass 2: Closing/assignment/expiry legs → match existing groups ────
        var (_, closeErrors) = await MatchClosingLegsAsync(closingLegs, account);
        errors.AddRange(closeErrors);

        return new ImportResult(inserted, skipped, errors);
    }

    // ── Shared close-leg matching logic ───────────────────────────────────────

    private async Task<(int Updated, List<string> Errors)> MatchClosingLegsAsync(
        List<Execution> closingLegs, string account)
    {
        var errors = new List<string>();
        var updatedGroupIds = new HashSet<long>();

        if (!closingLegs.Any()) return (0, errors);

        // Cluster closing legs by time (60-second window) — each cluster = one closing order
        var closeClusters = ClusterByTime(closingLegs, windowSeconds: 60);

        foreach (var cluster in closeClusters)
        {
            try
            {
                var occ = TryParseOcc(cluster[0].Symbol);
                if (occ == null) continue;

                // Find the matching open trade — same underlying, expiration, right
                // Order by most recent open date to match the closest prior trade
                var matchingGroup = await _db.TradeGroups
                    .Where(g =>
                        g.Account == account &&
                        g.Underlying == occ.Underlying &&
                        g.Expiration == occ.Expiration &&
                        g.Right == occ.Right)
                    .OrderByDescending(g => g.OpenDate)
                    .FirstOrDefaultAsync();

                if (matchingGroup == null)
                {
                    errors.Add($"No open trade found to close: {cluster[0].Symbol} on {cluster[0].ExecutedAt:yyyy-MM-dd}");
                    continue;
                }

                foreach (var exec in cluster)
                {
                    if (string.IsNullOrWhiteSpace(exec.Fingerprint))
                        exec.Fingerprint = MakeFingerprint(exec);

                    if (_db.Executions.Any(e => e.Fingerprint == exec.Fingerprint))
                        continue;

                    _db.Executions.Add(exec);
                    await _db.SaveChangesAsync();

                    _db.TradeGroupExecutions.Add(new TradeGroupExecution
                    {
                        TradeGroupId = matchingGroup.Id,
                        ExecutionId = exec.Id
                    });
                }

                if (matchingGroup.CloseDate == null)
                    matchingGroup.CloseDate = cluster.Max(l => l.ExecutedAt);

                updatedGroupIds.Add(matchingGroup.Id);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"Close cluster error: {ex.Message}");
            }
        }

        // Recalculate NetPL for all updated groups
        foreach (var groupId in updatedGroupIds)
        {
            var allExecIds = _db.TradeGroupExecutions
                .Where(l => l.TradeGroupId == groupId)
                .Select(l => l.ExecutionId)
                .ToList();

            var total = _db.Executions
                .Where(e => allExecIds.Contains(e.Id))
                .AsEnumerable()
                .Sum(e => e.NetAmount);

            await _db.Database.ExecuteSqlRawAsync(
                "UPDATE TradeGroups SET NetPL = {0}, GrossReturn = {1} WHERE Id = {2}",
                total, total, groupId);
        }

        return (updatedGroupIds.Count, errors);
    }

    // ── Trade grouping — time-based clustering ────────────────────────────────
    // Groups legs that executed within 60 seconds of each other = one spread order.
    // This is correct for OPENING legs only — never mix open and close legs here.

    private static List<(TradeGroup Group, List<Execution> Legs)> GroupIntoTradeGroups(
        List<Execution> executions,
        string account,
        List<string> errors)
    {
        var result = new List<(TradeGroup, List<Execution>)>();

        if (!executions.Any()) return result;

        var clusters = ClusterByTime(executions, windowSeconds: 60);

        foreach (var cluster in clusters)
        {
            if (cluster.Count == 0) continue;

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
                GrossReturn = netPL,
                Setup = first.occ.Right == "Put" ? "BullPutSpread" : "BearCallSpread"
            };

            result.Add((group, cluster));
        }

        return result;
    }

    // ── 60-second time-window clustering ─────────────────────────────────────
    // Legs within 60 seconds of each other belong to the same order fill.

    private static List<List<Execution>> ClusterByTime(List<Execution> executions, int windowSeconds = 60)
    {
        var sorted = executions.OrderBy(e => e.ExecutedAt).ToList();
        var clusters = new List<List<Execution>>();
        var current = new List<Execution>();

        foreach (var exec in sorted)
        {
            if (current.Count == 0 ||
                Math.Abs((exec.ExecutedAt - current.Last().ExecutedAt).TotalSeconds) < windowSeconds)
            {
                current.Add(exec);
            }
            else
            {
                clusters.Add(new List<Execution>(current));
                current = new List<Execution> { exec };
            }
        }

        if (current.Any()) clusters.Add(current);

        return clusters;
    }

    // ── OCC symbol parser ─────────────────────────────────────────────────────

    private sealed record OccContract(string Underlying, DateOnly Expiration, decimal Strike, string Right);

    private static OccContract? TryParseOcc(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        symbol = symbol.Trim();
        if (symbol.Length < 15) return null;

        try
        {
            var i = 0;
            while (i < symbol.Length && !char.IsDigit(symbol[i])) i++;

            if (i >= symbol.Length - 14) return null;

            var underlying = symbol[..i].Trim();
            var dateStr = symbol.Substring(i, 6);
            var rightChar = symbol[i + 6].ToString().ToUpper();
            var strikeStr = symbol.Substring(i + 7, 8);

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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsOptionAction(string? action) =>
        action?.Contains("_TO_OPEN", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("to open", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("_TO_CLOSE", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("to close", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("EXPIRATION", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("EXERCISE", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("ASSIGNMENT", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSell(string? action) =>
        action?.StartsWith("SELL", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("Sell", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsBuy(string? action) =>
        action?.StartsWith("BUY", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("Buy", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsOpen(string? action) =>
        action?.Contains("_TO_OPEN", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("to open", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsClose(string? action) =>
        action?.Contains("_TO_CLOSE", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("to close", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAssignmentOrExpiry(string? action) =>
        action?.Contains("EXPIRATION", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("EXERCISE", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("ASSIGNMENT", StringComparison.OrdinalIgnoreCase) == true;

    private static string MakeFingerprint(Execution e)
    {
        var s = $"TastyTrade|{e.Account}|{e.ExecutedAt:O}|{e.Action}|{e.Symbol}|{e.Quantity}|{e.Price}|{e.NetAmount}";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
