using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Undertow.Server;

/// <summary>Byte-exact response bodies matching the Gleam router.</summary>
public static class Responses
{
    public static IResult Json(string body, int status) =>
        Results.Text(body, "application/json", statusCode: status);

    public static IResult Json(byte[] utf8Body, int status) =>
        Json(Encoding.UTF8.GetString(utf8Body), status);

    public static IResult Unauthorized() => Json("""{"error":"unauthorized"}""", 401);

    public static IResult NotFound() => Json("""{"error":"not found"}""", 404);

    public static IResult BadRequest() => Json("""{"error":"bad request"}""", 400);

    public static IResult Conflict() => Json("""{"error":"conflict"}""", 409);

    /// <summary>Every auth rejection is 401 — the Routerlicious contract (ADR-009).</summary>
    public static IResult AuthError(string message) =>
        Json($$"""{"error":{{JsonString(message)}}}""", 401);

    /// <summary>Bare 404 with empty body — the router's fall-through.</summary>
    public static IResult Empty404() => Results.StatusCode(404);

    /// <summary>A JSON string literal with Erlang-compatible escaping.</summary>
    public static string JsonString(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStringValue(value);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };
}
