namespace TradeLedger.Core.Models;

public sealed class TradeGroupExecution
{
    public long TradeGroupId { get; set; }
    public TradeGroup TradeGroup { get; set; } = null!;

    public long ExecutionId { get; set; }
    public Execution Execution { get; set; } = null!;
}
