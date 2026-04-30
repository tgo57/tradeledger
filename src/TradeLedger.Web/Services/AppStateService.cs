namespace TradeLedger.Web.Services;

/// <summary>
/// Singleton service that holds global app state (selected account, range, search).
/// Injected into both MainLayout and Dashboard so the top bar drives the dashboard.
/// </summary>
public sealed class AppStateService
{
    private string _account = "Schwab1";
    private string _range = "All";
    private string _search = "";

    public string Account
    {
        get => _account;
        set { _account = value; NotifyStateChanged(); }
    }

    public string Range
    {
        get => _range;
        set { _range = value; NotifyStateChanged(); }
    }

    public string Search
    {
        get => _search;
        set { _search = value; NotifyStateChanged(); }
    }

    /// <summary>Derive broker from account name.</summary>
    public string Broker => Account.StartsWith("Tasty", StringComparison.OrdinalIgnoreCase)
        ? "TastyTrade"
        : "Schwab";

    /// <summary>Fires whenever Account, Range, or Search changes.</summary>
    public event Action? OnChange;
    private void NotifyStateChanged() => OnChange?.Invoke();
}
