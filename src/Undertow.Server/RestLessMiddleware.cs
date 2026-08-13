using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Undertow.Server;

/// <summary>
/// RestLess protocol support: a POST whose Content-Type carries ";restless"
/// tunnels the real request in a form-encoded body (method=, header=, body=).
/// Must run before routing, because it rewrites the request method.
/// </summary>
public static class RestLessMiddleware
{
    public const int MaxBodyBytes = 4_000_000;

    public static IApplicationBuilder UseRestLess(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var contentType = context.Request.ContentType;
            if (contentType is not null && contentType.Contains(";restless", StringComparison.Ordinal))
            {
                var raw = await ReadBodyAsync(context.Request.Body, context.RequestAborted);
                var fields = QueryHelpers.ParseQuery(raw);

                if (fields.TryGetValue("method", out var method) && method.Count > 0 &&
                    IsKnownMethod(method[0]))
                {
                    context.Request.Method = method[0]!.ToUpperInvariant();
                }

                foreach (var header in fields.TryGetValue("header", out var headers) ? headers : default)
                {
                    var separator = header?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
                    if (header is not null && separator > 0)
                    {
                        context.Request.Headers[header[..separator].ToLowerInvariant()] =
                            header[(separator + 2)..];
                    }
                }

                var body = fields.TryGetValue("body", out var bodies) && bodies.Count > 0 ? bodies[0]! : "";
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                context.Request.Body = new MemoryStream(bodyBytes);
                context.Request.ContentLength = bodyBytes.Length;
                context.Request.ContentType = "application/json";
            }

            await next();
        });

    private static bool IsKnownMethod(string? method) =>
        method?.ToUpperInvariant() is "GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS";

    internal static async Task<string> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[65536];
        while (buffer.Length <= MaxBodyBytes)
        {
            var read = await body.ReadAsync(chunk, ct);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }

        return buffer.Length > MaxBodyBytes ? "" : Encoding.UTF8.GetString(buffer.ToArray());
    }
}
