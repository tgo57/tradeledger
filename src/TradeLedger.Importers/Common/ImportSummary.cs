namespace TradeLedger.Importers;

public sealed class ImportSummary
{
    public int RowsRead { get; set; }
    public int ExecutionsCreated { get; set; }
    public List<string> Warnings { get; } = new();
}
