using System.IO;
using Microsoft.EntityFrameworkCore;

namespace TradeLedger.Data;

public static class DbFactory
{
    public static AppDbContext Create(string sqlitePath)
    {
        var finalPath = ResolvePath(sqlitePath);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={finalPath}")
            .Options;

        return new AppDbContext(options);
    }

    // NEW: call this from CLI so "DB:" prints the real target
    public static string ResolvePath(string sqlitePath) => ResolveDbPath(sqlitePath);

    private static string ResolveDbPath(string sqlitePath)
    {
        if (string.IsNullOrWhiteSpace(sqlitePath))
            sqlitePath = "tradeledger_ef.db";

        if (Path.IsPathRooted(sqlitePath))
            return sqlitePath;

        // AppContext.BaseDirectory: ...\src\TradeLedger.Cli\bin\Debug\net8.0\
        // Go up to: ...\src\
        var srcRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\.."));

        // Then go to parent: ...\TradeLedger\
        var repoRoot = Directory.GetParent(srcRoot)!.FullName;

        var dbFolder = Path.Combine(repoRoot, "db");
        Directory.CreateDirectory(dbFolder);

        return Path.Combine(dbFolder, sqlitePath);
    }

}
