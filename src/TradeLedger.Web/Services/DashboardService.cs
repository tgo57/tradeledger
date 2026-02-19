using Microsoft.EntityFrameworkCore;
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
                e.NetAmount,
                e.Fees
            }
        ).ToListAsync();

        static bool IsOpen(string action) => action.Contains("to open", StringComparison.OrdinalIgnoreCase);
        static bool IsClose(string action) =>
                action.Contains("to close", StringComparison.OrdinalIgnoreCase)
            || action.Contains("expired", StringComparison.OrdinalIgnoreCase);
        static bool IsSell(string action) => action.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        static bool IsBuy(string action) => action.TrimStart().StartsWith("buy", StringComparison.OrdinalIgnoreCase);

        // Group executions in memory so we can parse symbols
        var execByGroup = execRows
            .GroupBy(x => x.TradeGroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tr in groups)
        {
            if (!execByGroup.TryGetValue(tr.Id, out var ex))
            {
                tr.TotalFees = 0m;
                tr.ReturnGross = tr.NetPL + tr.TotalFees;
                tr.NetReturnPct = null;
                continue;
            }

            // Fees + gross return (this is the one you want to match ToS/TraderSync)
            tr.TotalFees = ex.Sum(x => x.Fees);
            tr.ReturnGross = tr.NetPL + tr.TotalFees;

            // Net Return % only makes sense for CLOSED CreditSpreads
            if (tr.CloseDate is null ||
                !string.Equals(tr.StrategyType, "CreditSpread", StringComparison.OrdinalIgnoreCase))
            {
                tr.NetReturnPct = null;
                continue;
            }

            // TraderSync-style: Net Return % = NetPL(after all fees) / EntryCredit(before fees)
            var open = ex.Where(x => IsOpen(x.Action)).ToList();

            var entryNetAfterFees = open.Sum(x => x.NetAmount); // sells +, buys -
            var openFees = open.Sum(x => x.Fees);

            // Entry credit "before fees" (this makes 1062.80 + 12.20 = 1075.00 on your example)
            var entryCreditBeforeFees = entryNetAfterFees + openFees;

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
        // NOTE: ReturnGross = NetPL + TotalFees (you already computed it above)
        var grossReturn = closed.Sum(x => x.ReturnGross);


        var totalPL = closed.Sum(x => x.NetPL);
        var wins = closed.Count(x => x.NetPL > 0m);
        var losses = closed.Count(x => x.NetPL < 0m);
        // NEW (matches what you see in the table)
        var trades = closed.Count;
       
        var winRate = trades == 0 ? 0m : (decimal)wins / trades;

        var grossWin = closed.Where(x => x.NetPL > 0m).Sum(x => x.NetPL);
        var grossLoss = closed.Where(x => x.NetPL < 0m).Sum(x => -x.NetPL);
        var profitFactor = grossLoss == 0m ? 0m : grossWin / grossLoss;

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
                var pf = gl == 0m ? 0m : gw / gl;

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

            Kpi_TotalPL = totalPL,
            Kpi_GrossReturn = grossReturn,
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
