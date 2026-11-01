using System.Net;

namespace Wings.Gateway.Proxy;

/// <summary>Controlled responses: generic wording, no internal server names, addresses or stack traces.</summary>
public static class GatewayResponses
{
    public const string LoginPath = "/_wings/login";

    public static async Task WriteAsync(HttpContext context, int status, string code, string message, string? requestId = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        requestId ??= context.TraceIdentifier;
        if (WantsHtml(context.Request))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync($$"""
                <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
                <title>Wings</title><style>body{font-family:system-ui,sans-serif;background:#f4f6f9;color:#1f2933;display:grid;place-items:center;min-height:100vh;margin:0}
                main{background:#fff;padding:32px 40px;border-radius:12px;box-shadow:0 2px 12px rgba(0,0,0,.08);max-width:440px}
                h1{font-size:20px;margin:0 0 12px}p{line-height:1.5}small{color:#6b7280}a{color:#1d4ed8}</style></head>
                <body><main><h1>{{WebUtility.HtmlEncode(Title(status))}}</h1><p>{{WebUtility.HtmlEncode(message)}}</p>
                <p><a href="/">Try again</a> · <a href="{{LoginPath}}">Sign in</a></p><small>Reference: {{WebUtility.HtmlEncode(requestId)}}</small></main></body></html>
                """);
        }
        else
        {
            await context.Response.WriteAsJsonAsync(new { error = code, message, requestId });
        }
    }

    /// <summary>Unauthenticated: browsers go to the login page, API clients get 401.</summary>
    public static Task ChallengeAsync(HttpContext context)
    {
        if (WantsHtml(context.Request) && HttpMethods.IsGet(context.Request.Method))
        {
            var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            context.Response.Redirect($"{LoginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}");
            return Task.CompletedTask;
        }
        return WriteAsync(context, StatusCodes.Status401Unauthorized, "authentication_required", "Sign in to continue.");
    }

    private static bool WantsHtml(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static string Title(int status) => status switch
    {
        401 => "Sign in required",
        403 => "Access denied",
        404 => "Not found",
        502 => "Application unavailable",
        503 => "Service temporarily unavailable",
        _ => "Request failed"
    };

    /// <summary>Only local, relative return URLs are accepted (open-redirect protection).</summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl)) return "/";
        if (!returnUrl.StartsWith('/') || returnUrl.StartsWith("//") || returnUrl.StartsWith("/\\") || returnUrl.Contains('\r')
            || returnUrl.Contains('\n') || returnUrl.StartsWith("/_wings/", StringComparison.OrdinalIgnoreCase))
            return "/";
        return returnUrl;
    }
}
