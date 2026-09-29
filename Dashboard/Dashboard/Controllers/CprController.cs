using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Controllers;

/// <summary>Today's CPR (Central Pivot Range) reading -- a later task wires up the actual DB query.</summary>
public sealed class CprController : Controller
{
    public IActionResult Index() => View();
}
