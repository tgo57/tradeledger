using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeLedger.Core.Models;

namespace TradeLedger.Web.Services;

/// <summary>
/// Authenticates with the TastyTrade API via OAuth2 and fetches option transactions.
/// Docs: https://developer.tastytrade.com
/// </summary>
public sealed class TastyTradeApiService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ILogger<TastyTradeApiService> _logger;

    private const string BaseUrl = "https://api.tastytrade.com";

    public TastyTradeApiService(HttpClient http, IConfiguration config, ILogger<TastyTradeApiService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    public async Task<string> GetSessionTokenAsync()
    {
        var clientSecret = _config["TastyTrade:ClientSecret"] ?? throw new InvalidOperationException("TastyTrade:ClientSecret not configured");
        var refreshToken = _config["TastyTrade:RefreshToken"] ?? throw new InvalidOperationException("TastyTrade:RefreshToken not configured");

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradeLedger/1.0");

        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_secret"] = clientSecret,
        });

        var response = await _http.PostAsync($"{BaseUrl}/oauth/token", body);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("TastyTrade token refresh failed: {Status} {Body}", response.StatusCode, json);
            throw new InvalidOperationException($"TastyTrade auth failed: {response.StatusCode} - {json}");
        }

        var token = JsonSerializer.Deserialize<OAuthTokenResponse>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return token?.AccessToken ?? throw new InvalidOperationException("No access_token in TastyTrade response");
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    public async Task<List<Execution>> GetOptionExecutionsAsync(
        string accountNumber,
        string sessionToken,
        DateTime? since = null)
    {
        var fromDate = since ?? DateTime.UtcNow.AddMonths(-6);
        var fromStr = fromDate.ToString("yyyy-MM-dd");

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", sessionToken);

        var allItems = new List<TastyTransaction>();
        var pageOffset = 0;
        const int pageSize = 250;

        while (true)
        {
            var url = $"{BaseUrl}/accounts/{accountNumber}/transactions" +
                      $"?start-date={fromStr}" +
                      $"&instrument-type=Equity%20Option" +
                      $"&per-page={pageSize}" +
                      $"&page-offset={pageOffset}";

            var response = await _http.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("TastyTrade transactions failed: {Status} {Body}", response.StatusCode, json);
                throw new InvalidOperationException($"TastyTrade transactions failed: {response.StatusCode}");
            }

            var page = JsonSerializer.Deserialize<TastyTransactionPage>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var items = page?.Data?.Items ?? [];
            allItems.AddRange(items);

            _logger.LogInformation("TastyTrade: fetched {Count} transactions (offset {Offset})",
                items.Count, pageOffset);

            if (items.Count < pageSize) break;
            pageOffset += pageSize;
        }

        return allItems
            .Where(t =>
                (t.TransactionType == "Trade" &&
                    (t.TransactionSubType == "Buy to Open" ||
                     t.TransactionSubType == "Sell to Open" ||
                     t.TransactionSubType == "Buy to Close" ||
                     t.TransactionSubType == "Sell to Close"))
                ||
                (t.TransactionType == "Receive Deliver" &&
                    (t.TransactionSubType == "Cash Settled Exercise" ||
                     t.TransactionSubType == "Cash Settled Assignment" ||
                     t.TransactionSubType == "Expiration")))
            .Select(t => MapToExecution(t, accountNumber))
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();
    }

    // ── Mapping ───────────────────────────────────────────────────────────────

    private static Execution? MapToExecution(TastyTransaction t, string account)
    {
        if (string.IsNullOrWhiteSpace(t.Symbol)) return null;

        var action = t.TransactionType switch
        {
            "Receive Deliver" => t.TransactionSubType switch
            {
                "Cash Settled Exercise" => "SELL_TO_CLOSE",
                "Cash Settled Assignment" => "BUY_TO_CLOSE",
                "Expiration" => "EXPIRATION",
                _ => t.TransactionSubType?.ToUpper() ?? "UNKNOWN"
            },
            _ => t.TransactionSubType?
                    .Replace(" ", "_")
                    .ToUpper()
                 ?? "UNKNOWN"
        };

        var netRaw = decimal.TryParse(t.NetValue, out var nv) ? nv : 0m;
        var price = decimal.TryParse(t.Price, out var p) ? p : 0m;
        var quantity = decimal.TryParse(t.Quantity, out var q) ? q : 0m;
        var netAmount = t.NetValueEffect == "Debit" ? -netRaw : netRaw;

        return new Execution
        {
            Account = account,
            Symbol = t.Symbol,
            Action = action,
            Quantity = quantity,
            Price = price,
            NetAmount = netAmount,
            ExecutedAt = t.ExecutedAt ?? DateTime.UtcNow,
            Broker = "TastyTrade",
            Fingerprint = MakeFingerprint(account, t),
        };
    }

    private static string MakeFingerprint(string account, TastyTransaction t)
    {
        var s = $"TastyTrade|{account}|{t.ExecutedAt:O}|{t.TransactionSubType}|{t.Symbol}|{t.Quantity}|{t.Price}|{t.NetValue}";
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }
    }

    private sealed class TastyTransactionPage
    {
        public TastyTransactionData? Data { get; set; }
    }

    private sealed class TastyTransactionData
    {
        public List<TastyTransaction> Items { get; set; } = [];
    }

    private sealed class TastyTransaction
    {
        [JsonPropertyName("id")]
        public int? Id { get; set; }

        [JsonPropertyName("transaction-type")]
        public string? TransactionType { get; set; }

        [JsonPropertyName("transaction-sub-type")]
        public string? TransactionSubType { get; set; }

        [JsonPropertyName("symbol")]
        public string? Symbol { get; set; }

        [JsonPropertyName("instrument-type")]
        public string? InstrumentType { get; set; }

        [JsonPropertyName("quantity")]
        public string? Quantity { get; set; }

        [JsonPropertyName("price")]
        public string? Price { get; set; }

        [JsonPropertyName("net-value")]
        public string? NetValue { get; set; }

        [JsonPropertyName("net-value-effect")]
        public string? NetValueEffect { get; set; }

        [JsonPropertyName("executed-at")]
        public DateTime? ExecutedAt { get; set; }

        [JsonPropertyName("transaction-date")]
        public string? TransactionDate { get; set; }
    }
}
