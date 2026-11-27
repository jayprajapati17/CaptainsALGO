using Microsoft.AspNetCore.Builder;
using Serilog;

namespace AlgoWorker.Startup;

public static class LoggingSetup
{
    /// <summary>
    /// Everything goes to logs/nifty-bot-*.log. The console only echoes the EMA 9/21 pipeline
    /// (lines from AlgoWorker.EmaCross.*), as a compact live view.
    /// </summary>
    public static void ConfigureSerilog(this WebApplicationBuilder builder)
    {
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(e => e.Properties.TryGetValue("SourceContext", out var sc) && sc.ToString().Contains("AlgoWorker.EmaCross"))
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [EMA9/21] [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .WriteTo.File(
                path: "logs/nifty-bot-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
    }
}
