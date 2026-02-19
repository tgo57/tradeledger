using Microsoft.EntityFrameworkCore;
using TradeLedger.Cli.Grouping;
using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;






static string? GetArg(string[] args, string name)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            if (i + 1 < args.Length) return args[i + 1];
            return null;
        }
    }
    return null;
}

var rawDb = GetArg(args, "--db") ?? "tradeledger_ef.db";
var resolvedDb = DbFactory.ResolvePath(rawDb);

void PrintDb() => Console.WriteLine($"DB: {resolvedDb}");


static bool HasArg(string[] args, string name) =>
    args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

static void PrintUsage()
{
    Console.WriteLine("TradeLedger CLI");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  import-schwab --account <label> --file <path> [--db <path>]");
    Console.WriteLine("  import-tastytrade --account <label> --file <path> [--db <path>]");
    Console.WriteLine("  list-exec --account <label> [--broker <name>] [--take <n>] [--db <path>]");
    Console.WriteLine("  scan-options --account <label> [--broker <name>] [--take <n>] [--db <path>]");
    Console.WriteLine("  group-spreads --account <label> [--broker <name>] [--db <path>]");
    Console.WriteLine("  list-groups --account <label> [--broker <name>] [--take <n>] [--strategy <name>] [--open-only] [--db <path>]");
    Console.WriteLine("  list-group-exec --group <id> [--db <path>]");
    Console.WriteLine();
    Console.WriteLine("  clear-groups [--db <path>]");
    Console.WriteLine("  schema-exec [--db <path>]");
    Console.WriteLine("  schema-groups [--db <path>]");
    Console.WriteLine("  fk-check [--db <path>]");
    Console.WriteLine("  fk-list [--db <path>]");
    Console.WriteLine();
    Console.WriteLine("  stats-dte --account <label> [--broker <name>] [--db <path>]");
    Console.WriteLine("  stats-daily --account <label> [--broker <name>] [--db <path>]");
    Console.WriteLine();
    Console.WriteLine("  db-tables [--db <path>]");
    Console.WriteLine();
    Console.WriteLine("  reset-schwab --account <label> --file <path> [--db <path>] --force");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine(@"  import-schwab --account Schwab1 --file ""E:\Exports\schwab.csv""");
    Console.WriteLine(@"  import-schwab --account Schwab1 --file ""E:\Exports\schwab.csv"" --db ""E:\Projects\TradeLedger\db\tradeledger_ef.db""");
    Console.WriteLine(@"  list-exec --account Schwab1 --take 25");
    Console.WriteLine(@"  list-groups --account Schwab1 --open-only");
    Console.WriteLine(@"  list-groups --account Schwab1 --strategy CreditSpread");
    Console.WriteLine(@"  reset-schwab --account Schwab1 --file ""E:\schwab.csv"" --force");
    Console.WriteLine("  group-bwb --account <label> [--broker <name>] [--db <path>]");


}

if (args.Length == 0 || HasArg(args, "--help") || HasArg(args, "-h"))
{
    PrintUsage();
    return;
}

var command = args[0];

if (string.Equals(command, "import-schwab", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account");
    var file = GetArg(args, "--file");
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(file))
    {
        Console.WriteLine("Error: --account and --file are required.");
        Console.WriteLine();
        PrintUsage();
        Environment.ExitCode = 2;
        return;
    }

    if (!File.Exists(file))
    {
        Console.WriteLine($"Error: CSV file not found: {file}");
        Environment.ExitCode = 4;
        return;
    }

    var importer = new SchwabCsvImporter();
    var executions = importer.ImportExecutions(account, file, out var summary);

    
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    // 1) Remove any blank fingerprints (safety)
    executions = executions.Where(e => !string.IsNullOrWhiteSpace(e.Fingerprint)).ToList();

    // 2) Skip ones already in DB (idempotent import)
    var fpSet = executions.Select(e => e.Fingerprint).ToHashSet();

    var existing = ctx.Executions
        .Where(e => e.Broker == "Schwab" && e.Account == account && fpSet.Contains(e.Fingerprint))
        .Select(e => e.Fingerprint)
        .ToHashSet();

    var toInsert = executions.Where(e => !existing.Contains(e.Fingerprint)).ToList();

    ctx.Executions.AddRange(toInsert);
    ctx.SaveChanges();

    Console.WriteLine($"Inserted: {toInsert.Count}");
    Console.WriteLine($"Skipped duplicates: {executions.Count - toInsert.Count}");

    Console.WriteLine($"Rows read: {summary.RowsRead}");
    Console.WriteLine($"Executions parsed: {summary.ExecutionsCreated}");
    Console.WriteLine($"Executions inserted: {toInsert.Count}");
    Console.WriteLine($"Duplicates skipped: {executions.Count - toInsert.Count}");
    PrintDb();
    if (summary.Warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Warnings:");
        foreach (var w in summary.Warnings.Distinct())
            Console.WriteLine($"- {w}");
    }

    return;
}

