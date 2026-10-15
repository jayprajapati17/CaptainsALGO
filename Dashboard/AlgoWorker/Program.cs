using AlgoWorker.Startup;
using Microsoft.AspNetCore.Builder;

// The Worker process: the Nifty and Bank Nifty pipelines (BackgroundServices) plus Kestrel for the SignalR hub,
// the command API and the Upstox OAuth callback. Each concern lives in /Startup.
var builder = WebApplication.CreateBuilder(args);

var dbConfigSource = builder.AddWorkerConfiguration();   // files, env, secrets, then the DB-backed AppSettings layer
builder.ConfigureSerilog();

builder.Services
    .AddWorkerOptions(builder.Configuration)
    .AddSharedInfrastructure(builder.Configuration)
    .AddNiftyStrategies()
    .AddBankNifty()
    .AddDashboardChannel(builder.Configuration);

var app = builder.Build();

app.UseCors("DashboardCors");   // must come before endpoint mapping
app.MapWorkerEndpoints(dbConfigSource);

await app.SeedSettingsAsync(dbConfigSource);
await app.CloseOrphanedPositionsAsync();

await app.RunAsync();
