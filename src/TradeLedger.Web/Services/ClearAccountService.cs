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

        var groupIds = groups.Select(g => g.Id).ToList();

        // Find all linked execution IDs
        var links = await _db.TradeGroupExecutions
            .Where(l => groupIds.Contains(l.TradeGroupId))
            .ToListAsync();

        var linkedExecIds = links.Select(l => l.ExecutionId).Distinct().ToList();

        // Find linked executions
        var linkedExecs = await _db.Executions
            .Where(e => linkedExecIds.Contains(e.Id))
            .ToListAsync();

        // Find ALL orphaned executions for this account (any broker field value)
        var orphanedExecs = await _db.Executions
            .Where(e => e.Account == account)
            .Where(e => !_db.TradeGroupExecutions.Any(l => l.ExecutionId == e.Id))
            .ToListAsync();

        // Delete everything
        _db.TradeGroupExecutions.RemoveRange(links);
        await _db.SaveChangesAsync();

        _db.TradeGroups.RemoveRange(groups);
        _db.Executions.RemoveRange(linkedExecs);
        _db.Executions.RemoveRange(orphanedExecs);
        await _db.SaveChangesAsync();

        return new ClearResult(groups.Count, linkedExecs.Count + orphanedExecs.Count);
    }
}
