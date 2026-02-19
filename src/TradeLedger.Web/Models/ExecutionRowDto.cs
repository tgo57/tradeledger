namespace TradeLedger.Web.Models
{
    public sealed class ExecutionRowDto
    {
        public long Id { get; init; }
        public DateTime ExecutedAt { get; init; }

        public string Symbol { get; init; } = "";
        public string Description { get; init; } = "";

        public decimal Quantity { get; init; }
        public decimal Price { get; init; }

        public decimal Fees { get; init; }
        public decimal NetAmount { get; init; }   // signed, from CSV/importer

        public decimal? MaxRisk { get; init; }         // dollars
        public decimal? NetReturnPct { get; init; }    // percent (0-100)

    }

}
