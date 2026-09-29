using Microsoft.AspNetCore.Mvc;

namespace Dashboard.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Error() => View();
}
