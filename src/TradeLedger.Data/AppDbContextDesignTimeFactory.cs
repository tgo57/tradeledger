using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradeLedger.Data;

public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var db = "tradeledger_ef.db";

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                db = args[i + 1];
                break;
            }
        }

        var finalPath = DbFactory.ResolvePath(db);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={finalPath}")
            .Options;

        return new AppDbContext(options);
    }
}
