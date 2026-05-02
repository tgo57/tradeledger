using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;
using Microsoft.EntityFrameworkCore;

namespace TradeLedger.Web.Services;

/// <summary>
/// Web-friendly Schwab CSV import service.
/// - Opening legs (Buy/Sell to Open) → create new TradeGroups
/// - Closing legs (Buy/Sell to Close) → match back to existing TradeGroup,
///   update CloseDate and NetPL
/// </summary>
public sealed class SchwabCsvImportService
{
    private readonly AppDbContext _db;
    public SchwabCsvImportService(AppDbContext db) => _db = db;

    public sealed record ImportResult(int Inserted, int Skipped, List<string> Errors);

    public async Task<ImportResult> ImportAsync(
        Stream csvStream,
        string broker,
        string account,
        string fileName = "upload.csv")
    {
        var inserted = 0;
        var skipped = 0;
        var errors = new List<string>();

        var tempPath = Path.Combine(Path.GetTempPath(), $"schwab_{Guid.NewGuid():N}.csv");

        try
        {
            await using (var fs = File.Create(tempPath))
                await csvStream.CopyToAsync(fs);

            var importer = new SchwabCsvImporter();
            var executions = importer.ImportExecutions(account, tempPath, out var summary);

            foreach (var w in summary.Warnings)
                errors.Add(w);

            // Split into opening and closing legs
            var optionExecs = executions
                .Where(e => SchwabOptionParser.TryParse(e.Symbol, out _))
                .ToList();

            var openingLegs = optionExecs.Where(e => IsOpen(e.Action)).ToList();
            var closingLegs = optionExecs.Where(e => IsClose(e.Action)).ToList();

            // ── Pass 1: Opening legs → create new TradeGroups ────────────────
            var openGroups = GroupByTime(openingLegs);

            foreach (var (group, legs) in openGroups)
            {
                try
                {
                    // Duplicate check via fingerprint of first leg
                    var fp = legs.FirstOrDefault()?.Fingerprint;
                    if (fp != null && _db.Executions.Any(e => e.Fingerprint == fp))
                    {
                        skipped++;
                        continue;
                    }

                    // Also check by unique index
                    var exists = _db.TradeGroups.Any(g =>
                        g.Broker == broker &&
                        g.Account == account &&
                        g.Underlying == group.Underlying &&
                        g.Expiration == group.Expiration &&
                        g.ShortStrike == group.ShortStrike &&
                        g.LongStrike == group.LongStrike &&
                        g.OpenDate.Date == group.OpenDate.Date);

                    if (exists) { skipped++; continue; }

                    _db.TradeGroups.Add(group);
                    await _db.SaveChangesAsync();

                    foreach (var exec in legs)
                    {
                        exec.SourceFile = fileName;
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
                    errors.Add($"Open group error: {ex.Message}");
                }
            }

            // ── Pass 2: Closing legs → match to existing TradeGroups ─────────
            var closeGroups = GroupByTime(closingLegs);

            foreach (var (_, legs) in closeGroups)
            {
                try
                {
                    SchwabOptionParser.TryParse(legs[0].Symbol, out var contract);
                    if (contract == null) continue;

                    // ✅ FIX: removed g.CloseDate == null so we can match trades
                    // that were already closed by the old CLI importer
                    var matchingGroup = await _db.TradeGroups
                        .Where(g =>
                            g.Broker == broker &&
                            g.Account == account &&
                            g.Underlying == contract.Underlying &&
                            g.Expiration == contract.Expiration &&
                            g.Right == (contract.Right == OptionRight.Put ? "Put" : "Call"))
                        .OrderByDescending(g => g.OpenDate)
                        .FirstOrDefaultAsync();

                    if (matchingGroup == null)
                    {
                        errors.Add($"No trade found to close: {legs[0].Symbol} on {legs[0].ExecutedAt:yyyy-MM-dd}");
                        continue;
                    }

                    // Skip if closing executions already linked
                    var fp = legs.FirstOrDefault()?.Fingerprint;
                    if (fp != null && _db.Executions.Any(e => e.Fingerprint == fp))
                        continue;

                    // Link closing executions to the trade group
                    foreach (var exec in legs)
                    {
                        exec.SourceFile = fileName;
                        _db.Executions.Add(exec);
                        await _db.SaveChangesAsync();

                        _db.TradeGroupExecutions.Add(new TradeGroupExecution
                        {
                            TradeGroupId = matchingGroup.Id,
                            ExecutionId = exec.Id
                        });
                    }

                    // ✅ FIX: only set CloseDate if not already set
                    if (matchingGroup.CloseDate == null)
                        matchingGroup.CloseDate = legs.Max(l => l.ExecutedAt);

                    // Always recalculate NetPL from ALL executions (open + close)
                    var allExecIds = await _db.TradeGroupExecutions
                        .Where(l => l.TradeGroupId == matchingGroup.Id)
                        .Select(l => l.ExecutionId)
                        .ToListAsync();

                    var allExecs = await _db.Executions
                        .Where(e => allExecIds.Contains(e.Id))
                        .ToListAsync();

                    matchingGroup.NetPL = allExecs.Sum(e => e.NetAmount);

                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    errors.Add($"Close group error: {ex.Message}");
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return new ImportResult(inserted, skipped, errors);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsOpen(string? action) =>
        action?.Contains("to open", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsClose(string? action) =>
        action?.Contains("to close", StringComparison.OrdinalIgnoreCase) == true ||
        action?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSell(string? action) =>
        action?.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase) == true;

    private static List<(TradeGroup Group, List<Execution> Legs)> GroupByTime(
        List<Execution> executions)
    {
        var result = new List<(TradeGroup, List<Execution>)>();
        if (!executions.Any()) return result;

        var sorted = executions.OrderBy(e => e.ExecutedAt).ToList();
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

            var parsed = cluster
                .Select(e =>
                {
                    SchwabOptionParser.TryParse(e.Symbol, out var c);
                    return (exec: e, contract: c);
                })
                .Where(x => x.contract != null)
                .ToList();

            if (!parsed.Any()) continue;

            var sellLeg = parsed.FirstOrDefault(x => IsSell(x.exec.Action));
            var buyLeg = parsed.FirstOrDefault(x => !IsSell(x.exec.Action));

            if (sellLeg.exec == null) sellLeg = parsed[0];
            if (buyLeg.exec == null) buyLeg = parsed.Count > 1 ? parsed[1] : parsed[0];

            var first = parsed.OrderBy(x => x.exec.ExecutedAt).First();
            var expiration = first.contract!.Expiration;
            var underlying = first.contract.Underlying;
            var right = first.contract.Right == OptionRight.Put ? "Put" : "Call";
            var openDate = first.exec.ExecutedAt;
            var netPL = cluster.Sum(e => e.NetAmount);

            DateTime? closeDate = null;
            if (expiration.ToDateTime(TimeOnly.MinValue) < DateTime.Today)
                closeDate = expiration.ToDateTime(TimeOnly.MinValue);

            var group = new TradeGroup
            {
                Broker = "Schwab",
                Account = cluster[0].Account ?? "",
                Underlying = underlying,
                Right = right,
                StrategyType = "CreditSpread",
                ShortStrike = sellLeg.contract?.Strike ?? 0m,
                LongStrike = buyLeg.contract?.Strike ?? 0m,
                Expiration = expiration,
                OpenDate = openDate,
                CloseDate = closeDate,
                NetPL = netPL,
                Setup = right == "Put" ? "BullPutSpread" : "BearCallSpread"
            };

            result.Add((group, cluster));
        }

        return result;
    }

    public async Task<ImportResult> ImportFromExecutionsAsync(
    List<Execution> executions,
    string broker,
    string account,
    string fileName = "api-sync")
    {
        var inserted = 0;
        var skipped = 0;
        var errors = new List<string>();

        var optionExecs = executions
            .Where(e => SchwabOptionParser.TryParse(e.Symbol, out _))
            .ToList();

        var openingLegs = optionExecs.Where(e => IsOpen(e.Action)).ToList();
        var closingLegs = optionExecs.Where(e => IsClose(e.Action)).ToList();

        // ── Pass 1: Opening legs ──────────────────────────────────────────────
        var openGroups = GroupByTime(openingLegs);

        foreach (var (group, legs) in openGroups)
        {
            try
            {
                var exists = _db.TradeGroups.Any(g =>
                    g.Broker == broker &&
                    g.Account == account &&
                    g.Underlying == group.Underlying &&
                    g.Expiration == group.Expiration &&
                    g.ShortStrike == group.ShortStrike &&
                    g.LongStrike == group.LongStrike);

                if (exists) { skipped++; continue; }

                _db.TradeGroups.Add(group);
                await _db.SaveChangesAsync();

                foreach (var exec in legs)
                {
                    exec.SourceFile = fileName;

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
                errors.Add($"Open group error: {ex.Message}");
            }
        }

        // ── Pass 2: Closing legs ──────────────────────────────────────────────
        var closeGroups = GroupByTime(closingLegs);

        foreach (var (_, legs) in closeGroups)
        {
            try
            {
                SchwabOptionParser.TryParse(legs[0].Symbol, out var contract);
                if (contract == null) continue;

                var matchingGroup = await _db.TradeGroups
                    .Where(g =>
                        g.Broker == broker &&
                        g.Account == account &&
                        g.Underlying == contract.Underlying &&
                        g.Expiration == contract.Expiration &&
                        g.Right == (contract.Right == OptionRight.Put ? "Put" : "Call"))
                    .OrderByDescending(g => g.OpenDate)
                    .FirstOrDefaultAsync();

                if (matchingGroup == null)
                {
                    errors.Add($"No trade found to close: {legs[0].Symbol} on {legs[0].ExecutedAt:yyyy-MM-dd}");
                    continue;
                }

                // Skip if this group already has CSV-sourced executions
                var hasNonApiExecs = await _db.TradeGroupExecutions
                    .Where(l => l.TradeGroupId == matchingGroup.Id)
                    .Join(_db.Executions, l => l.ExecutionId, e => e.Id, (l, e) => e)
                    .AnyAsync(e => e.SourceFile != "api-sync");

                if (hasNonApiExecs) continue;

                var fp = legs.FirstOrDefault()?.Fingerprint;
                if (fp != null && _db.Executions.Any(e => e.Fingerprint == fp))
                    continue;

                foreach (var exec in legs)
                {
                    exec.SourceFile = fileName;

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
                    matchingGroup.CloseDate = legs.Max(l => l.ExecutedAt);

                var allExecIds = await _db.TradeGroupExecutions
                    .Where(l => l.TradeGroupId == matchingGroup.Id)
                    .Select(l => l.ExecutionId)
                    .ToListAsync();

                var allExecs = await _db.Executions
                    .Where(e => allExecIds.Contains(e.Id))
                    .ToListAsync();

                matchingGroup.NetPL = allExecs.Sum(e => e.NetAmount);
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"Close group error: {ex.Message}");
            }
        }

        return new ImportResult(inserted, skipped, errors);
    }
}
