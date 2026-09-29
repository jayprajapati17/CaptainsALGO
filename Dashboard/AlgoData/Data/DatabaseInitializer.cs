using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace AlgoData.Data;

/// <summary>
/// Applies Data/schema-sqlserver.sql against the configured SQL Server
/// database. Safe to call every time the app starts -- every CREATE
/// statement in the script is guarded by an `IF NOT EXISTS` check, so
/// re-running it against an already-initialized database is a no-op.
/// </summary>
public static class DatabaseInitializer
{
    public static void EnsureSchema(this NiftyBotDbContext context)
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema-sqlserver.sql");
        if (!File.Exists(schemaPath))
        {
            // Fallback for when the consuming app doesn't preserve the folder
            // structure -- also check right next to the executable.
            schemaPath = Path.Combine(AppContext.BaseDirectory, "schema-sqlserver.sql");
        }

        if (!File.Exists(schemaPath))
        {
            throw new FileNotFoundException(
                "Could not find schema-sqlserver.sql. Make sure NiftyBot.Shared/Data/schema-sqlserver.sql is set to " +
                "CopyToOutputDirectory=PreserveNewest in the consuming project too.", schemaPath);
        }

        var script = File.ReadAllText(schemaPath);

        // >>> IMPORTANT: T-SQL scripts use "GO" (on its own line) as a BATCH
        // separator, not ';'. GO is NOT a real T-SQL keyword -- it's a
        // client-side directive understood by SSMS/sqlcmd, and sending the
        // literal text "GO" to the server via ADO.NET causes a syntax error.
        // So each batch (everything between two "GO" lines) is executed as
        // ONE ExecuteSqlRaw call -- never split further, since a batch can
        // legitimately contain multiple `;`-separated statements inside a
        // BEGIN...END block (e.g. the whole CREATE TABLE block).
        var batches = Regex
            .Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrWhiteSpace(b) && !IsCommentOnly(b));

        foreach (var batch in batches)
        {
            context.Database.ExecuteSqlRaw(batch);
        }
    }

    private static bool IsCommentOnly(string batch)
    {
        var lines = batch.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.All(line => line.StartsWith("--") || string.IsNullOrWhiteSpace(line));
    }
}