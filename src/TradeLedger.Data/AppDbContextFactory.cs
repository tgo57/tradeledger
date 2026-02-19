using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TradeLedger.Data;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // Allow: --db "path\to.db"
        string dbPath = "tradeledger.db";
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--db", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dbPath = args[i + 1];
            }
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        return new AppDbContext(options);
    }
}
