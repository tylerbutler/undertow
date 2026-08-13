using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Undertow.Abstractions;
using Undertow.Protocol;

namespace Undertow.Server;

/// <summary>
/// The REST router, one-for-one with the Gleam `rest` dispatcher: document
/// lifecycle + deltas catch-up + git object storage + token mint. Every
/// handler calls its authorize_* helper explicitly at the top.
/// </summary>
public static class Routes
{
    public static void MapUndertowRoutes(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<UndertowConfig>();
        var service = app.Services.GetRequiredService<DocumentService>();
        var time = app.Services.GetRequiredService<TimeProvider>();

        long Now() => time.GetUtcNow().ToUnixTimeSeconds();

        // Unauthenticated readiness probe, byte-identical to levee's
        // HealthController. HEAD as well as GET for container probes.
        app.MapMethods("/health", ["GET", "HEAD"], () => Responses.Json("""{"status":"ok"}""", 200));

        app.MapPost("/api/tenants/{tenant}/token-mint", async (string tenant, HttpRequest request) =>
        {
            var authorization = request.Headers.Authorization.ToString();
            if (tenant != config.Tenant)
                return Responses.Unauthorized();
            if (config.TokenMintSecret is null)
                return Responses.NotFound();
            if (authorization.Length == 0)
                return Responses.Unauthorized();
            if (!AuthBoundary.tokenMint(authorization, config.TokenMintSecret))
                return Responses.Unauthorized();

            var body = await ReadBodyAsync(request);
            if (ParseTokenMintRequest(body) is not var (documentId, bodyTenant))
                return Responses.BadRequest();
            if (bodyTenant is not null && bodyTenant != tenant)
                return Responses.Unauthorized();

            const int expiresIn = 3600;
            var token = AuthBoundary.mintToken(
                tenant, documentId, ["doc:read", "doc:write", "summary:read", "summary:write"],
                config.TokenMintUserId, config.JwtSecret, Now(), expiresIn, NewJti());
            var userJson =
                $$"""{"id":{{Responses.JsonString(config.TokenMintUserId)}},"name":{{Responses.JsonString(config.TokenMintUserName)}}}""";
            return Responses.Json(
                $$"""{"jwt":{{Responses.JsonString(token)}},"expiresIn":{{expiresIn}},"user":{{userJson}}}""",
                200);
        });

        app.MapPost("/documents/{tenant}", async (string tenant, HttpRequest request) =>
        {
            var body = await ReadBodyAsync(request);
            var auth = AuthBoundary.tenantWrite(
                config.Tenant, tenant, HasAuthorization(request), request.Headers.Authorization.ToString(),
                config.JwtSecret, Now());
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            // Levee: params["id"] || generate_document_id().
            var doc = RequestedDocumentId(body) ?? GenerateDocumentId();
            return await CreateDocument(tenant, doc, body);
        });

        app.MapGet("/documents/{tenant}/session/{doc}", async (string tenant, string doc, HttpRequest request) =>
        {
            var auth = AuthorizeRead(request, tenant, doc);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);
            if (!await service.ExistsAsync(UndertowConfig.Topic(tenant, doc)))
                return Responses.NotFound();
            return Responses.Json(SessionInfoJson(tenant), 200);
        });

        app.MapGet("/documents/{tenant}/{doc}/deltas", (string tenant, string doc, HttpRequest request) =>
            Deltas(request, tenant, doc, envelope: false));

        app.MapGet("/deltas/{tenant}/{doc}", (string tenant, string doc, HttpRequest request) =>
            Deltas(request, tenant, doc, envelope: true));

