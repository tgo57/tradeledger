using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;

namespace TradeLedger.Cli.Grouping;

internal static class GroupSpreads
{
    public static (int Created, int Linked) Run(AppDbContext ctx, string broker, string account)
    {
        var execs = ctx.Executions
            .Where(e => e.Broker == broker && e.Account == account)
            .OrderBy(e => e.ExecutedAt)
            .ToList();

        var opt = execs
            .Select(e =>
            {
                var ok = SchwabOptionParser.TryParse(e.Symbol, out var c);
                return new { Exec = e, Contract = ok ? c : null };
            })
            .Where(x => x.Contract != null)
            .Select(x => new { x.Exec, Contract = x.Contract! })
            .ToList();

        static bool IsOpen(string action) => action.Contains("to open", StringComparison.OrdinalIgnoreCase);
        static bool IsClose(string action) =>
                action.Contains("to close", StringComparison.OrdinalIgnoreCase)
            ||  action.Contains("expired", StringComparison.OrdinalIgnoreCase);

        static bool IsSell(string action) => action.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase);
        static bool IsBuy(string action) => action.TrimStart().StartsWith("buy", StringComparison.OrdinalIgnoreCase);

        var opens = opt.Where(x => IsOpen(x.Exec.Action)).ToList();

        var openGroups = opens.GroupBy(x => new
        {
            //Date = x.Exec.ExecutedAt.Date,
            x.Contract.Underlying,
            x.Contract.Expiration,
            Right = x.Contract.Right.ToString()
        });

        int created = 0;
        int linked = 0;

        foreach (var g in openGroups)
        {
            var sto = g.Where(x => IsSell(x.Exec.Action)).ToList();
            var bto = g.Where(x => IsBuy(x.Exec.Action)).ToList();

            foreach (var s in sto)
            {
                var qty = s.Exec.Quantity ?? 0m;
                if (qty == 0m) continue;

                var match = bto
                    .Where(x => (x.Exec.Quantity ?? 0m) == qty)
                    .OrderBy(x => Math.Abs((double)(x.Contract.Strike - s.Contract.Strike)))
                    .FirstOrDefault();

                if (match == null) continue;
                bto.Remove(match);

                var shortStrike = s.Contract.Strike;
                var longStrike = match.Contract.Strike;

                bool exists = ctx.TradeGroups.Any(t =>
                    t.Broker == broker &&
                    t.Account == account &&
                    t.StrategyType == "CreditSpread" &&
                    t.Underlying == g.Key.Underlying &&
                    t.Expiration == g.Key.Expiration &&
                    t.Right == g.Key.Right &&
                    t.ShortStrike == shortStrike &&
                    t.LongStrike == longStrike);


                if (exists) continue;

                var tg = new TradeGroup
                {
                    Broker = broker,
                    Account = account,
                    StrategyType = "CreditSpread",
                    Underlying = g.Key.Underlying,
                    Expiration = g.Key.Expiration,
                    Right = g.Key.Right,
                    ShortStrike = shortStrike,
                    LongStrike = longStrike,
                    OpenDate = s.Exec.ExecutedAt.Date,
                    NetPL = 0m
                };

                ctx.TradeGroups.Add(tg);
                ctx.SaveChanges();

                ctx.TradeGroupExecutions.Add(new TradeGroupExecution { TradeGroupId = tg.Id, ExecutionId = s.Exec.Id });
                ctx.TradeGroupExecutions.Add(new TradeGroupExecution { TradeGroupId = tg.Id, ExecutionId = match.Exec.Id });
                linked += 2;

                var closes = opt.Where(x =>
                        IsClose(x.Exec.Action) &&
                        x.Contract.Underlying == tg.Underlying &&
                        x.Contract.Expiration == tg.Expiration &&
                        x.Contract.Right.ToString() == tg.Right &&
                        (x.Contract.Strike == tg.ShortStrike || x.Contract.Strike == tg.LongStrike))
                    .ToList();

                foreach (var c in closes)
                {
                    bool alreadyLinked = ctx.TradeGroupExecutions
                        .Any(l => l.TradeGroupId == tg.Id && l.ExecutionId == c.Exec.Id);

                    if (!alreadyLinked)
                    {
                        ctx.TradeGroupExecutions.Add(new TradeGroupExecution { TradeGroupId = tg.Id, ExecutionId = c.Exec.Id });
                        linked++;
                    }
                }

                ctx.SaveChanges();

                var netAmounts = (from l in ctx.TradeGroupExecutions
                                  join e in ctx.Executions on l.ExecutionId equals e.Id
                                  where l.TradeGroupId == tg.Id
                                  select (decimal?)e.NetAmount).AsEnumerable();

                tg.NetPL = netAmounts.Sum() ?? 0m;

                var execsForGroup =
                                from l in ctx.TradeGroupExecutions
                                join e in ctx.Executions on l.ExecutionId equals e.Id
                                where l.TradeGroupId == tg.Id
                                select e;
                var entryCredit = execsForGroup
                                .Where(e =>
                                e.Action.Contains("to open", StringComparison.OrdinalIgnoreCase) &&
                                e.Action.StartsWith("sell", StringComparison.OrdinalIgnoreCase))
                                .Sum(e => (e.Price ?? 0m) * Math.Abs(e.Quantity ?? 0m) * 100m);
                var exitDebit = execsForGroup
                                .Where(e =>
                                    e.Action.Contains("to close", StringComparison.OrdinalIgnoreCase) &&
                                    e.Action.StartsWith("buy", StringComparison.OrdinalIgnoreCase))
                                .Sum(e => (e.Price ?? 0m) * Math.Abs(e.Quantity ?? 0m) * 100m);

                tg.GrossReturn = entryCredit - exitDebit;


                var closeDates = (from l in ctx.TradeGroupExecutions
                                  join e in ctx.Executions on l.ExecutionId equals e.Id
                                  where l.TradeGroupId == tg.Id
                                  select new { e.ExecutedAt, e.Action })
                     .AsEnumerable()
                     .Where(x => IsClose(x.Action))
                     .Select(x => x.ExecutedAt.Date)
                     .ToList();

                tg.CloseDate = closeDates.Count > 0 ? closeDates.Max() : null;

                ctx.SaveChanges();
                created++;
            }
        }

        return (created, linked);
    }
}
