using AlgoData.Data;

var builder = WebApplication.CreateBuilder(args);

// ---- MVC ----
builder.Services.AddControllersWithViews();

// ---- Shared database (NiftyBot.Shared) -- same connection string as the
// Worker Service, both apps must point at the SAME database. Not queried yet
// in this task (History/Signals Log/CPR pages come in Tasks 10/11), but wired
// up now since it's part of this task's scope ("NiftyBot.Shared reference").
builder.Services.AddNiftyBotDatabase(builder.Configuration["NiftyBot:ConnectionString"]!);

// ---- Worker Service base URL -- everything the Dashboard needs from the
// Worker (SignalR hub for live updates, Command API for manual close,
// token-status/generate endpoints) lives at this one base address. Read
// via configuration (not hardcoded) so it survives moving off localhost later.
// Task 8 (Live page) is what actually calls this; registered here since this
// task also covers "Configuration: Worker Service ka base URL".
var workerBaseUrl = builder.Configuration["NiftyBot:WorkerBaseUrl"]
    ?? throw new InvalidOperationException("NiftyBot:WorkerBaseUrl is not configured in appsettings.json.");

builder.Services.AddHttpClient("WorkerApi", client =>
{
    client.BaseAddress = new Uri(workerBaseUrl);
    // Short timeout so a stopped Worker shows an error banner quickly instead of hanging the page load.
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Made available to views (e.g. to build the SignalR hub URL for the JS client in Task 8/9).
builder.Services.AddSingleton(new WorkerEndpoints(workerBaseUrl));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Live}/{action=Index}/{id?}"); // Live Dashboard is the landing page

app.Run();

/// <summary>
/// Just the handful of Worker URLs the views/JS need directly (the SignalR
/// hub, mainly). The Command API and token endpoints are called from
/// controllers via the "WorkerApi" named HttpClient above, not from here.
/// </summary>
public sealed record WorkerEndpoints(string BaseUrl)
{
    public string PositionsHubUrl => $"{BaseUrl.TrimEnd('/')}/hubs/positions";

    // >>> NEW (Task 9): token generation is a full Worker-owned OAuth flow (it holds the
    // Upstox ApiKey/ApiSecret), so the Dashboard only ever needs these two URLs -- one to
    // check today's status, one to open in a new tab for the actual login/OTP.
    public string TokenStatusUrl => $"{BaseUrl.TrimEnd('/')}/api/token/status";
    public string TokenAuthorizeUrl => $"{BaseUrl.TrimEnd('/')}/auth/upstox/authorize";
}