        app.MapGet("/documents/{tenant}/{doc}", async (string tenant, string doc, HttpRequest request) =>
        {
            var auth = AuthorizeRead(request, tenant, doc);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);
            var topic = UndertowConfig.Topic(tenant, doc);
            if (!await service.ExistsAsync(topic))
                return Responses.NotFound();
            var sequenceNumber = await service.SequenceNumberAsync(topic);
            return Responses.Json(
                $$"""{"id":{{Responses.JsonString(doc)}},"tenantId":{{Responses.JsonString(tenant)}},"sequenceNumber":{{sequenceNumber}}}""",
                200);
        });

        app.MapMethods("/documents/{tenant}/{doc}", ["POST", "PUT"], async (string tenant, string doc, HttpRequest request) =>
        {
            var auth = AuthBoundary.write(
                config.Tenant, tenant, doc, HasAuthorization(request), request.Headers.Authorization.ToString(),
                config.JwtSecret, Now());
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);
            return await CreateDocument(tenant, doc, await ReadBodyAsync(request));
        });

        app.MapGet("/repos/{tenant}/commits", async (string tenant, HttpRequest request) =>
        {
            var auth = AuthorizeStorageRead(request, tenant);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var countRaw = request.Query["count"].ToString();
            int count;
            if (countRaw.Length == 0)
                count = 1;
            else if (!int.TryParse(countRaw, out count) || count <= 0)
                return Responses.BadRequest();

            var requested = request.Query["sha"].ToString();
            if (requested.Length == 0)
                return Responses.BadRequest();

            // ?sha=<documentId> resolves through the summary ref first.
            var sha = await service.GitObjects.GetRefAsync(tenant, $"refs/heads/{requested}") ?? requested;
            var chain = await Historian.LoadCommitChainAsync(service.GitObjects, tenant, sha, count);
            return Responses.Json(
                SiltBoundary.commitHistoryResponse(config.PublicUrl, tenant, sha, count, chain), 200);
        });

        app.MapGet("/repos/{tenant}/git/refs", async (string tenant, HttpRequest request) =>
        {
            var auth = AuthorizeStorageRead(request, tenant);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var refs = await service.GitObjects.ListRefsAsync(tenant);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, Responses.WriterOptions))
            {
                writer.WriteStartArray();
                foreach (var (path, sha) in refs)
                    writer.WriteRawValue(SiltBoundary.refResponse(config.PublicUrl, tenant, path, sha), true);
                writer.WriteEndArray();
            }

            return Responses.Json(buffer.WrittenSpan.ToArray(), 200);
        });

        app.MapPost("/repos/{tenant}/git/refs", async (string tenant, HttpRequest request) =>
        {
            var auth = AuthorizeStorageWrite(request, tenant);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var body = await ReadBodyAsync(request);
            string refPath = "", sha = "";
            if (!SiltBoundary.tryDecodeRef(body, ref refPath, ref sha))
                return Responses.BadRequest();

            if (!await service.GitObjects.TryCreateRefAsync(tenant, SiltBoundary.normalizeRef(refPath), sha))
                return Responses.Conflict();
            return Responses.Json(SiltBoundary.refResponse(config.PublicUrl, tenant, refPath, sha), 201);
        });

        app.MapMethods("/repos/{tenant}/git/refs/{**path}", ["GET", "PATCH"],
            async (string tenant, string path, HttpRequest request) =>
        {
            var refPath = $"refs/{path}";
            if (HttpMethods.IsGet(request.Method))
            {
                var auth = AuthorizeStorageRead(request, tenant);
                if (!auth.Ok)
                    return Responses.AuthError(auth.ErrorMessage);
                var sha = await service.GitObjects.GetRefAsync(tenant, SiltBoundary.normalizeRef(refPath));
                if (sha is null)
                    return Responses.NotFound();
                return Responses.Json(SiltBoundary.refResponse(config.PublicUrl, tenant, refPath, sha), 200);
            }
            else
            {
                var auth = AuthorizeStorageWrite(request, tenant);
                if (!auth.Ok)
                    return Responses.AuthError(auth.ErrorMessage);
                var body = await ReadBodyAsync(request);
                if (ParseShaField(body) is not { } sha)
                    return Responses.BadRequest();
                await service.GitObjects.PutRefAsync(tenant, SiltBoundary.normalizeRef(refPath), sha);
                return Responses.Json(SiltBoundary.refResponse(config.PublicUrl, tenant, refPath, sha), 200);
            }
        });

        app.MapPost("/repos/{tenant}/git/{kind}", async (string tenant, string kind, HttpRequest request) =>
        {
            // Kind validated in the handler, not a route constraint, so an
            // unknown kind falls through to 404 like the Gleam case does.
            if (kind is not ("blobs" or "trees" or "commits"))
                return Responses.Empty404();

            var auth = AuthorizeStorageWrite(request, tenant);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var body = await ReadBodyAsync(request);
            if (SiltBoundary.objectId(kind, body) is not { } sha)
                return Responses.BadRequest();
            await service.GitObjects.PutObjectAsync(tenant, sha, body);

            // Levee's GitController returns {sha, url} for a created blob but
            // the whole object for a created tree or commit.
            if (kind == "blobs")
            {
                return Responses.Json(
                    $$"""{"sha":{{Responses.JsonString(sha)}},"url":{{Responses.JsonString($"{config.PublicUrl}/repos/{tenant}/git/{kind}/{sha}")}}}""",
                    201);
            }

            var stored = await service.GitObjects.GetObjectAsync(tenant, sha);
            if (stored is null)
                return Responses.BadRequest();
            var closure = await Historian.LoadTreeClosureAsync(service.GitObjects, tenant, stored);
            var response = SiltBoundary.objectResponse(config.PublicUrl, tenant, kind, sha, stored, false, closure);
            return response is null ? Responses.BadRequest() : Responses.Json(response, 201);
        });

        app.MapGet("/repos/{tenant}/git/{kind}/{sha}", async (string tenant, string kind, string sha, HttpRequest request) =>
        {
            if (kind is not ("blobs" or "trees" or "commits"))
                return Responses.Empty404();

            var auth = AuthorizeStorageRead(request, tenant);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var body = await service.GitObjects.GetObjectAsync(tenant, sha);
            if (body is null)
                return Responses.NotFound();

            var recursive = request.Query["recursive"].ToString() == "1";
            var closure = recursive
                ? await Historian.LoadTreeClosureAsync(service.GitObjects, tenant, body)
                : new Dictionary<string, string>();
            var response = SiltBoundary.objectResponse(config.PublicUrl, tenant, kind, sha, body, recursive, closure);
            return response is null ? Responses.BadRequest() : Responses.Json(response, 200);
        });

        // The router's fall-through: bare 404 with an empty body.
        app.MapFallback(() => Responses.Empty404());
        return;

        // ── Shared handler pieces ───────────────────────────────────────────

        AuthOutcome AuthorizeRead(HttpRequest request, string tenant, string doc) =>
            AuthBoundary.read(
                config.Tenant, tenant, doc, HasAuthorization(request),
                request.Headers.Authorization.ToString(), config.JwtSecret, Now());

        AuthOutcome AuthorizeStorageRead(HttpRequest request, string tenant) =>
            AuthBoundary.storageRead(
                config.Tenant, tenant, HasAuthorization(request),
                request.Headers.Authorization.ToString(), config.JwtSecret, Now());

        AuthOutcome AuthorizeStorageWrite(HttpRequest request, string tenant) =>
            AuthBoundary.storageWrite(
                config.Tenant, tenant, HasAuthorization(request),
                request.Headers.Authorization.ToString(), config.JwtSecret, Now());

        async Task<IResult> Deltas(HttpRequest request, string tenant, string doc, bool envelope)
        {
            var auth = AuthorizeRead(request, tenant, doc);
            if (!auth.Ok)
                return Responses.AuthError(auth.ErrorMessage);

            var topic = UndertowConfig.Topic(tenant, doc);
            if (!await service.ExistsAsync(topic))
                return Responses.NotFound();

            var from = long.TryParse(request.Query["from"].ToString(), out var f) ? f : -1;
            var to = long.TryParse(request.Query["to"].ToString(), out var t) ? t : long.MaxValue;
            var ops = (await service.SinceAsync(topic, from))
                .Where(op => op.SequenceNumber <= to)
                .Take(2000);

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, Responses.WriterOptions))
            {
                if (envelope)
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("value");
                }

                writer.WriteStartArray();
                foreach (var op in ops)
                    WriteStoredMessage(writer, op);
                writer.WriteEndArray();

                if (envelope)
                    writer.WriteEndObject();
            }

            return Responses.Json(buffer.WrittenSpan.ToArray(), 200);
        }

        async Task<IResult> CreateDocument(string tenant, string doc, string body)
        {
            var topic = UndertowConfig.Topic(tenant, doc);
            var result = await service.CreateInitializedAsync(topic, tenant, body, Now());
            switch (result)
            {
                case CreateInitializedResult.AlreadyExists:
                    return Responses.Conflict();
                case CreateInitializedResult.InvalidInitialSummary:
                    return Responses.BadRequest();
                default:
                    // Publish the ref mirroring the committed summary pointer —
                    // after, not during, persist, so a crash can only leave the
                    // ref lagging.
                    var (handle, _) = await service.SummaryAsync(topic);
                    if (handle.Length > 0)
                        await service.PublishSummaryRefAsync(tenant, doc, handle);

                    // Levee responds with the bare document id (a JSON string)
                    // unless the caller asked for discovery.
                    var responseBody = EnableDiscovery(body)
                        ? $$"""{"id":{{Responses.JsonString(doc)}},"session":{{SessionInfoJson(tenant)}}}"""
                        : Responses.JsonString(doc);
                    return Responses.Json(responseBody, 201);
            }
        }

        string SessionInfoJson(string tenant) =>
            $$"""{"ordererUrl":{{Responses.JsonString(config.PublicUrl)}},"historianUrl":{{Responses.JsonString($"{config.PublicUrl}/repos/{tenant}")}},"deltaStreamUrl":{{Responses.JsonString(config.PublicUrl)}},"isSessionAlive":true,"isSessionActive":true}""";
    }

    private static bool HasAuthorization(HttpRequest request) =>
        request.Headers.ContainsKey("Authorization");

    internal static async Task<string> ReadBodyAsync(HttpRequest request) =>
        await RestLessMiddleware.ReadBodyAsync(request.Body, request.HttpContext.RequestAborted);

    /// <summary>Stored op JSON is spliced raw when it already carries an int
    /// sequenceNumber; otherwise wrapped, matching session.stored_message_json.</summary>
    private static void WriteStoredMessage(Utf8JsonWriter writer, OpRecord op)
    {
        var hasSequenceNumber = false;
        try
        {
            using var doc = JsonDocument.Parse(op.Payload);
            hasSequenceNumber =
                doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sequenceNumber", out var sn) &&
                sn.ValueKind == JsonValueKind.Number;
        }
        catch (JsonException)
        {
        }

        if (hasSequenceNumber)
        {
            writer.WriteRawValue(op.Payload, skipInputValidation: true);
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequenceNumber", op.SequenceNumber);
            writer.WriteString("contents", op.Payload);
            writer.WriteEndObject();
        }
    }

    private static (string DocumentId, string? TenantId)? ParseTokenMintRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!doc.RootElement.TryGetProperty("documentId", out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            string? tenant = null;
            if (doc.RootElement.TryGetProperty("tenantId", out var t) && t.ValueKind == JsonValueKind.String)
                tenant = t.GetString();
            return (id.GetString()!, tenant);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RequestedDocumentId(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.String &&
                id.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool EnableDiscovery(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("enableDiscovery", out var flag) &&
                   flag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ParseShaField(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("sha", out var sha) &&
                sha.ValueKind == JsonValueKind.String)
            {
                return sha.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    /// <summary>32 uppercase hex chars, matching Gleam's base16_encode.</summary>
    private static string GenerateDocumentId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static string NewJti() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
