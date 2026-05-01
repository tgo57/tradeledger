using System.Net.Http.Headers;
using System.Text.Json;
using TradeLedger.Core.Models;
using TradeLedger.Data;
using TradeLedger.Importers.Schwab;

namespace TradeLedger.Web.Services;

/// <summary>
/// Fetches transaction history from the Schwab Trader API and imports into TradeLedger.
/// Uses SchwabOAuthService to get a valid access token.
/// </summary>
public sealed class SchwabSyncService
{
    private readonly SchwabOAuthService _oauth;
    private readonly SchwabCsvImportService _importer;
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;

    private const string BaseUrl = "https://api.schwabapi.com/trader/v1";

    public SchwabSyncService(
        SchwabOAuthService oauth,
        SchwabCsvImportService importer,
        AppDbContext db,
        IHttpClientFactory httpFactory)
    {
        _oauth = oauth;
        _importer = importer;
        _db = db;
        _httpFactory = httpFactory;
    }

    public sealed record SyncResult(int Inserted, int Skipped, List<string> Errors);

    /// <summary>
    /// Fetch account numbers linked to this OAuth session.
    /// Returns list of (accountNumber, displayName).
    /// </summary>
    public async Task<List<(string Number, string Name)>> GetAccountsAsync()
    {
        var token = await _oauth.GetValidAccessTokenAsync();
        if (token == null) return new();

        var client = MakeClient(token);
        var resp = await client.GetAsync($"{BaseUrl}/accounts/accountNumbers");
        if (!resp.IsSuccessStatusCode) return new();

        var json = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);

        var accounts = new List<(string, string)>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var num = item.GetProperty("accountNumber").GetString() ?? "";
            var hash = item.GetProperty("hashValue").GetString() ?? "";
            accounts.Add((num, hash));
        }
        return accounts;
    }

    /// <summary>
    /// Sync transactions for the given account from Schwab API.
    /// Fetches the last 60 days of transactions (Schwab API limit).
    /// </summary>
    public async Task<SyncResult> SyncAsync(string accountNumber, string accountLabel)
    {
        var errors = new List<string>();
        var inserted = 0;
        var skipped = 0;

        var token = await _oauth.GetValidAccessTokenAsync();
        if (token == null)
        {
            errors.Add("No valid access token. Please reconnect Schwab.");
            return new SyncResult(0, 0, errors);
        }

        var client = MakeClient(token);

        // Get account hash (required for API calls)
        var accounts = await GetAccountsAsync();
        var account = accounts.FirstOrDefault(a => a.Number == accountNumber);
        if (account.Number == null)
        {
            // Use first account if specific one not found
            account = accounts.FirstOrDefault();
        }
        if (account.Number == null)
        {
            errors.Add("No Schwab accounts found.");
            return new SyncResult(0, 0, errors);
        }

        // Fetch transactions — Schwab allows up to 60 days
        var startDate = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
        var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var url = $"{BaseUrl}/accounts/{account.Name}/transactions" +
                   $"?startDate={startDate}&endDate={endDate}&types=TRADE";
        var resp = await client.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            errors.Add($"Schwab API error {resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            return new SyncResult(0, 0, errors);
        }

        var json = await resp.Content.ReadAsStringAsync();
        var transactions = ParseTransactions(json, accountLabel, errors);

        if (!transactions.Any())
        {
            errors.Add("No option transactions found in the last 60 days.");
            return new SyncResult(0, 0, errors);
        }

        // Use the same grouping logic as CSV import
        var optionExecs = transactions
            .Where(e => SchwabOptionParser.TryParse(e.Symbol, out _))
            .ToList();

        // Save via temp CSV approach — reuse existing import pipeline
        // (Convert API response to CSV format and import)
        var csv = BuildCsvFromExecutions(transactions);
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var result = await _importer.ImportAsync(stream, "Schwab", accountLabel, "api-sync");

        return new SyncResult(result.Inserted, result.Skipped, result.Errors);
    }

    private List<Execution> ParseTransactions(string json, string account, List<string> errors)
    {
        var result = new List<Execution>();
        try
        {
            var doc = JsonDocument.Parse(json);
            foreach (var tx in doc.RootElement.EnumerateArray())
            {
                try
                {
                    var txType = tx.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (txType != "TRADE") continue;

                    var txDate = tx.GetProperty("tradeDate").GetString() ?? "";
                    if (!DateTime.TryParse(txDate, out var executedAt)) continue;

                    if (!tx.TryGetProperty("transferItems", out var items)) continue;

                    foreach (var item in items.EnumerateArray())
                    {
                        var inst = item.GetProperty("instrument");
                        var sym = inst.TryGetProperty("symbol", out var s) ? s.GetString() : "";

                        if (string.IsNullOrWhiteSpace(sym)) continue;

                        var amount = item.TryGetProperty("amount", out var a) ? a.GetDecimal() : 0m;
                        var cost = item.TryGetProperty("cost", out var c) ? c.GetDecimal() : 0m;
                        var price = item.TryGetProperty("price", out var p) ? p.GetDecimal() : 0m;
                        var qty = item.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m;

                        var instruction = item.TryGetProperty("instruction", out var i)
                            ? i.GetString() ?? "" : "";

                        // Map Schwab API instruction to action string
                        var action = instruction switch
                        {
                            "BUY" => "Buy to Open",
                            "SELL" => "Sell to Open",
                            "BUY_TO_CLOSE" => "Buy to Close",
                            "SELL_TO_CLOSE" => "Sell to Close",
                            _ => instruction
                        };

                        var exec = new Execution
                        {
                            Broker = "Schwab",
                            Account = account,
                            ExecutedAt = executedAt,
                            Symbol = sym ?? "",
                            Action = action,
                            Quantity = qty,
                            Price = price,
                            NetAmount = cost,
                            Fees = Math.Abs(amount - cost),
                            Fingerprint = "",
                            SourceFile = "api-sync"
                        };
                        result.Add(exec);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Parse error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"JSON parse error: {ex.Message}");
        }
        return result;
    }

    /// <summary>Build a Schwab-format CSV from API executions so we can reuse the CSV importer.</summary>
    private static string BuildCsvFromExecutions(List<Execution> executions)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\"Date\",\"Action\",\"Symbol\",\"Description\",\"Quantity\",\"Price\",\"Fees & Comm\",\"Amount\"");

        foreach (var e in executions)
        {
            var date = e.ExecutedAt.ToString("MM/dd/yyyy");
            var price = e.Price?.ToString("F2") ?? "0.00";
            var fees = e.Fees.ToString("F2");
            var amt = e.NetAmount.ToString("F2");
            sb.AppendLine($"\"{date}\",\"{e.Action}\",\"{e.Symbol}\",\"{e.Description}\",\"{e.Quantity}\",\"${price}\",\"${fees}\",\"${amt}\"");
        }
        return sb.ToString();
    }

    private HttpClient MakeClient(string accessToken)
    {
        var client = _httpFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
