using Microsoft.EntityFrameworkCore;
using MudBlazor.Extensions;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;


namespace TradeLedger.Web.Services;


public sealed class DashboardService
{
    private readonly AppDbContext _db;
    public DashboardService(AppDbContext db) => _db = db;


    public async Task<DashboardVm> GetDashboard(string broker, string account, int take = 200)
    {
        var groups = await _db.TradeGroups
            .Where(g => g.Broker == broker && g.Account == account)
            .OrderByDescending(g => g.OpenDate)
            .Take(take)
            .Select(g => new TradeRow
            {
                Id = g.Id,
                StrategyType = g.StrategyType,
                Setup = g.Setup,
                Underlying = g.Underlying,
                Expiration = g.Expiration,
                Right = g.Right,
                ShortStrike = g.ShortStrike,
                LongStrike = g.LongStrike,
                OpenDate = g.OpenDate,
                CloseDate = g.CloseDate,

                // We will override NetPL below using executions (Schwab Amount is authoritative)
                NetPL = g.NetPL,
            })
            .ToListAsync();

        var groupIds = groups.Select(g => g.Id).ToList();

        // Pull executions for those groups (single query)
        var execRows = await (
            from l in _db.TradeGroupExecutions
            join e in _db.Executions on l.ExecutionId equals e.Id
            where groupIds.Contains(l.TradeGroupId)
            select new
            {
                l.TradeGroupId,
                e.Action,
                e.Symbol,
                e.Quantity,
                e.Price,     // ✅ REQUIRED to reconstruct TraderSync "Return $" (gross before fees)
                e.NetAmount, // Schwab CSV "Amount" = AFTER fees
                e.Fees       // Schwab CSV "Fees & Comm" (positive), but normalize anyway
            }
        ).ToListAsync();

        static bool IsOpen(string action) =>
            action.Contains("to open", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("_TO_OPEN", StringComparison.OrdinalIgnoreCase);

        static bool IsClose(string action) =>
            action.Contains("to close", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("_TO_CLOSE", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("expired", StringComparison.OrdinalIgnoreCase);

        static bool IsSell(string action) => action.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        static bool IsBuy(string action) => action.TrimStart().StartsWith("buy", StringComparison.OrdinalIgnoreCase);

        // Group executions in memory so we can parse symbols
        var execByGroup = execRows
            .GroupBy(x => x.TradeGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tr in groups)
        {
            if (!execByGroup.TryGetValue(tr.Id, out var ex) || ex.Count == 0)
            {
                tr.TotalFees = 0m;
                tr.ReturnGross = 0m;     // no executions → no gross
                tr.NetPL = 0m;           // no executions → no net
                tr.NetReturnPct = null;
                continue;
            }

            // -----------------------------------------
            // Schwab semantics (locked):
            // NetAmount (CSV Amount) is AFTER fees.
            // Fees is a positive cost (normalize anyway).
            // TraderSync "Return $" = BEFORE fees.
            // -----------------------------------------

            // Fees as positive cost
            var feesCost = ex.Sum(x => Math.Abs(x.Fees));
            tr.TotalFees = feesCost;

            var netAfterFees = ex.Sum(x => x.NetAmount);
            tr.NetPL = netAfterFees;

            // Gross BEFORE fees (TraderSync "Return $") reconstructed from price & qty
            // SELL => +, BUY => -
            decimal grossBeforeFees = 0m;
            if (broker.Equals("TastyTrade", StringComparison.OrdinalIgnoreCase))
            {
                // TastyTrade: sum all execution net amounts (includes cash settlements)
                grossBeforeFees = ex.Sum(x => x.NetAmount);
            }
            else
            {
                // Schwab: reconstruct from price × qty × 100
                foreach (var x in ex)
                {
                    var qty = Math.Abs(x.Quantity.GetValueOrDefault());
                    var price = x.Price.GetValueOrDefault();
                    var leg = qty * price * 100m;
                    grossBeforeFees += IsSell(x.Action) ? leg : -leg;
                }
            }
            tr.ReturnGross = grossBeforeFees;

            // Optional sanity check: gross - fees ≈ net
            // (Don’t throw in prod; just debug/log if you want)
            // var check = grossBeforeFees - feesCost;
            // if (Math.Abs(check - netAfterFees) > 0.02m) { /* log */ }

            // Net Return % only makes sense for CLOSED CreditSpreads
            if (tr.CloseDate is null ||
                !string.Equals(tr.StrategyType, "CreditSpread", StringComparison.OrdinalIgnoreCase))
            {
                tr.NetReturnPct = null;
                continue;
            }

            // TraderSync-style: Net Return % = NetPL(after all fees) / EntryCredit(before fees)
            var open = ex.Where(x => IsOpen(x.Action)).ToList();

            // Entry credit BEFORE fees must also be reconstructed from price/qty
            // (This matches TraderSync and avoids mixing Amount-with-fees)
            decimal entryCreditBeforeFees = 0m;
            if (broker.Equals("TastyTrade", StringComparison.OrdinalIgnoreCase))
            {
                entryCreditBeforeFees = open.Sum(x => x.NetAmount);
            }
            else
            {
                foreach (var x in open)
                {
                    var qty = Math.Abs(x.Quantity.GetValueOrDefault());
                    var price = x.Price.GetValueOrDefault();
                    var leg = qty * price * 100m;
                    entryCreditBeforeFees += IsSell(x.Action) ? leg : -leg;
                }
            }

            if (entryCreditBeforeFees <= 0m)
            {
                tr.NetReturnPct = null;
            }
            else
            {
                tr.NetReturnPct = (tr.NetPL / entryCreditBeforeFees) * 100m;
            }
        }

        var closed = groups.Where(g => g.CloseDate != null).ToList();

        // TraderSync-style accumulative return (sum of per-trade Return $)
        var grossReturn = closed.Sum(x => x.ReturnGross);

        var totalPL = closed.Sum(x => x.NetPL);
        var wins = closed.Count(x => x.NetPL > 0m);
        var losses = closed.Count(x => x.NetPL < 0m);
        var trades = closed.Count;

        var winRate = trades == 0 ? 0m : (decimal)wins / trades;

        var grossWin = closed.Where(x => x.NetPL > 0m).Sum(x => x.NetPL);
        var grossLoss = closed.Where(x => x.NetPL < 0m).Sum(x => -x.NetPL);
        var profitFactor = grossLoss == 0m ? (grossWin > 0m ? 99m : 0m) : grossWin / grossLoss;

        var avgPL = trades == 0 ? 0m : totalPL / trades;

        // Equity curve drawdown (close-date order)
        var equityPts = closed
            .OrderBy(x => x.CloseDate!.Value.Date)
            .ThenBy(x => x.Id)
            .Select(x => x.NetPL)
            .ToList();

        decimal equity = 0m;
        decimal peak = 0m;
        decimal maxDrawdown = 0m;

        foreach (var pl in equityPts)
        {
            equity += pl;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDrawdown) maxDrawdown = dd;
        }

        var byYear = closed
            .GroupBy(x => x.CloseDate!.Value.Year)
            .OrderBy(g => g.Key)
            .Select(g => new BucketRow(g.Key.ToString(), g.Sum(x => x.NetPL)))
            .ToList();

        var byMonth = closed
            .GroupBy(x => new { x.CloseDate!.Value.Year, x.CloseDate!.Value.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new BucketRow($"{g.Key.Year}-{g.Key.Month:00}", g.Sum(x => x.NetPL)))
            .ToList();

        var byDte = closed
            .Select(x =>
            {
                var dte = (x.Expiration.ToDateTime(TimeOnly.MinValue) - x.OpenDate.Date).Days;
                return new { Bucket = DteBucket(dte), x.NetPL, Dte = dte };
            })
            .GroupBy(x => x.Bucket)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var gw = g.Where(x => x.NetPL > 0m).Sum(x => x.NetPL);
                var gl = g.Where(x => x.NetPL < 0m).Sum(x => -x.NetPL);
                var pf = gl == 0m ? (gw > 0m ? 99m : 0m) : gw / gl;

                return new DteBucketRow(
                    Bucket: g.Key,
                    Trades: g.Count(),
                    TotalPL: g.Sum(x => x.NetPL),
                    ProfitFactor: pf,
                    MinDte: g.Min(x => x.Dte),
                    MaxDte: g.Max(x => x.Dte)
                );
            })
            .ToList();

        return new DashboardVm
        {
            Broker = broker,
            Account = account,

            Kpi_TotalPL = totalPL,           // after-fees
            Kpi_GrossReturn = grossReturn,   // before-fees (TraderSync "Return $")
            Kpi_ProfitFactor = profitFactor,
            Kpi_WinRate = winRate,
            Kpi_AvgPL = avgPL,
            Kpi_MaxDrawdown = maxDrawdown,
            Kpi_Trades = trades,

            Trades = groups,
            ByYear = byYear,
            ByMonth = byMonth,
            ByDteBucket = byDte
        };
    }

    private static string DteBucket(int dte)
    {
        if (dte <= 1) return "0-1";
        if (dte <= 3) return "2-3";
        if (dte <= 7) return "4-7";
        if (dte <= 14) return "8-14";
        if (dte <= 30) return "15-30";
        return "31+";
    }
}

public sealed class DashboardVm
{
    public string Broker { get; set; } = "";
    public string Account { get; set; } = "";

