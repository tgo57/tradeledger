namespace TradeLedger.Web.Services;

/// <summary>
/// Singleton service that persists import history across Blazor component navigation.
/// Registered as AddSingleton in Program.cs so the list survives page changes.
/// </summary>
public sealed class ImportHistoryService
{
    public sealed record HistoryEntry(
        DateTime Time,
        string Broker,
        string Account,
        string FileName,
        int Inserted,
        int Skipped,
        List<string> Errors);

    private readonly List<HistoryEntry> _history = new();

    public IReadOnlyList<HistoryEntry> History => _history;

    public void Add(HistoryEntry entry) => _history.Insert(0, entry);

    public void Clear() => _history.Clear();
}
