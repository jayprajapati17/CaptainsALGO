using System.Globalization;
using AlgoData.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Dashboard.Models;

namespace Dashboard.Controllers;

/// <summary>
/// Settings page: edit the Worker's Upstox / Telegram / Strategy settings, which live in the
/// shared DB's AppSettings table (seeded from the Worker's appsettings.json on its first start).
/// Saving writes the rows, then asks the Worker to reload them (POST /api/settings/reload).
/// </summary>
public sealed class SettingsController : Controller
{
    private static readonly string[] SectionOrder = { "Upstox", "Telegram", "Strategy" };

    private readonly NiftyBotDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SettingsController> _logger;

    public SettingsController(NiftyBotDbContext db, IHttpClientFactory httpClientFactory, ILogger<SettingsController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Index(string? tab, CancellationToken ct)
    {
        var vm = new SettingsViewModel { ActiveTab = tab ?? "Upstox" };
        vm.Message = TempData["SettingsMessage"] as string;
        vm.Warning = TempData["SettingsWarning"] as string;

        try
        {
            vm.Sections = await BuildSectionsAsync(null, null, ct);
            if (vm.Sections.Count == 0)
                vm.Warning ??= "No settings found yet. Start the Worker once -- it creates the AppSettings table and fills it from its appsettings.json.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings.");
            vm.Error = "Could not load settings from the database (has the Worker been started once with the new version, so the AppSettings table exists?).";
        }

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Dictionary<int, string?> values, string? tab, CancellationToken ct)
    {
        var rows = await _db.AppSettings.ToListAsync(ct);
        var errors = new Dictionary<int, string>();
        var submitted = new Dictionary<int, string>();

        foreach (var row in rows)
        {
            if (!values.TryGetValue(row.Id, out var raw)) continue;
            var value = (raw ?? string.Empty).Trim();
            submitted[row.Id] = value;

            var error = Validate(row.ValueType, row.Key, value);
            if (error is not null) errors[row.Id] = error;
        }

        if (errors.Count > 0)
        {
            var vmWithErrors = new SettingsViewModel
            {
                ActiveTab = tab ?? "Upstox",
                Error = $"{errors.Count} value(s) are invalid -- nothing was saved. Fix the highlighted fields and save again.",
                Sections = await BuildSectionsAsync(submitted, errors, ct)
            };
            return View("Index", vmWithErrors);
        }

        var changedRestart = new List<string>();
        var changedCount = 0;
        foreach (var row in rows)
        {
            if (!submitted.TryGetValue(row.Id, out var value)) continue;
            var normalized = Normalize(row.ValueType, value);
            if (string.Equals(row.Value ?? string.Empty, normalized, StringComparison.Ordinal)) continue;

            row.Value = normalized;
            row.UpdatedAt = DateTimeOffset.Now;
            changedCount++;
            if (row.RequiresRestart) changedRestart.Add(row.Key);
        }

        if (changedCount == 0)
        {
            TempData["SettingsMessage"] = "No changes to save.";
            return RedirectToAction(nameof(Index), new { tab });
        }

        await _db.SaveChangesAsync(ct);

        var applied = await TryReloadWorkerAsync(ct);
        if (applied)
        {
            TempData["SettingsMessage"] = $"Saved {changedCount} change(s) and applied them to the running Worker.";
        }
        else
        {
            TempData["SettingsMessage"] = $"Saved {changedCount} change(s) to the database.";
            TempData["SettingsWarning"] = "The Worker could not be reached, so nothing is applied yet. The new values will be used the next time the Worker starts.";
        }

        if (changedRestart.Count > 0)
        {
            var note = $"These settings are only read at Worker startup -- restart the Worker for them to take effect: {string.Join(", ", changedRestart)}.";
            TempData["SettingsWarning"] = TempData["SettingsWarning"] is string existing ? existing + " " + note : note;
        }

        return RedirectToAction(nameof(Index), new { tab });
    }

    // ------------------------------------------------------------------

    private async Task<List<SettingSection>> BuildSectionsAsync(
        Dictionary<int, string>? overrideValues, Dictionary<int, string>? errors, CancellationToken ct)
    {
        var rows = await _db.AppSettings.AsNoTracking()
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Key)
            .ToListAsync(ct);

        var sections = new List<SettingSection>();
        foreach (var sectionName in SectionOrder.Concat(rows.Select(r => r.Section)).Distinct())
        {
            var sectionRows = rows.Where(r => r.Section == sectionName).ToList();
            if (sectionRows.Count == 0) continue;

            var section = new SettingSection { Name = sectionName };
            foreach (var group in sectionRows.GroupBy(r => r.GroupName))
            {
                section.Groups.Add(new SettingGroup
                {
                    Name = string.IsNullOrWhiteSpace(group.Key) ? "General" : group.Key,
                    Items = group.Select(r => new SettingItem
                    {
                        Id = r.Id,
                        Section = r.Section,
                        Key = r.Key,
                        Value = overrideValues is not null && overrideValues.TryGetValue(r.Id, out var v) ? v : r.Value ?? string.Empty,
                        ValueType = r.ValueType,
                        IsSecret = r.IsSecret,
                        RequiresRestart = r.RequiresRestart,
                        Description = r.Description,
                        Error = errors is not null && errors.TryGetValue(r.Id, out var e) ? e : null
                    }).ToList()
                });
            }
            sections.Add(section);
        }
        return sections;
    }

    private static string? Validate(string valueType, string key, string value)
    {
        switch (valueType)
        {
            case "int":
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? null : "Whole number required.";
            case "double":
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? null : "Number required (use . for decimals).";
            case "bool":
                return value is "true" or "false" ? null : "Must be true or false.";
            default:
                if (key.EndsWith("Time", StringComparison.Ordinal) && !TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return "Time must be HH:mm (e.g. 15:20).";
                return null;
        }
    }

    // Store numbers in a canonical invariant form so "15", "15.0" and " 15 " don't register as changes.
    private static string Normalize(string valueType, string value) => valueType switch
    {
        "int" => int.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        "double" => double.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
        _ => value
    };

    private async Task<bool> TryReloadWorkerAsync(CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("WorkerApi");
            using var response = await client.PostAsync("/api/settings/reload", content: null, ct);
            if (response.IsSuccessStatusCode) return true;

            _logger.LogWarning("Worker rejected settings reload: {Status}", (int)response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach the Worker to apply settings.");
            return false;
        }
    }
}