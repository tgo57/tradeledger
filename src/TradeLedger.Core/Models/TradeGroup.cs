namespace TradeLedger.Core.Models;

public sealed class TradeGroup
{
    public long Id { get; set; }

    public string Broker { get; set; } = "";
    public string Account { get; set; } = "";

    public string StrategyType { get; set; } = ""; // "CreditSpread" / "BWB"
    public string Setup { get; set; } = "";        // NEW

    public string Underlying { get; set; } = "";
    public DateOnly Expiration { get; set; }
    public string Right { get; set; } = ""; // "Put" / "Call" or "P"/"C"

    // Keep these for CreditSpread compatibility (optional long-term)
    public decimal ShortStrike { get; set; }
    public decimal LongStrike { get; set; }

    public DateTime OpenDate { get; set; }
    public DateTime? CloseDate { get; set; }

    public decimal NetPL { get; set; }

    public decimal GrossReturn { get; set; }

}
