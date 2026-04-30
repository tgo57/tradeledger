using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;

namespace TradeLedger.Web.Services;

/// <summary>
/// Web-friendly Schwab CSV import service.
/// Accepts an uploaded Stream, parses executions using SchwabCsvImporter,
/// groups option legs into TradeGroups, and saves to the database.
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

            var optionExecs = executions
                .Where(e => SchwabOptionParser.TryParse(e.Symbol, out _))
                .ToList();

            if (!optionExecs.Any())
            {
                errors.Add("No option executions found in this file.");
                return new ImportResult(0, 0, errors);
            }

            var groups = GroupIntoTradeGroups(optionExecs, broker, account);

            foreach (var (group, legs) in groups)
            {
                try
                {
                    var fp = legs.FirstOrDefault()?.Fingerprint;
                    if (fp != null && _db.Executions.Any(e => e.Fingerprint == fp))
                    {
                        skipped++;
                        continue;
                    }

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
                    errors.Add($"Save error: {ex.Message}");
                }
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return new ImportResult(inserted, skipped, errors);
    }

    private static List<(TradeGroup Group, List<Execution> Legs)> GroupIntoTradeGroups(
        List<Execution> executions,
        string broker,
        string account)
    {
        var result = new List<(TradeGroup, List<Execution>)>();

        var parsed = executions
            .Select(e =>
            {
                SchwabOptionParser.TryParse(e.Symbol, out var contract);
                return (exec: e, contract);
            })
            .Where(x => x.contract != null)
            .ToList();

        var byContract = parsed
            .GroupBy(x => (x.contract!.Underlying, x.contract.Expiration, x.contract.Right));

        foreach (var contractGroup in byContract)
        {
            var legs = contractGroup.OrderBy(x => x.exec.ExecutedAt).ToList();

            var clusters = new List<List<(Execution exec, ParsedOptionContract contract)>>();
            var current = new List<(Execution, ParsedOptionContract)>();

            foreach (var leg in legs)
            {
                if (current.Count == 0 ||
                    Math.Abs((leg.exec.ExecutedAt - current.Last().Item1.ExecutedAt).TotalSeconds) < 60)
                {
                    current.Add((leg.exec, leg.contract!));
                }
                else
                {
                    clusters.Add(new List<(Execution, ParsedOptionContract)>(current));
                    current = new List<(Execution, ParsedOptionContract)> { (leg.exec, leg.contract!) };
                }
            }
            if (current.Any()) clusters.Add(current);

            foreach (var cluster in clusters)
            {
                if (cluster.Count == 0) continue;

                var sellLeg = cluster.FirstOrDefault(x =>
                    x.Item1.Action?.StartsWith("Sell", StringComparison.OrdinalIgnoreCase) == true);
                var buyLeg = cluster.FirstOrDefault(x =>
                    x.Item1.Action?.StartsWith("Buy", StringComparison.OrdinalIgnoreCase) == true);

                if (sellLeg.Item1 == null) sellLeg = cluster[0];
                if (buyLeg.Item1 == null) buyLeg = cluster.Count > 1 ? cluster[1] : cluster[0];

                var first = cluster.OrderBy(x => x.Item1.ExecutedAt).First();
                var expiration = first.Item2.Expiration;
                var underlying = first.Item2.Underlying;
                var right = first.Item2.Right == OptionRight.Put ? "Put" : "Call";
                var openDate = first.Item1.ExecutedAt;

                // Fix: NetAmount is decimal? so cast before null-coalescing
                var netPL = cluster.Sum(x => x.Item1.NetAmount);
                DateTime? closeDate = null;
                if (expiration.ToDateTime(TimeOnly.MinValue) < DateTime.Today)
                    closeDate = expiration.ToDateTime(TimeOnly.MinValue);

                var group = new TradeGroup
                {
                    Broker = broker,
                    Account = account,
                    Underlying = underlying,
                    Right = right,
                    StrategyType = "CreditSpread",
                    ShortStrike = sellLeg.Item2?.Strike ?? 0m,
                    LongStrike = buyLeg.Item2?.Strike ?? 0m,
                    Expiration = expiration,
                    OpenDate = openDate,
                    CloseDate = closeDate,
                    NetPL = netPL,
                    Setup = right == "Put" ? "BullPutSpread" : "BearCallSpread"
                };

                var execLegs = cluster.Select(x => x.Item1).ToList();
                result.Add((group, execLegs));
            }
        }

        return result;
    }
}