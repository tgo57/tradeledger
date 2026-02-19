namespace TradeLedger.Core.Models;

public sealed class Execution
{
    public long Id { get; set; }

    public string Fingerprint { get; set; } = "";


    public string Broker { get; set; } = "Schwab";
    public string Account { get; set; } = "";

    public DateTime ExecutedAt { get; set; }
    public string Symbol { get; set; } = "";
    public string Description { get; set; } = "";

    public string Action { get; set; } = "";   // BUY / SELL / TO OPEN, etc.

    public decimal? Quantity { get; set; }
    public decimal? Price { get; set; }

    public decimal Fees { get; set; }
    public decimal NetAmount { get; set; }

    public string Currency { get; set; } = "USD";

    public string SourceFile { get; set; } = "";
    public int SourceRowNumber { get; set; }
    public string RawRowJson { get; set; } = "{}";

}
