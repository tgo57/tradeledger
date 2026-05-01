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

app.MapGet("/schwab/callback", (HttpContext context) =>
{
    var code = context.Request.Query["code"].ToString();
    var error = context.Request.Query["error"].ToString();

    if (!string.IsNullOrWhiteSpace(error))
        return Results.Content(
            $"<html><body style='font-family:sans-serif;padding:40px'>" +
            $"<h2>❌ Error: {error}</h2></body></html>", "text/html");

    if (string.IsNullOrWhiteSpace(code))
        return Results.Content(
            "<html><body style='font-family:sans-serif;padding:40px'>" +
            "<h2>❌ No code received</h2></body></html>", "text/html");

    return Results.Content($"""
        <html><body style='font-family:sans-serif;padding:40px;max-width:700px;margin:auto'>
        <h2>✅ Almost there!</h2>
        <p>Copy the URL from your browser address bar and paste it into TradeLedger on your local machine.</p>
        <p>The URL should look like:<br/>
        <code>https://tradeledger-production.up.railway.app/schwab/callback?code=...</code></p>
        <hr/>
        <p><strong>Your auth code (expires in 30 seconds!):</strong></p>
        <textarea rows='3' style='width:100%;font-size:11px'>{code}</textarea>
        <p>Go to <a href='http://localhost:7179/import'>localhost:7179/import</a> 
        and paste the full URL into Step 2.</p>
        </body></html>
    """, "text/html");
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
