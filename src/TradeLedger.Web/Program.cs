using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;
using TradeLedger.Data;
using TradeLedger.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddMudServices();

var dbPath = DbFactory.ResolvePath("tradeledger_ef.db");
var cs = $"Data Source={dbPath}";

builder.Services.AddDbContextFactory<AppDbContext>(options =>
{
    options.UseSqlite(cs);
});

builder.Services.AddScoped<TradesReadService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<SchwabCsvImportService>();
builder.Services.AddScoped<TastyTradeImporter>();
builder.Services.AddSingleton<ImportHistoryService>();
builder.Services.AddSingleton<AppStateService>();
builder.Services.AddScoped<ClearAccountService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<SchwabTokenStore>();
builder.Services.AddSingleton<SchwabOAuthService>(sp => new SchwabOAuthService(
    sp.GetRequiredService<SchwabTokenStore>(),
    sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<IHttpClientFactory>()
));
builder.Services.AddScoped<SchwabSyncService>();


var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.MapGet("/schwab/callback", async (HttpContext context, SchwabOAuthService oauth) =>
{
    var code = context.Request.Query["code"].ToString();
    var error = context.Request.Query["error"].ToString();
    if (!string.IsNullOrWhiteSpace(error))
        return Results.Redirect($"/import?schwab_error={Uri.EscapeDataString(error)}");
    if (string.IsNullOrWhiteSpace(code))
        return Results.Redirect("/import?schwab_error=no_code");
    var success = await oauth.ExchangeCodeForTokensAsync(code);
    return success
        ? Results.Redirect("/import?schwab_connected=true")
        : Results.Redirect("/import?schwab_error=token_exchange_failed");
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
