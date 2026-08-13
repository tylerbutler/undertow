using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Undertow.Server.Tests;

public class RestApiTests : IClassFixture<TestServerFixture>
{
    private readonly TestServerFixture _factory;

    public RestApiTests(TestServerFixture factory) => _factory = factory;

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Health_IsByteExact_ForGetAndHead()
    {
        using var client = Client();
        var body = await client.GetStringAsync("/health");
        Assert.Equal("{\"status\":\"ok\"}", body);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/health"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task CreateDocument_ReturnsBareIdString()
    {
        using var client = Client(TestServerFixture.MintJwt("bare-id-doc"));
        var response = await client.PostAsync("/documents/fluid", JsonBody("""{"id":"bare-id-doc"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"bare-id-doc\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateDocument_DuplicateIs409()
    {
        using var client = Client(TestServerFixture.MintJwt("dup-doc"));
        var first = await client.PostAsync("/documents/fluid", JsonBody("""{"id":"dup-doc"}"""));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await client.PostAsync("/documents/fluid", JsonBody("""{"id":"dup-doc"}"""));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("""{"error":"conflict"}""", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingAuthorization_Is401WithLeveeWording()
    {
        using var client = Client();
        var response = await client.PostAsync("/documents/fluid", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":"Missing Authorization header"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InsufficientScopes_Is401_NotAspNets403()
    {
        // ADR-009: 401 makes a Fluid client refresh its token and retry; 403 is
        // fatal. rest-api.test.ts expects 403 here and is SUPPOSED to fail —
        // this test pins the deliberate divergence.
        using var client = Client(TestServerFixture.MintJwt("scoped-doc", scopes: ["doc:read"]));
        var response = await client.PostAsync("/documents/fluid", JsonBody("""{"id":"scoped-doc"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("scope", body);
    }

    [Fact]
    public async Task ExpiredToken_Is401WithExpiryMessage()
    {
        var expired = TestServerFixture.MintJwt(
            "expired-doc", now: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200, expiresIn: 3600);
        using var client = Client(expired);
        var response = await client.GetAsync("/documents/fluid/expired-doc");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("expired", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deltas_EnvelopeDialects()
    {
        var token = TestServerFixture.MintJwt("deltas-doc");
        using var client = Client(token);
        await client.PostAsync("/documents/fluid", JsonBody("""{"id":"deltas-doc"}"""));

        // Levee dialect: {"value":[...]}
        Assert.Equal("""{"value":[]}""", await client.GetStringAsync("/deltas/fluid/deltas-doc"));
        // Routerlicious dialect: bare array.
        Assert.Equal("[]", await client.GetStringAsync("/documents/fluid/deltas-doc/deltas"));
    }

    [Fact]
    public async Task GitWorkflow_BlobTreeCommitRef()
    {
        var token = TestServerFixture.MintJwt("git-doc");
        using var client = Client(token);

        var blob = await client.PostAsync("/repos/fluid/git/blobs",
            JsonBody("""{"content":"hello","encoding":"utf-8"}"""));
        Assert.Equal(HttpStatusCode.Created, blob.StatusCode);
        using var blobJson = JsonDocument.Parse(await blob.Content.ReadAsStringAsync());
        var blobSha = blobJson.RootElement.GetProperty("sha").GetString()!;
        Assert.Equal("b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0", blobSha);

        var tree = await client.PostAsync("/repos/fluid/git/trees",
            JsonBody($$"""{"tree":[{"path":"a.txt","mode":"100644","type":"blob","sha":"{{blobSha}}"}]}"""));
        Assert.Equal(HttpStatusCode.Created, tree.StatusCode);
        using var treeJson = JsonDocument.Parse(await tree.Content.ReadAsStringAsync());
        var treeSha = treeJson.RootElement.GetProperty("sha").GetString()!;

        var commit = await client.PostAsync("/repos/fluid/git/commits",
            JsonBody($$"""{"tree":"{{treeSha}}","parents":[],"message":"m","author":{"name":"A","email":"a@x","date":"1"}""" + "}"));
        Assert.Equal(HttpStatusCode.Created, commit.StatusCode);
        using var commitJson = JsonDocument.Parse(await commit.Content.ReadAsStringAsync());
        var commitSha = commitJson.RootElement.GetProperty("sha").GetString()!;

        var createRef = await client.PostAsync("/repos/fluid/git/refs",
            JsonBody($$"""{"ref":"refs/heads/git-doc","sha":"{{commitSha}}"}"""));
        Assert.Equal(HttpStatusCode.Created, createRef.StatusCode);

        // Duplicate create conflicts (CAS).
        var dupRef = await client.PostAsync("/repos/fluid/git/refs",
            JsonBody($$"""{"ref":"refs/heads/git-doc","sha":"{{commitSha}}"}"""));
        Assert.Equal(HttpStatusCode.Conflict, dupRef.StatusCode);

        var getRef = await client.GetAsync("/repos/fluid/git/refs/heads/git-doc");
        Assert.Equal(HttpStatusCode.OK, getRef.StatusCode);
        using var refJson = JsonDocument.Parse(await getRef.Content.ReadAsStringAsync());
        Assert.Equal(commitSha, refJson.RootElement.GetProperty("object").GetProperty("sha").GetString());

        // Commit history resolves ?sha=<documentId> through refs/heads/.
        var commits = await client.GetAsync("/repos/fluid/commits?sha=git-doc&count=5");
        Assert.Equal(HttpStatusCode.OK, commits.StatusCode);
        using var commitsJson = JsonDocument.Parse(await commits.Content.ReadAsStringAsync());
        Assert.Equal(1, commitsJson.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task RecursiveTree_FlattensWithPrefixedPaths()
    {
        var token = TestServerFixture.MintJwt("tree-doc");
        using var client = Client(token);

        var blob = await client.PostAsync("/repos/fluid/git/blobs", JsonBody("""{"content":"x"}"""));
        using var blobJson = JsonDocument.Parse(await blob.Content.ReadAsStringAsync());
        var blobSha = blobJson.RootElement.GetProperty("sha").GetString()!;

        var child = await client.PostAsync("/repos/fluid/git/trees",
            JsonBody($$"""{"tree":[{"path":"b.txt","type":"blob","sha":"{{blobSha}}"}]}"""));
        using var childJson = JsonDocument.Parse(await child.Content.ReadAsStringAsync());
        var childSha = childJson.RootElement.GetProperty("sha").GetString()!;

        var root = await client.PostAsync("/repos/fluid/git/trees",
            JsonBody($$"""{"tree":[{"path":"dir","type":"tree","sha":"{{childSha}}"}]}"""));
        using var rootJson = JsonDocument.Parse(await root.Content.ReadAsStringAsync());
        var rootSha = rootJson.RootElement.GetProperty("sha").GetString()!;

        var flat = await client.GetStringAsync($"/repos/fluid/git/trees/{rootSha}?recursive=1");
        Assert.Contains("\"path\":\"dir/b.txt\"", flat);
    }

    [Fact]
    public async Task TokenMint_RequiresExactBearerSecret()
    {
        using var client = _factory.CreateClient();
        var unauthorized = await client.PostAsync("/api/tenants/fluid/token-mint",
            JsonBody("""{"documentId":"mint-doc"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/tenants/fluid/token-mint")
        {
            Content = JsonBody("""{"documentId":"mint-doc"}"""),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TestServerFixture.MintSecret}");
        var minted = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        using var json = JsonDocument.Parse(await minted.Content.ReadAsStringAsync());
        Assert.Equal(3600, json.RootElement.GetProperty("expiresIn").GetInt32());
        Assert.Equal("floodgate-token-mint", json.RootElement.GetProperty("user").GetProperty("id").GetString());
    }

    [Fact]
    public async Task RestLess_RewritesMethodHeadersAndBody()
    {
        // Unit-level: TestServer normalizes the Content-Type header (inserting
        // a space after ';'), which defeats an end-to-end assertion — the real
        // Kestrel path is covered by the conformance suites.
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.ContentType = "application/x-www-form-urlencoded;restless";
        var form = $"method=PATCH&header={Uri.EscapeDataString("Authorization: Bearer tok")}" +
                   $"&body={Uri.EscapeDataString("""{"id":"restless-doc"}""")}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(form));

        var ran = false;
        var app = new Microsoft.AspNetCore.Builder.ApplicationBuilder(_factory.Services);
        app.UseRestLess();
        app.Use(_ => _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });
        await app.Build()(context);

        Assert.True(ran, "pipeline continued");
        Assert.Equal("PATCH", context.Request.Method);
        Assert.Equal("Bearer tok", context.Request.Headers.Authorization.ToString());
        Assert.Equal("application/json", context.Request.ContentType);
        using var reader = new StreamReader(context.Request.Body);
        Assert.Equal("""{"id":"restless-doc"}""", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task UnknownRoute_Is404WithEmptyBody()
    {
        using var client = Client();
        var response = await client.GetAsync("/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownGitKind_Is404()
    {
        using var client = Client(TestServerFixture.MintJwt("kind-doc"));
        var response = await client.PostAsync("/repos/fluid/git/tags", JsonBody("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task BasicScheme_Base64UserJwt_IsAccepted()
    {
        var token = TestServerFixture.MintJwt("basic-doc");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"user:{token}"));
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/documents/fluid")
        {
            Content = JsonBody("""{"id":"basic-doc"}"""),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Basic {basic}");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
