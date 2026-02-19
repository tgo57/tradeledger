using Microsoft.EntityFrameworkCore;
using TradeLedger.Data;
using TradeLedger.Web.Models;

namespace TradeLedger.Web.Services
{
    public sealed class TradesReadService
    {
        private readonly IDbContextFactory<AppDbContext> _dbFactory;

        public TradesReadService(IDbContextFactory<AppDbContext> dbFactory)
            => _dbFactory = dbFactory;

        public async Task<List<ExecutionRowDto>> GetExecutionsForGroupAsync(long groupId, CancellationToken ct = default)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Step 1: pull raw values from EF (prevents EF from trying to cast NULL -> non-nullable during projection)
            var raw = await (
                from link in db.TradeGroupExecutions
                join e in db.Executions on link.ExecutionId equals e.Id
                where link.TradeGroupId == groupId
                orderby e.ExecutedAt
                select new
                {
                    e.Id,
                    e.ExecutedAt,
                    e.Symbol,
                    e.Description,
                    e.Quantity,
                    e.Price,
                    e.Fees,
                    e.NetAmount
                }
            ).ToListAsync(ct);

            // Step 2: map safely in memory
            var rows = raw.Select(e => new ExecutionRowDto
            {
                Id = e.Id,
                ExecutedAt = e.ExecutedAt,
                Symbol = e.Symbol ?? "",
                Description = e.Description ?? "",

                Quantity = ToDecimalSafe(e.Quantity),
                Price = ToDecimalSafe(e.Price),
                Fees = ToDecimalSafe(e.Fees),
                NetAmount = ToDecimalSafe(e.NetAmount),
            }).ToList();

            return rows;
        }

        public async Task<Dictionary<long, TradeGroupExecTotalsDto>> GetExecTotalsForGroupsAsync(
            IEnumerable<long> groupIds,
            CancellationToken ct = default)
        {
            var ids = groupIds.Distinct().ToList();
            if (ids.Count == 0) return new();

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Use nullable sums to prevent EF from throwing when values are NULL in DB.
            // Sum as decimal? first, then coalesce to 0.
            var rows = await (
                from link in db.TradeGroupExecutions
                join e in db.Executions on link.ExecutionId equals e.Id
                where ids.Contains(link.TradeGroupId)
                group e by link.TradeGroupId into g
                select new
                {
                    TradeGroupId = g.Key,
                    ExecGross = g.Sum(x => (decimal?)x.NetAmount) ?? 0m,
                    ExecFees = g.Sum(x => (decimal?)x.Fees) ?? 0m
                }
            ).ToListAsync(ct);

            return rows.ToDictionary(
                x => x.TradeGroupId,
                x => new TradeGroupExecTotalsDto
                {
                    TradeGroupId = x.TradeGroupId,
                    ExecGross = x.ExecGross,
                    ExecFees = x.ExecFees,
                    ExecNetAfterFees = x.ExecGross - x.ExecFees
                });
        }

        // Handles decimal, decimal?, double, double?, int, long, etc. and returns 0m for nulls.
        private static decimal ToDecimalSafe(object? value)
        {
            if (value is null) return 0m;

            // If EF gives us boxed nullable with HasValue=false, it will still come through as null.
            // So here we only handle non-null values.
            return Convert.ToDecimal(value);
        }
    }
}
