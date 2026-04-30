using Microsoft.EntityFrameworkCore;
using TradeLedger.Data;

namespace TradeLedger.Web.Services;

/// <summary>
/// Clears all trade data for a specific broker + account combination.
/// Used to wipe and re-import when fixing importer bugs.
/// </summary>
public sealed class ClearAccountService
{
    private readonly AppDbContext _db;
    public ClearAccountService(AppDbContext db) => _db = db;

    public sealed record ClearResult(int GroupsDeleted, int ExecutionsDeleted);

    public async Task<ClearResult> ClearAsync(string broker, string account)
    {
        // Find all trade groups for this account
        var groups = await _db.TradeGroups
            .Where(g => g.Broker == broker && g.Account == account)
            .ToListAsync();

        if (!groups.Any())
            return new ClearResult(0, 0);

        var groupIds = groups.Select(g => g.Id).ToList();

        // Find all execution links for these groups
        var links = await _db.TradeGroupExecutions
            .Where(l => groupIds.Contains(l.TradeGroupId))
            .ToListAsync();

        var execIds = links.Select(l => l.ExecutionId).Distinct().ToList();

        // Find executions
        var executions = await _db.Executions
            .Where(e => execIds.Contains(e.Id))
            .ToListAsync();

        // Delete in correct order (links first, then groups and executions)
        _db.TradeGroupExecutions.RemoveRange(links);
        await _db.SaveChangesAsync();

        _db.TradeGroups.RemoveRange(groups);
        _db.Executions.RemoveRange(executions);
        await _db.SaveChangesAsync();

        return new ClearResult(groups.Count, executions.Count);
    }
}
