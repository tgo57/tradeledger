namespace TradeLedger.Core.Models;

public sealed class TradeGroupLeg
{
    public long Id { get; set; }

    public long TradeGroupId { get; set; }
    public TradeGroup? TradeGroup { get; set; }

    public string Underlying { get; set; } = "";
    public DateOnly Expiration { get; set; }
    public string Right { get; set; } = "";

    public decimal Strike { get; set; }

    /// <summary>
    /// Signed quantity convention:
    ///  + = long contracts, - = short contracts
    /// Example: STO 1 => -1, BTO 1 => +1
    /// BWB typical (puts): +1 / -2 / +1 across strikes.
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Optional role label for readability: "Short", "Long", "Body", "Wing"
    /// </summary>
    public string Role { get; set; } = "";
}
