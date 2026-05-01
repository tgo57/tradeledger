using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TradeLedger.Web.Services;

/// <summary>
/// Handles Schwab OAuth2 authorization_code flow.
/// - Builds the authorization URL for the user to visit
/// - Exchanges the auth code for access + refresh tokens
/// - Refreshes the access token automatically
/// </summary>
public sealed class SchwabOAuthService
{
    private readonly SchwabTokenStore _store;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;

    // Schwab OAuth endpoints
    private const string AuthorizeUrl = "https://api.schwabapi.com/v1/oauth/authorize";
    private const string TokenUrl = "https://api.schwabapi.com/v1/oauth/token";

    public SchwabOAuthService(
        SchwabTokenStore store,
        IConfiguration config,
        IHttpClientFactory httpFactory)
    {
        _store = store;
        _config = config;
        _httpFactory = httpFactory;
    }

    public string ClientId => _config["Schwab:ClientId"] ?? "";
    public string ClientSecret => _config["Schwab:ClientSecret"] ?? "";
    public string CallbackUrl => _config["Schwab:CallbackUrl"] ?? "";

    public bool IsConnected => _store.HasValidRefreshToken;
    public bool NeedsReconnect => !IsConnected;
    public bool AccessTokenValid => _store.Tokens != null && !_store.NeedsTokenRefresh;

    public DateTime? RefreshTokenExpiry => _store.Tokens?.RefreshTokenExpiry;
    public int DaysUntilExpiry =>
        _store.Tokens == null ? 0
        : Math.Max(0, (int)(_store.Tokens.RefreshTokenExpiry - DateTime.UtcNow).TotalDays);

    /// <summary>Build the URL the user clicks to authorize TradeLedger with Schwab.</summary>
    public string BuildAuthorizationUrl()
    {
        var state = Guid.NewGuid().ToString("N")[..8];
        return $"{AuthorizeUrl}?response_type=code" +
               $"&client_id={Uri.EscapeDataString(ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
               $"&state={state}";
    }

    /// <summary>Exchange the authorization code (from callback) for tokens.</summary>
    public async Task<bool> ExchangeCodeForTokensAsync(string code)
    {
        var client = _httpFactory.CreateClient();

        // Basic auth header: base64(clientId:clientSecret)
        var creds = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type",   "authorization_code"),
            new KeyValuePair<string,string>("code",         code),
            new KeyValuePair<string,string>("redirect_uri", CallbackUrl),
        });

        var resp = await client.PostAsync(TokenUrl, body);
        if (!resp.IsSuccessStatusCode) return false;

        var json = await resp.Content.ReadAsStringAsync();
        return ParseAndSaveTokens(json);
    }

    /// <summary>Use the refresh token to get a new access token.</summary>
    public async Task<bool> RefreshAccessTokenAsync()
    {
        if (_store.Tokens == null) return false;

        var client = _httpFactory.CreateClient();
        var creds = Convert.ToBase64String(
            Encoding.ASCII.GetBytes($"{ClientId}:{ClientSecret}"));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("grant_type",    "refresh_token"),
            new KeyValuePair<string,string>("refresh_token", _store.Tokens.RefreshToken),
        });

        var resp = await client.PostAsync(TokenUrl, body);
        if (!resp.IsSuccessStatusCode) return false;

        var json = await resp.Content.ReadAsStringAsync();
        return ParseAndSaveTokens(json);
    }

    /// <summary>Get a valid access token, refreshing if needed.</summary>
    public async Task<string?> GetValidAccessTokenAsync()
    {
        if (_store.Tokens == null) return null;

        if (_store.NeedsTokenRefresh)
        {
            var ok = await RefreshAccessTokenAsync();
            if (!ok) return null;
        }

        return _store.Tokens?.AccessToken;
    }

    public void Disconnect() => _store.Clear();

    private bool ParseAndSaveTokens(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var accessToken = root.GetProperty("access_token").GetString() ?? "";
            var refreshToken = root.TryGetProperty("refresh_token", out var rt)
                ? rt.GetString() ?? _store.Tokens?.RefreshToken ?? ""
                : _store.Tokens?.RefreshToken ?? "";
            var expiresIn = root.GetProperty("expires_in").GetInt32(); // seconds

            _store.Save(new SchwabTokenData
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn),
                // Schwab refresh tokens expire after 7 days
                RefreshTokenExpiry = DateTime.UtcNow.AddDays(7),
                Scope = root.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : ""
            });
            return true;
        }
        catch { return false; }
    }
}
