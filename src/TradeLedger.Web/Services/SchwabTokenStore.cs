namespace TradeLedger.Web.Services;

/// <summary>
/// Stores Schwab OAuth tokens in memory and persists to a local JSON file.
/// Tokens survive app restarts. Refresh token expires after 7 days.
/// </summary>
public sealed class SchwabTokenStore
{
    private readonly string _tokenPath;
    private SchwabTokenData? _tokens;

    public SchwabTokenStore()
    {
        // Store tokens in AppData so they survive deployments
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TradeLedger");
        Directory.CreateDirectory(dir);
        _tokenPath = Path.Combine(dir, "schwab_tokens.json");
        Load();
    }

    public SchwabTokenData? Tokens => _tokens;

    public bool HasValidRefreshToken =>
        _tokens != null &&
        !string.IsNullOrWhiteSpace(_tokens.RefreshToken) &&
        _tokens.RefreshTokenExpiry > DateTime.UtcNow;

    public bool NeedsTokenRefresh =>
        _tokens != null &&
        _tokens.AccessTokenExpiry <= DateTime.UtcNow.AddMinutes(2);

    public void Save(SchwabTokenData tokens)
    {
        _tokens = tokens;
        var json = System.Text.Json.JsonSerializer.Serialize(tokens,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_tokenPath, json);
    }

    public void Clear()
    {
        _tokens = null;
        if (File.Exists(_tokenPath))
            File.Delete(_tokenPath);
    }

    private void Load()
    {
        if (!File.Exists(_tokenPath)) return;
        try
        {
            var json = File.ReadAllText(_tokenPath);
            _tokens = System.Text.Json.JsonSerializer.Deserialize<SchwabTokenData>(json);
        }
        catch { _tokens = null; }
    }
}

public sealed class SchwabTokenData
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime AccessTokenExpiry { get; set; }
    public DateTime RefreshTokenExpiry { get; set; }
    public string Scope { get; set; } = "";
}