if (string.Equals(command, "reset-schwab", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account");
    var file = GetArg(args, "--file");
    var force = HasArg(args, "--force");
    var broker = GetArg(args, "--broker") ?? "Schwab"; // optional, default Schwab

    if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(file))
    {
        Console.WriteLine("Error: --account and --file are required.");
        Console.WriteLine();
        PrintUsage();
        Environment.ExitCode = 2;
        return;
    }

    if (!force)
    {
        Console.WriteLine("Refusing to run without --force.");
        Console.WriteLine("This command DELETEs ALL Executions + TradeGroups (full reset).");
        Environment.ExitCode = 2;
        return;
    }

    if (!File.Exists(file))
    {
        Console.WriteLine($"Error: CSV file not found: {file}");
        Environment.ExitCode = 4;
        return;
    }

    using var ctx = DbFactory.Create(rawDb);
    PrintDb();

    Console.WriteLine($"RESET: Clearing data for {broker}/{account}...");

    // 1) delete links for groups in this account/broker
    ctx.Database.ExecuteSqlRaw(@"
DELETE FROM TradeGroupExecutions
WHERE TradeGroupId IN (
  SELECT Id FROM TradeGroups WHERE Broker = {0} AND Account = {1}
);", broker, account);

    // 2) delete legs for groups in this account/broker
    ctx.Database.ExecuteSqlRaw(@"
DELETE FROM TradeGroupLegs
WHERE TradeGroupId IN (
  SELECT Id FROM TradeGroups WHERE Broker = {0} AND Account = {1}
);", broker, account);

    // 3) delete groups
    ctx.Database.ExecuteSqlRaw(
        "DELETE FROM TradeGroups WHERE Broker = {0} AND Account = {1};",
        broker, account);

    // 4) delete executions for this account/broker
    ctx.Database.ExecuteSqlRaw(
        "DELETE FROM Executions WHERE Broker = {0} AND Account = {1};",
        broker, account);

    Console.WriteLine("RESET: Cleared.");

    // 5) OPTIONAL: reset identity counters *only if tables are empty*
    // (SQLite sequences are global per table; only reset when table has no rows.)
    var tgEmpty = !ctx.TradeGroups.AsNoTracking().Any();
    var legEmpty = !ctx.TradeGroupLegs.AsNoTracking().Any();
    var exEmpty = !ctx.Executions.AsNoTracking().Any();
    var linkEmpty = !ctx.TradeGroupExecutions.AsNoTracking().Any();

    if (tgEmpty && legEmpty && exEmpty && linkEmpty)
    {
        ctx.Database.ExecuteSqlRaw(@"
DELETE FROM sqlite_sequence
WHERE name IN ('TradeGroups','TradeGroupLegs','Executions','TradeGroupExecutions');
");
    }



    Console.WriteLine("RESET: Importing Schwab CSV...");

    var importer = new SchwabCsvImporter();
    var executions = importer.ImportExecutions(account, file, out var summary);

    // safety
    executions = executions.Where(e => !string.IsNullOrWhiteSpace(e.Fingerprint)).ToList();

    // DB is empty, so insert all
    ctx.Executions.AddRange(executions);
    ctx.SaveChanges();

    Console.WriteLine($"Rows read: {summary.RowsRead}");
    Console.WriteLine($"Executions parsed: {summary.ExecutionsCreated}");
    Console.WriteLine($"Executions inserted: {executions.Count}");

    if (summary.Warnings.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Warnings:");
        foreach (var w in summary.Warnings.Distinct())
            Console.WriteLine($"- {w}");
    }

    Console.WriteLine();
    Console.WriteLine("RESET: Grouping spreads...");

    // Run the SAME logic as group-spreads.
    // Since you have it inline, we just invoke it by duplicating the call pattern:
    // (Yes, later you should refactor to a shared method, but this works now.)

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
            || action.Contains("expired", StringComparison.OrdinalIgnoreCase);
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

            // DB is empty for groups now, so exists check is optional, but keep it
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

            // GrossReturn (TraderSync-style for your app): NetPL + total fees
            var execRows = (
                from l in ctx.TradeGroupExecutions
                join e in ctx.Executions on l.ExecutionId equals e.Id
                where l.TradeGroupId == tg.Id
                select new { e.NetAmount, e.Fees }
            ).ToList(); // materialize => no EF translation issues

            var totalFees = execRows.Sum(x => x.Fees);

            // NetPL is already computed above as sum(NetAmount) (after-fee in your model),
            // so gross is net + fees
            tg.GrossReturn = tg.NetPL + totalFees;




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

    Console.WriteLine($"TradeGroups created: {created}");
    Console.WriteLine($"Execution links created: {linked}");
    PrintDb();

    Console.WriteLine();
    Console.WriteLine("RESET: Completed.");
    return;
}


if (string.Equals(command, "import-tastytrade", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account");
    var file = GetArg(args, "--file");
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(file))
    {
        Console.WriteLine("Error: --account and --file are required.");
        Environment.ExitCode = 2;
        return;
    }

    if (!File.Exists(file))
    {
        Console.WriteLine($"Error: CSV file not found: {file}");
        Environment.ExitCode = 4;
        return;
    }

    var importer = new TradeLedger.Importers.Tastytrade.TastytradeCsvImporter();
    var executions = importer.ImportExecutions(account, file, out var summary);

   
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    foreach (var e in executions)
    {
        e.Broker = "tastytrade";
        e.Account = account;
    }

    executions = executions
        .Where(e => !string.IsNullOrWhiteSpace(e.Fingerprint))
        .ToList();

    var fpSet = executions.Select(e => e.Fingerprint).ToHashSet();

    var existing = ctx.Executions
        .Where(e => e.Broker == "tastytrade" && e.Account == account && fpSet.Contains(e.Fingerprint))
        .Select(e => e.Fingerprint)
        .ToHashSet();

    var toInsert = executions.Where(e => !existing.Contains(e.Fingerprint)).ToList();

    ctx.Executions.AddRange(toInsert);
    ctx.SaveChanges();

    Console.WriteLine($"Inserted: {toInsert.Count}");
    Console.WriteLine($"Rows read: {summary.RowsRead}");
    Console.WriteLine($"Executions parsed: {summary.ExecutionsCreated}");
    PrintDb();

    return;
}


if (string.Equals(command, "list-exec", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var takeStr = GetArg(args, "--take");
    var take = int.TryParse(takeStr, out var t) ? t : 50;

    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var rows = ctx.Executions
        .Where(e => e.Broker == broker && e.Account == account)
        .OrderByDescending(e => e.ExecutedAt)
        .Take(take)
        .Select(e => new { e.ExecutedAt, e.Action, e.Symbol, e.Quantity, e.Price, e.NetAmount, e.Fees })
        .ToList();

    foreach (var r in rows)
        Console.WriteLine($"{r.ExecutedAt:yyyy-MM-dd HH:mm:ss} | {r.Action,-12} | {r.Symbol,-20} | {r.Quantity,8} | {r.Price,10} | {r.NetAmount,10} | {r.Fees,8}");

    return;
}

if (string.Equals(command, "scan-options", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var takeStr = GetArg(args, "--take");
    var take = int.TryParse(takeStr, out var t) ? t : 50;
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var rows = ctx.Executions
        .Where(e => e.Broker == broker && e.Account == account)
        .OrderByDescending(e => e.ExecutedAt)
        .Take(take)
        .Select(e => new { e.ExecutedAt, e.Action, e.Symbol, e.Quantity, e.NetAmount })
        .ToList();

    foreach (var r in rows)
    {
        if (SchwabOptionParser.TryParse(r.Symbol, out var c) && c is not null)
        {
            Console.WriteLine($"{r.ExecutedAt:yyyy-MM-dd} | {r.Action,-12} | {c.Underlying} {c.Expiration:yyyy-MM-dd} {c.Strike} {c.Right} | Qty={r.Quantity} | Net={r.NetAmount}");
        }
        else
        {
            Console.WriteLine($"{r.ExecutedAt:yyyy-MM-dd} | {r.Action,-12} | (non-option) '{r.Symbol}' | Net={r.NetAmount}");
        }
    }

    return;
}


if (string.Equals(command, "group-spreads", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var (created, linked) = GroupSpreads.Run(ctx, broker, account);

    Console.WriteLine($"TradeGroups created: {created}");
    Console.WriteLine($"Execution links created: {linked}");
    PrintDb();
    return;
}


if (string.Equals(command, "group-bwb", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    static bool IsOpen(string action) => action.Contains("to open", StringComparison.OrdinalIgnoreCase);
    static bool IsClose(string action) =>
                action.Contains("to close", StringComparison.OrdinalIgnoreCase)
            || action.Contains("expired", StringComparison.OrdinalIgnoreCase);
    static bool IsSell(string action) => action.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase);
    static bool IsBuy(string action) => action.TrimStart().StartsWith("buy", StringComparison.OrdinalIgnoreCase);

    static bool TryParseOption(string broker, string symbol, out string und, out DateOnly exp, out decimal strike, out string right)
    {
        und = ""; exp = default; strike = 0m; right = "";

        if (broker.Equals("Schwab", StringComparison.OrdinalIgnoreCase))
        {
            if (!SchwabOptionParser.TryParse(symbol, out var c) || c is null) return false;
            und = c.Underlying;
            exp = c.Expiration;
            strike = c.Strike;
            right = c.Right.ToString(); // "Put" / "Call"
            return true;
        }

        // TODO: tastytrade parser later
        return false;
    }

    var execs = ctx.Executions
        .Where(e => e.Broker == broker && e.Account == account)
        .OrderBy(e => e.ExecutedAt)
        .ToList();

    // Parse all option executions (open + close)
    var opt = execs
        .Select(e =>
        {
            var ok = TryParseOption(broker, e.Symbol, out var und, out var exp, out var strike, out var right);
            return new { Exec = e, Ok = ok, und, exp, strike, right };
        })
        .Where(x => x.Ok)
        .ToList();

    // Opens only to identify BWB structure
    var opens = opt.Where(x => IsOpen(x.Exec.Action)).ToList();

    // Bucket (v0) by day + contract family
    var buckets = opens.GroupBy(x => new
    {
        Date = x.Exec.ExecutedAt.Date,
        x.und,
        x.exp,
        x.right
    });

    int candidates = 0;
    int created = 0;
    int linked = 0;

    foreach (var b in buckets)
    {
        // Build signed open qty per strike
        // Buy to open => +qty, Sell to open => -qty
        var byStrike = b.GroupBy(x => x.strike)
            .Select(g =>
            {
                decimal signedQty = 0m;

                foreach (var row in g)
                {
                    var q = row.Exec.Quantity ?? 0m;
                    if (q == 0m) continue;
                    var abs = Math.Abs(q);

                    if (IsBuy(row.Exec.Action)) signedQty += abs;
                    else if (IsSell(row.Exec.Action)) signedQty -= abs;
                }

                return new { Strike = g.Key, SignedQty = signedQty, Rows = g.ToList() };
            })
            .Where(x => x.SignedQty != 0m)
            .OrderBy(x => x.Strike)
            .ToList();

        if (byStrike.Count < 3)
            continue;

        // Find triples that match +wing / -2wing / +wing
        for (int i = 0; i < byStrike.Count - 2; i++)
            for (int j = i + 1; j < byStrike.Count - 1; j++)
                for (int k = j + 1; k < byStrike.Count; k++)
                {
                    var low = byStrike[i];
                    var mid = byStrike[j];
                    var high = byStrike[k];

                    var wing = Math.Min(Math.Abs(low.SignedQty), Math.Abs(high.SignedQty));
                    if (wing == 0m) continue;

                    if (!(low.SignedQty == +wing && high.SignedQty == +wing && mid.SignedQty == -2m * wing))
                        continue;

                    candidates++;

                    // Dedup check: same day/und/exp/right and same 3 strikes
                    // (We look at TradeGroupLegs, not TradeGroup.Short/LongStrike)
                    bool exists = (from grp in ctx.TradeGroups
                                   where grp.Broker == broker
                                      && grp.Account == account
                                      && grp.StrategyType == "BWB"
                                      && grp.Underlying == b.Key.und
                                      && grp.Expiration == b.Key.exp
                                      && grp.Right == b.Key.right
                                      && grp.OpenDate.Date == b.Key.Date
                                   select grp.Id)
                                  .AsEnumerable()
                                  .Any(tgId =>
                                  {
                                      var strikes = ctx.TradeGroupLegs
                                          .Where(l => l.TradeGroupId == tgId)
                                          .Select(l => l.Strike)
                                          .ToList();

                                      return strikes.Contains(low.Strike) &&
                                             strikes.Contains(mid.Strike) &&
                                             strikes.Contains(high.Strike);
                                  });

                    if (exists) continue;

                    var tg = new TradeGroup
                    {
                        Broker = broker,
                        Account = account,
                        StrategyType = "BWB",
                        Setup = "",
                        Underlying = b.Key.und,
                        Expiration = b.Key.exp,
                        Right = b.Key.right,
                        OpenDate = b.Key.Date,
                        NetPL = 0m
                    };

                    ctx.TradeGroups.Add(tg);
                    ctx.SaveChanges();

                    // Insert legs
                    ctx.TradeGroupLegs.AddRange(
                        new TradeGroupLeg { TradeGroupId = tg.Id, Underlying = tg.Underlying, Expiration = tg.Expiration, Right = tg.Right, Strike = low.Strike, Quantity = low.SignedQty, Role = "Wing" },
                        new TradeGroupLeg { TradeGroupId = tg.Id, Underlying = tg.Underlying, Expiration = tg.Expiration, Right = tg.Right, Strike = mid.Strike, Quantity = mid.SignedQty, Role = "Body" },
                        new TradeGroupLeg { TradeGroupId = tg.Id, Underlying = tg.Underlying, Expiration = tg.Expiration, Right = tg.Right, Strike = high.Strike, Quantity = high.SignedQty, Role = "Wing" }
                    );
                    ctx.SaveChanges();

                    // Link all OPEN executions for those strikes in this bucket
                    var openExecIds = low.Rows
                        .Concat(mid.Rows)
                        .Concat(high.Rows)
                        .Select(x => x.Exec.Id)
                        .Distinct()
                        .ToList();

                    foreach (var id in openExecIds)
                    {
                        if (!ctx.TradeGroupExecutions.Any(l => l.TradeGroupId == tg.Id && l.ExecutionId == id))
                        {
                            ctx.TradeGroupExecutions.Add(new TradeGroupExecution { TradeGroupId = tg.Id, ExecutionId = id });
                            linked++;
                        }
                    }
                    ctx.SaveChanges();

                    // Link CLOSE executions for those strikes (same und/exp/right)
                    var closes = opt
                        .Where(x =>
                            IsClose(x.Exec.Action) &&
                            x.und == tg.Underlying &&
                            x.exp == tg.Expiration &&
                            x.right == tg.Right &&
                            (x.strike == low.Strike || x.strike == mid.Strike || x.strike == high.Strike))
                        .Select(x => x.Exec)
                        .ToList();

                    foreach (var ce in closes)
                    {
                        if (!ctx.TradeGroupExecutions.Any(l => l.TradeGroupId == tg.Id && l.ExecutionId == ce.Id))
                        {
                            ctx.TradeGroupExecutions.Add(new TradeGroupExecution { TradeGroupId = tg.Id, ExecutionId = ce.Id });
                            linked++;
                        }
                    }
                    ctx.SaveChanges();

                    // Compute NetPL
                    var netAmounts = (from l in ctx.TradeGroupExecutions
                                      join e in ctx.Executions on l.ExecutionId equals e.Id
                                      where l.TradeGroupId == tg.Id
                                      select (decimal?)e.NetAmount)
                                     .AsEnumerable();

                    tg.NetPL = netAmounts.Sum() ?? 0m;

                    // TraderSync-style Gross Return
                    var execsForGroup =
                        from l in ctx.TradeGroupExecutions
                        join e in ctx.Executions on l.ExecutionId equals e.Id
                        where l.TradeGroupId == tg.Id
                        select e;

                    var entryCredit = execsForGroup
                        .Where(e => e.Action.Contains("to open", StringComparison.OrdinalIgnoreCase)
                                 && e.Action.StartsWith("sell", StringComparison.OrdinalIgnoreCase))
                        .Sum(e => (e.Price ?? 0m) * Math.Abs(e.Quantity ?? 0m) * 100m);

                    var exitDebit = execsForGroup
                        .Where(e => e.Action.Contains("to close", StringComparison.OrdinalIgnoreCase)
                                 && e.Action.StartsWith("buy", StringComparison.OrdinalIgnoreCase))
                        .Sum(e => (e.Price ?? 0m) * Math.Abs(e.Quantity ?? 0m) * 100m);

                    tg.GrossReturn = entryCredit - exitDebit;


                    // Compute CloseDate = max close exec date (if any)
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

    Console.WriteLine($"BWB candidates found: {candidates}");
    Console.WriteLine($"BWB TradeGroups created: {created}");
    Console.WriteLine($"Execution links created: {linked}");
    PrintDb();
    return;
}




if (string.Equals(command, "list-groups", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var takeStr = GetArg(args, "--take");
    var take = int.TryParse(takeStr, out var t) ? t : 25;
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";
    var strategy = GetArg(args, "--strategy");
    var openOnly = HasArg(args, "--open-only");

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var q = ctx.TradeGroups
        .Where(g => g.Broker == broker && g.Account == account);

    if (!string.IsNullOrWhiteSpace(strategy))
        q = q.Where(g => g.StrategyType == strategy);

    if (openOnly)
        q = q.Where(g => g.CloseDate == null);

    var groups = q
        .OrderByDescending(g => g.OpenDate)
        .Take(take)
        .ToList();

    if (groups.Count == 0)
    {
        Console.WriteLine("No trade groups found.");
        PrintDb();
        return;
    }

    var groupIds = groups.Select(g => g.Id).ToList();

    var links = ctx.TradeGroupExecutions
        .Where(l => groupIds.Contains(l.TradeGroupId))
        .ToList();

    var execIds = links.Select(l => l.ExecutionId).Distinct().ToList();

    var execs = ctx.Executions
        .Where(e => execIds.Contains(e.Id))
        .ToList();

    var execById = execs.ToDictionary(e => e.Id);

    static bool IsOpen(string action) => action.Contains("to open", StringComparison.OrdinalIgnoreCase);
    static bool IsClose(string action) =>
                action.Contains("to close", StringComparison.OrdinalIgnoreCase)
            || action.Contains("expired", StringComparison.OrdinalIgnoreCase);
    static bool IsSell(string action) => action.TrimStart().StartsWith("sell", StringComparison.OrdinalIgnoreCase);
    static bool IsBuy(string action) => action.TrimStart().StartsWith("buy", StringComparison.OrdinalIgnoreCase);

    Console.WriteLine("Id   OpenDate   CloseDate  Outcome Und      Exp        R  Strikes            Qty  W  DTE  Days  EntryPx ExitPx  Credit   Debit    NetPL    GrossRsk  MaxRisk   Ret%  BE      ROC/d");
    Console.WriteLine(new string('-', 170));

    foreach (var g in groups)
    {
        var gLinks = links.Where(l => l.TradeGroupId == g.Id).ToList();
        var gExecs = gLinks.Select(l => execById[l.ExecutionId]).ToList();

        bool isClosed = g.CloseDate != null;

        string daysHeld = "";
        if (g.CloseDate != null)
            daysHeld = (g.CloseDate.Value.Date - g.OpenDate.Date).Days.ToString();

        int dte = (g.Expiration.ToDateTime(TimeOnly.MinValue) - g.OpenDate.Date).Days;

        decimal entryCredit = 0m;
        decimal exitDebit = 0m;

        foreach (var e in gExecs)
        {
            if (IsOpen(e.Action))
            {
                if (IsSell(e.Action)) entryCredit += Math.Abs(e.NetAmount);
                else if (IsBuy(e.Action)) entryCredit -= Math.Abs(e.NetAmount);
            }
            else if (IsClose(e.Action))
            {
                if (IsBuy(e.Action)) exitDebit += Math.Abs(e.NetAmount);
                else if (IsSell(e.Action)) exitDebit -= Math.Abs(e.NetAmount);
            }
        }

        var dispEntryCredit = Math.Max(0m, entryCredit);
        var dispExitDebit = Math.Max(0m, exitDebit);

        if (isClosed && dispEntryCredit == 0m && dispExitDebit > 0m)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ Warning: Group {g.Id} closed with zero entry credit (check import/signs)");
            Console.ResetColor();
        }

        decimal? contracts = null;
        decimal? width = null;
        decimal? grossRisk = null;
        decimal? maxRisk = null;
        decimal? retPct = null;
        decimal? entryPx = null;
        decimal? exitPx = null;

        if (string.Equals(g.StrategyType, "CreditSpread", StringComparison.OrdinalIgnoreCase))
        {
            decimal? qtyShort = null;
            decimal? qtyLong = null;

            foreach (var e in gExecs.Where(x => IsOpen(x.Action)))
            {
                if (!SchwabOptionParser.TryParse(e.Symbol, out var c) || c == null)
                    continue;

                if (c.Strike == g.ShortStrike)
                    qtyShort = Math.Abs(e.Quantity ?? 0m);

                if (c.Strike == g.LongStrike)
                    qtyLong = Math.Abs(e.Quantity ?? 0m);
            }

            if (qtyShort.HasValue && qtyLong.HasValue)
            {
                contracts = Math.Min(qtyShort.Value, qtyLong.Value);
                width = Math.Abs(g.ShortStrike - g.LongStrike);

                grossRisk = width * 100m * contracts.Value;
                maxRisk = grossRisk - dispEntryCredit;

                if (maxRisk.Value != 0m)
                    retPct = (g.NetPL / maxRisk.Value) * 100m;

                if (contracts.Value > 0m)
                {
                    entryPx = dispEntryCredit / (contracts.Value * 100m);
                    if (isClosed)
                        exitPx = dispExitDebit / (contracts.Value * 100m);
                }
            }
        }

        decimal? breakEven = null;
        if (entryPx.HasValue)
        {
            var r = (g.Right ?? "").Trim().ToUpperInvariant();
            if (r.StartsWith("P"))
                breakEven = (decimal)g.ShortStrike - entryPx.Value;
            else if (r.StartsWith("C"))
                breakEven = (decimal)g.ShortStrike + entryPx.Value;
        }

        decimal? rocPerDay = null;
        if (isClosed && retPct.HasValue && int.TryParse(daysHeld, out var dh) && dh > 0)
            rocPerDay = retPct.Value / dh;

        string outcome;
        if (!isClosed) outcome = "OPEN";
        else if (g.NetPL > 0m) outcome = "WIN";
        else if (g.NetPL < 0m)
        {
            if (maxRisk.HasValue)
            {
                var diff = Math.Abs(g.NetPL + maxRisk.Value);
                outcome = diff <= 1.00m ? "MAXL" : "LOSS";
            }
            else outcome = "LOSS";
        }
        else outcome = "FLAT";

        var openStr = g.OpenDate.ToString("yyyy-MM-dd");
        var closeStr = g.CloseDate == null ? "—" : g.CloseDate.Value.ToString("yyyy-MM-dd");
        var expStr = g.Expiration.ToString("yyyy-MM-dd");

        var right = !string.IsNullOrWhiteSpace(g.Right)
            ? char.ToUpperInvariant(g.Right[0]).ToString()
            : "?";

        var strikes = $"{g.ShortStrike} / {g.LongStrike}";

        var qtyStr = contracts.HasValue ? contracts.Value.ToString("0") : "—";
        var widthStr = width.HasValue ? width.Value.ToString("0") : "—";

        var entryPxStr = entryPx.HasValue ? entryPx.Value.ToString("0.00") : "—";
        var exitPxStr = (isClosed && exitPx.HasValue) ? exitPx.Value.ToString("0.00") : "—";

        var debitStr = isClosed ? dispExitDebit.ToString("0.00") : "—";
        var netPlStr = isClosed ? g.NetPL.ToString("0.00") : "—";

        var grossRiskStr = (isClosed && grossRisk.HasValue) ? grossRisk.Value.ToString("0.00") : "—";
        var maxRiskStr = (isClosed && maxRisk.HasValue) ? maxRisk.Value.ToString("0.00") : "—";
        var retStr = (isClosed && retPct.HasValue) ? retPct.Value.ToString("0.0") : "—";

        // IMPORTANT CHANGE: BE only shown when closed
        var beStr = (isClosed && breakEven.HasValue) ? breakEven.Value.ToString("0.00") : "—";
        var rocDayStr = (isClosed && rocPerDay.HasValue) ? rocPerDay.Value.ToString("0.00") : "—";

        Console.WriteLine(
            $"{g.Id,4}  {openStr,-10}  {closeStr,-9}  {outcome,-7} {g.Underlying,-7}  {expStr,-9}  {right,1}  {strikes,-16}  " +
            $"{qtyStr,3}  {widthStr,3}  {dte,3}  {daysHeld,4} {entryPxStr,6}  {exitPxStr,6}  {dispEntryCredit,7:0.00}  {debitStr,7}  {netPlStr,7}  " +
            $"{grossRiskStr,8}  {maxRiskStr,7}  {retStr,5}  {beStr,6}  {rocDayStr,6}"
        );
    }

    Console.WriteLine($"Shown: {groups.Count}");
    PrintDb();
    return;
}

if (string.Equals(command, "list-group-exec", StringComparison.OrdinalIgnoreCase))
{
    var groupStr = GetArg(args, "--group");
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (!long.TryParse(groupStr, out var groupId) || groupId <= 0)
    {
        Console.WriteLine("Error: --group <id> is required and must be a positive integer.");
        Environment.ExitCode = 2;
        return;
    }

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var g = ctx.TradeGroups.FirstOrDefault(x => x.Id == groupId);
    if (g == null)
    {
        Console.WriteLine($"TradeGroup not found: {groupId}");
        PrintDb();
        return;
    }

    var execs = (from l in ctx.TradeGroupExecutions
                 join e in ctx.Executions on l.ExecutionId equals e.Id
                 where l.TradeGroupId == groupId
                 orderby e.ExecutedAt
                 select new
                 {
                     e.ExecutedAt,
                     e.Action,
                     e.Symbol,
                     e.Quantity,
                     e.Price,
                     e.NetAmount,
                     e.Fees
                 }).ToList();

    Console.WriteLine(
        $"TradeGroup {g.Id} | {g.Underlying} {g.Expiration:yyyy-MM-dd} {g.Right} {g.ShortStrike}/{g.LongStrike} | " +
        $"Open={g.OpenDate:yyyy-MM-dd} Close={(g.CloseDate == null ? "—" : g.CloseDate.Value.ToString("yyyy-MM-dd"))} | NetPL={g.NetPL:0.00}"
    );

    Console.WriteLine();
    Console.WriteLine("Time                | Action        | Qty    | Price     | NetAmt     | Fees   | Contract");
    Console.WriteLine("-------------------+--------------+--------+----------+-----------+--------+------------------------------");

    foreach (var e in execs)
    {
        var contract = e.Symbol;
        if (SchwabOptionParser.TryParse(e.Symbol, out var c) && c != null)
            contract = $"{c.Underlying} {c.Expiration:yyyy-MM-dd} {c.Strike} {c.Right}";

        Console.WriteLine(
            $"{e.ExecutedAt:yyyy-MM-dd HH:mm:ss} | {e.Action,-12} | {e.Quantity,6:0.##} | {e.Price,8:0.00} | {e.NetAmount,9:0.00} | {e.Fees,6:0.00} | {contract}"
        );
    }

    Console.WriteLine();
    Console.WriteLine($"Rows: {execs.Count}");
    PrintDb();
    return;
}

if (string.Equals(command, "db-tables", StringComparison.OrdinalIgnoreCase))
{
    using var ctx = DbFactory.Create(rawDb);
    PrintDb();

    var conn = ctx.Database.GetDbConnection();
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
    using var r = cmd.ExecuteReader();

    while (r.Read())
        Console.WriteLine(r.GetString(0));

    return;
}


if (string.Equals(command, "clear-groups", StringComparison.OrdinalIgnoreCase))
{
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var account = GetArg(args, "--account"); // optional

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    if (string.IsNullOrWhiteSpace(account))
    {
        // existing behavior: wipe all groups
        ctx.Database.ExecuteSqlRaw("DELETE FROM TradeGroupExecutions;");
        ctx.Database.ExecuteSqlRaw("DELETE FROM TradeGroupLegs;");
        ctx.Database.ExecuteSqlRaw("DELETE FROM TradeGroups;");
        ctx.Database.ExecuteSqlRaw("DELETE FROM sqlite_sequence WHERE name IN ('TradeGroups','TradeGroupLegs');");

        Console.WriteLine("Cleared ALL TradeGroups + links + legs.");
        return;
    }

    // scoped: only groups for this broker/account
    ctx.Database.ExecuteSqlRaw(@"
DELETE FROM TradeGroupExecutions
WHERE TradeGroupId IN (
  SELECT Id FROM TradeGroups WHERE Broker = {0} AND Account = {1}
);", broker, account);

    ctx.Database.ExecuteSqlRaw(@"
DELETE FROM TradeGroupLegs
WHERE TradeGroupId IN (
  SELECT Id FROM TradeGroups WHERE Broker = {0} AND Account = {1}
);", broker, account);

    ctx.Database.ExecuteSqlRaw("DELETE FROM TradeGroups WHERE Broker = {0} AND Account = {1};", broker, account);

    Console.WriteLine($"Cleared TradeGroups for {broker}/{account}.");
    return;
}


if (string.Equals(command, "schema-exec", StringComparison.OrdinalIgnoreCase))
{
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";
    db = DbFactory.ResolvePath(db);
using var ctx = DbFactory.Create(rawDb); PrintDb();

   
    var conn = ctx.Database.GetDbConnection();
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA table_info(Executions);";
    using var r = cmd.ExecuteReader();

    while (r.Read())
    {
        var name = r["name"]?.ToString();
        var type = r["type"]?.ToString();
        if (name is "NetAmount" or "Fees" or "Price" or "Quantity")
            Console.WriteLine($"{name}: {type}");
    }
    return;
}

if (string.Equals(command, "schema-groups", StringComparison.OrdinalIgnoreCase))
{
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";
    db = DbFactory.ResolvePath(db);

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var conn = ctx.Database.GetDbConnection();
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA table_info(TradeGroups);";
    using var r = cmd.ExecuteReader();

    while (r.Read())
    {
        var name = r["name"]?.ToString();
        var type = r["type"]?.ToString();
        Console.WriteLine($"{name}: {type}");
    }

    return;
}


if (string.Equals(command, "fk-check", StringComparison.OrdinalIgnoreCase))
{
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";
    db = DbFactory.ResolvePath(db);
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var conn = ctx.Database.GetDbConnection();
    conn.Open();

    using var cmd = conn.CreateCommand();
    cmd.CommandText = "PRAGMA foreign_key_check;";
    using var r = cmd.ExecuteReader();
    bool any = false;
    while (r.Read())
    {
        any = true;
        Console.WriteLine($"{r.GetString(0)} | rowid={r.GetInt64(1)} | parent={r.GetString(2)} | fkid={r.GetInt64(3)}");
    }
    if (!any) Console.WriteLine("No FK violations.");
    return;
}

if (string.Equals(command, "fk-list", StringComparison.OrdinalIgnoreCase))
{
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";
    db = DbFactory.ResolvePath(db);
    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var conn = ctx.Database.GetDbConnection();
    conn.Open();

    var tables = new List<string>();
    using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
    }

    foreach (var t in tables)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list('{t}');";
        using var r = cmd.ExecuteReader();

        bool any = false;
        while (r.Read())
        {
            any = true;
            var id = r["id"]?.ToString();
            var seq = r["seq"]?.ToString();
            var table = r["table"]?.ToString();
            var from = r["from"]?.ToString();
            var to = r["to"]?.ToString();
            var onUpdate = r["on_update"]?.ToString();
            var onDelete = r["on_delete"]?.ToString();

            Console.WriteLine($"{t}: fk[{id},{seq}] {from} -> {table}.{to} (ON DELETE {onDelete}, ON UPDATE {onUpdate})");
        }

        if (!any)
            Console.WriteLine($"{t}: (no foreign keys)");
    }

    return;
}

if (string.Equals(command, "stats-dte", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    db = DbFactory.ResolvePath(db);

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var groups = ctx.TradeGroups
        .Where(g => g.Broker == broker && g.Account == account && g.CloseDate != null)
        .Select(g => new { g.OpenDate, g.Expiration, g.NetPL })
        .ToList();

    if (groups.Count == 0)
    {
        Console.WriteLine("No CLOSED trade groups found.");
        return;
    }

    static string Bucket(int dte)
    {
        if (dte <= 1) return "0-1";
        if (dte <= 3) return "2-3";
        if (dte <= 7) return "4-7";
        if (dte <= 14) return "8-14";
        if (dte <= 30) return "15-30";
        return "31+";
    }

    var rows = groups
        .Select(g => new
        {
            Dte = (g.Expiration.ToDateTime(TimeOnly.MinValue) - g.OpenDate.Date).Days,
            g.NetPL
        })
        .Select(x => new { Bucket = Bucket(x.Dte), x.Dte, x.NetPL })
        .ToList();

    var byBucket = rows
        .GroupBy(x => x.Bucket)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
            var n = g.Count();
            var total = g.Sum(x => x.NetPL);
            var avg = total / n;

            var wins = g.Count(x => x.NetPL > 0m);
            var losses = g.Count(x => x.NetPL < 0m);
            var winRate = n == 0 ? 0m : (decimal)wins / n;

            var avgWin = wins == 0 ? 0m : g.Where(x => x.NetPL > 0m).Average(x => x.NetPL);
            var avgLoss = losses == 0 ? 0m : g.Where(x => x.NetPL < 0m).Average(x => x.NetPL);

            var grossWin = g.Where(x => x.NetPL > 0m).Sum(x => x.NetPL);
            var grossLoss = g.Where(x => x.NetPL < 0m).Sum(x => -x.NetPL);
            var profitFactor = grossLoss == 0m ? 0m : grossWin / grossLoss;

            return new
            {
                Bucket = g.Key,
                Trades = n,
                WinRate = winRate,
                ProfitFactor = profitFactor,
                AvgPL = avg,
                AvgWin = avgWin,
                AvgLoss = avgLoss,
                TotalPL = total,
                MinDte = g.Min(x => x.Dte),
                MaxDte = g.Max(x => x.Dte)
            };
        })
        .ToList();

    Console.WriteLine();
    Console.WriteLine("DTE Bucket Stats (closed trades)");
    Console.WriteLine("Bucket | Trades | Win%  | PF    | AvgPL   | AvgWin  | AvgLoss | TotalPL | DTE Range");
    Console.WriteLine("-------+--------+-------+-------+---------+---------+---------+---------+----------");

    foreach (var b in byBucket)
    {
        Console.WriteLine(
            $"{b.Bucket,6} | {b.Trades,6} | {(b.WinRate * 100m),5:0.0}% | {b.ProfitFactor,5:0.00} | " +
            $"{b.AvgPL,7:0.00} | {b.AvgWin,7:0.00} | {b.AvgLoss,7:0.00} | {b.TotalPL,7:0.00} | {b.MinDte}-{b.MaxDte}");
    }

    return;
}

if (string.Equals(command, "stats-daily", StringComparison.OrdinalIgnoreCase))
{
    var account = GetArg(args, "--account") ?? "";
    var broker = GetArg(args, "--broker") ?? "Schwab";
    var db = GetArg(args, "--db") ?? "tradeledger_ef.db";

    if (string.IsNullOrWhiteSpace(account))
    {
        Console.WriteLine("Error: --account is required.");
        Environment.ExitCode = 2;
        return;
    }

    db = DbFactory.ResolvePath(db);

    using var ctx = DbFactory.Create(rawDb); PrintDb();

    var groups = ctx.TradeGroups
        .Where(g => g.Broker == broker && g.Account == account && g.CloseDate != null)
        .Select(g => new { g.CloseDate, g.NetPL })
        .ToList();

    if (groups.Count == 0)
    {
        Console.WriteLine("No CLOSED trade groups found.");
        return;
    }

    var rows = groups
        .Select(g => new { Day = g.CloseDate!.Value.DayOfWeek, g.NetPL })
        .ToList();

    var byDay = rows
        .GroupBy(x => x.Day)
        .OrderBy(g => g.Key)
        .Select(g =>
        {
            var n = g.Count();
            var total = g.Sum(x => x.NetPL);
            var avg = total / n;
            var wins = g.Count(x => x.NetPL > 0m);
            var winRate = n == 0 ? 0m : (decimal)wins / n;

            var grossWin = g.Where(x => x.NetPL > 0m).Sum(x => x.NetPL);
            var grossLoss = g.Where(x => x.NetPL < 0m).Sum(x => -x.NetPL);
            var profitFactor = grossLoss == 0m ? 0m : grossWin / grossLoss;

            return new
            {
                Day = g.Key,
                Trades = n,
                WinRate = winRate,
                ProfitFactor = profitFactor,
                AvgPL = avg,
                TotalPL = total
            };
        })
        .ToList();

    Console.WriteLine();
    Console.WriteLine("Weekday Stats (closed trades)");
    Console.WriteLine("Day       | Trades | Win%  | PF    | AvgPL   | TotalPL");
    Console.WriteLine("----------+--------+-------+-------+---------+---------");

    foreach (var d in byDay)
    {
        Console.WriteLine(
            $"{d.Day,-9} | {d.Trades,6} | {(d.WinRate * 100m),5:0.0}% | {d.ProfitFactor,5:0.00} | {d.AvgPL,7:0.00} | {d.TotalPL,7:0.00}");
    }

    return;
}

Console.WriteLine($"Unknown command: {command}");
Console.WriteLine();
PrintUsage();
Environment.ExitCode = 2;