    public decimal Kpi_TotalPL { get; set; }      // after fees (Net P/L)
    public decimal Kpi_GrossReturn { get; set; }  // before fees (TraderSync-style)

    public decimal Kpi_ProfitFactor { get; set; }
    public decimal Kpi_WinRate { get; set; }
    public decimal Kpi_AvgPL { get; set; }
    public decimal Kpi_MaxDrawdown { get; set; }
    public int Kpi_Trades { get; set; }

    public List<TradeRow> Trades { get; set; } = new();

    public List<BucketRow> ByYear { get; set; } = new();
    public List<BucketRow> ByMonth { get; set; } = new();
    public List<DteBucketRow> ByDteBucket { get; set; } = new();
}

public sealed class TradeRow
{
    public long Id { get; set; }
    public string StrategyType { get; set; } = "";
    public string Setup { get; set; } = "";
    public string Underlying { get; set; } = "";
    public DateOnly Expiration { get; set; }
    public string Right { get; set; } = "";
    public decimal ShortStrike { get; set; }
    public decimal LongStrike { get; set; }
    public DateTime OpenDate { get; set; }
    public DateTime? CloseDate { get; set; }
    public decimal NetPL { get; set; } // your current stored NetPL (after-fee)

    // NEW (dashboard display)
    public decimal TotalFees { get; set; }        // Σ Fees
    public decimal ReturnGross { get; set; }      // NetPL + Fees  (matches ToS/TraderSync "Return $")
    public decimal? NetReturnPct { get; set; }    // NetPL / MaxRisk * 100 (CreditSpread only)
}


public sealed record BucketRow(string Label, decimal Value);

public sealed record DteBucketRow(string Bucket, int Trades, decimal TotalPL, decimal ProfitFactor, int MinDte, int MaxDte);
