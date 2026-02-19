namespace TradeLedger.Web.Models
{
    public sealed class TradeGroupExecTotalsDto
    {
        public long TradeGroupId { get; init; }

        public decimal ExecGross { get; init; }        // Σ NetAmount
        public decimal ExecFees { get; init; }         // Σ Fees
        public decimal ExecNetAfterFees { get; init; } // Σ(NetAmount - Fees)
    }
}
