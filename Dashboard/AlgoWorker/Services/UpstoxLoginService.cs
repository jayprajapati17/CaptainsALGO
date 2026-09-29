using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AlgoWorker.Configuration;

namespace AlgoWorker.Services;

/// <summary>
/// Interactive `dotnet run -- login` helper. Automates everything AROUND the
/// actual Upstox login except the login itself -- opening the correct URL,
/// capturing the OAuth redirect, exchanging the code for a token, and writing
/// it into appsettings.json. You still complete the login/OTP/PIN yourself in
/// the browser, exactly as Upstox requires (see their community post:
/// automating that specific step is against their stated policy). This tool
/// only removes the tedious surrounding steps -- manually building the URL,
/// copy-pasting the code out of the redirected URL, calling the token
/// endpoint yourself, and hand-editing the config file.
/// </summary>
public sealed class UpstoxLoginService
{
    private readonly UpstoxOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger<UpstoxLoginService> _logger;

    public UpstoxLoginService(IOptions<UpstoxOptions> options, HttpClient http, ILogger<UpstoxLoginService> logger)
    {
        _options = options.Value;
        _http = http;
        _logger = logger;
    }

    public async Task<bool> RunInteractiveLoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            Console.WriteLine("ERROR: Upstox:ApiKey / Upstox:ApiSecret are not set in appsettings.json. Fill those in first.");
            return false;
        }

        var authorizeUrl =
            $"{_options.LoginAuthorizeUrl}?response_type=code" +
            $"&client_id={Uri.EscapeDataString(_options.ApiKey)}" +
            $"&redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}";

        Console.WriteLine("Starting local listener to catch the Upstox redirect...");
        using var listener = new HttpListener();
        listener.Prefixes.Add(NormalizeForHttpListener(_options.RedirectUri));

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"ERROR: Could not start local listener on {_options.RedirectUri} -- {ex.Message}");
            Console.WriteLine("Common cause: something else is already using that port, or RedirectUri isn't localhost.");
            return false;
        }

        Console.WriteLine("Opening your browser -- please log in and complete OTP/PIN as usual...");
        try
        {
            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not auto-open the browser ({ex.Message}). Please open this URL manually:\n{authorizeUrl}");
        }

        Console.WriteLine("Waiting for you to finish logging in (up to 3 minutes)...");
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpListenerContext context;
        try
        {
            var contextTask = listener.GetContextAsync();
            await using (linkedCts.Token.Register(() => listener.Stop()))
            {
                context = await contextTask;
            }
        }
        catch (Exception) when (timeoutCts.IsCancellationRequested)
        {
            Console.WriteLine("ERROR: Timed out waiting for login (3 minutes). Run `dotnet run -- login` again.");
            return false;
        }
        // >>> FIXED: previously called listener.Stop() here in a `finally`,
        // BEFORE writing the HTTP response back to the browser using
        // context.Response.OutputStream. Stopping the listener disposes
        // resources that the response stream still needs, causing an
        // ObjectDisposedException (ThreadPoolBoundHandle) on the write below.
        // The listener must stay alive until AFTER the response is fully sent.

        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];

        // Respond to the browser so the user sees a clean confirmation instead of a hanging tab.
        var responseHtml = error is null
            ? "<html><body><h2>Login captured. You can close this tab and return to the console.</h2></body></html>"
            : $"<html><body><h2>Login failed: {error}. Please close this tab and check the console.</h2></body></html>";
        var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, ct);
        context.Response.OutputStream.Close();

        // >>> NEW: safe to stop the listener now that the response is fully
        // sent -- this was the missing step (see comment above where we used
        // to stop it too early).
        if (listener.IsListening) listener.Stop();

        if (error is not null || string.IsNullOrWhiteSpace(code))
        {
            Console.WriteLine($"ERROR: Upstox returned an error or no authorization code (error={error}).");
            return false;
        }

        Console.WriteLine("Authorization code received. Exchanging it for an access token...");

        var tokenRequestBody = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _options.ApiKey,
            ["client_secret"] = _options.ApiSecret,
            ["redirect_uri"] = _options.RedirectUri,
            ["grant_type"] = "authorization_code"
        });

        using var tokenResponse = await _http.PostAsync(_options.LoginTokenUrl, tokenRequestBody, ct);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(ct);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            Console.WriteLine($"ERROR: Token exchange failed ({(int)tokenResponse.StatusCode}): {tokenBody}");
            return false;
        }

        using var doc = JsonDocument.Parse(tokenBody);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            Console.WriteLine($"ERROR: No access_token in response: {tokenBody}");
            return false;
        }

        UpdateAccessTokenInAppSettings(accessToken);

        Console.WriteLine($"SUCCESS. New access token saved (starts with: {accessToken[..Math.Min(10, accessToken.Length)]}...).");
        Console.WriteLine("If the Worker Service is already running, it will pick this up automatically (config reload-on-change) -- no restart needed.");
        return true;
    }

    /// <summary>
    /// Surgical, comment-preserving update: appsettings.json has `//` comments
    /// scattered through it (which System.Text.Json's config LOADER tolerates,
    /// but a naive JsonSerializer round-trip would strip). So this does a plain
    /// text find/replace of just the AccessToken line instead of a full parse+rewrite.
    /// </summary>
    private static void UpdateAccessTokenInAppSettings(string newToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"WARNING: could not find {path} to update -- please paste the token in manually:\n{newToken}");
            return;
        }

        var lines = File.ReadAllLines(path);
        var updated = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("\"AccessToken\""))
            {
                var indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                var trailingComma = lines[i].TrimEnd().EndsWith(",") ? "," : "";
                lines[i] = $"{indent}\"AccessToken\": \"{newToken}\"{trailingComma}";
                updated = true;
                break;
            }
        }

        if (!updated)
        {
            Console.WriteLine($"WARNING: could not find an \"AccessToken\" line in {path} -- please paste the token in manually:\n{newToken}");
            return;
        }

        File.WriteAllLines(path, lines);
        Console.WriteLine($"Updated: {path}");
    }

    private static string NormalizeForHttpListener(string redirectUri)
    {
        // HttpListener prefixes must end with '/'.
        return redirectUri.EndsWith('/') ? redirectUri : redirectUri + "/";
    }
}