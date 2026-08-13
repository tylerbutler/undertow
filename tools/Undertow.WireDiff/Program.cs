using System.Text;
using Undertow.WireDiff;

// Phase-0 wire recorder: drives a running Fluid server (Gleam Floodgate today,
// Undertow later) through scripted scenarios over both wire protocols plus REST
// and writes raw frame transcripts. Later phases extend this into a shadow
// differ by recording two servers and diffing the transcripts.
//
// Usage:
//   dotnet run --project tools/Undertow.WireDiff -- record \
//     --http http://localhost:3000 --out tests/fixtures/wire \
//     --tenant fluid --jwt-secret dev-tenant-secret-key \
//     --mint-secret dev-token-mint-secret [--label floodgate@abc123]

var opts = new Dictionary<string, string>();
for (var i = 1; i < args.Length - 1; i += 2)
    opts[args[i].TrimStart('-')] = args[i + 1];

if (args.Length > 0 && args[0] == "diff")
    return WireDiffer.Run(opts.GetValueOrDefault("left", ""), opts.GetValueOrDefault("right", ""));

if (args.Length == 0 || args[0] != "record")
{
    Console.Error.WriteLine("usage: wirediff record --http <url> --out <dir> [--tenant fluid] " +
                            "[--jwt-secret s] [--mint-secret s] [--label l]");
    Console.Error.WriteLine("       wirediff diff --left <dir> --right <dir>");
    return 2;
}

var httpBase = opts.GetValueOrDefault("http", "http://localhost:3000").TrimEnd('/');
var outDir = opts.GetValueOrDefault("out", "fixtures");
var tenant = opts.GetValueOrDefault("tenant", "fluid");
var jwtSecret = opts.GetValueOrDefault("jwt-secret", "dev-tenant-secret-key");
var mintSecret = opts.GetValueOrDefault("mint-secret", "dev-token-mint-secret");
var label = opts.GetValueOrDefault("label", "unlabeled");
var wsBase = httpBase.Replace("http://", "ws://").Replace("https://", "wss://");
// Comma-separated scenario name prefixes to record; empty = all.
var only = opts.GetValueOrDefault("only", "");
bool Enabled(string name) =>
    only.Length == 0 || only.Split(',').Any(p => name.StartsWith(p, StringComparison.Ordinal));

Directory.CreateDirectory(outDir);
File.WriteAllText(Path.Combine(outDir, "SOURCE.txt"), $"{label}\ncaptured: {DateTime.UtcNow:O}\nfrom: {httpBase}\n");

var http = new HttpClient { BaseAddress = new Uri(httpBase) };
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var allScopes = new[] { "doc:read", "doc:write", "summary:read", "summary:write" };
var readOnlyScopes = new[] { "doc:read" };
var ct = CancellationToken.None;
var settle = TimeSpan.FromMilliseconds(700);
var wait = TimeSpan.FromSeconds(5);

async Task Save(string name, Transcript log)
{
    var path = Path.Combine(outDir, name + ".txt");
    await File.WriteAllTextAsync(path, string.Join("\n", log.Snapshot()) + "\n");
    Console.WriteLine($"wrote {path}");
}

async Task RecordHttp(Transcript log, HttpRequestMessage req, string? body = null)
{
    if (body is not null)
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
    log.Add($"> {req.Method} {req.RequestUri}{(body is null ? "" : " " + body)}");
    var resp = await http.SendAsync(req);
    var respBody = await resp.Content.ReadAsStringAsync();
    log.Add($"< HTTP {(int)resp.StatusCode} {respBody}");
}

string ClientJson(string mode, string userId, string userName) =>
    $"{{\"mode\":\"{mode}\",\"details\":{{\"capabilities\":{{\"interactive\":true}},\"environment\":\"wirediff\"}}," +
    $"\"permission\":[],\"scopes\":[{string.Join(",", allScopes.Select(s => $"\"{s}\""))}]," +
    $"\"user\":{{\"id\":\"{userId}\",\"name\":{System.Text.Json.JsonSerializer.Serialize(userName)}}}}}";

string ConnectPayload(string doc, string token, string mode, string clientJson) =>
    $"{{\"tenantId\":\"{tenant}\",\"id\":\"{doc}\",\"token\":\"{token}\",\"mode\":\"{mode}\"," +
    $"\"client\":{clientJson},\"versions\":[\"^0.4.0\",\"^0.3.0\",\"^0.2.0\",\"^0.1.0\"],\"nonce\":\"wirediff-nonce\"}}";

static string ExtractLast(Transcript log, string frameMarker, string key, string label = "")
{
    var haystack = log.Snapshot().LastOrDefault(l =>
        l.StartsWith($"<{label} ", StringComparison.Ordinal) &&
        l.Contains(frameMarker, StringComparison.Ordinal));
    if (haystack is null)
        return "";
    var marker = $"\"{key}\":\"";
    var idx = haystack.IndexOf(marker, StringComparison.Ordinal);
    if (idx < 0)
        return "";
    var start = idx + marker.Length;
    var end = haystack.IndexOf('"', start);
    return end < 0 ? "" : haystack[start..end];
}

// Opens an Engine.IO/Socket.IO socket through the handshake + namespace connect.
async Task<SocketRecorder> OpenSocketIo(Transcript log, string label = "")
{
    var rec = new SocketRecorder(log, label);
    await rec.ConnectAsync(new Uri($"{wsBase}/socket.io/?EIO=4&transport=websocket"), ct);
    await rec.WaitForAsync("\"sid\"", wait); // 0{...} open
    await rec.SendAsync("40", ct);
    await rec.WaitForAsync("40", wait); // 40{"sid":...}
    return rec;
}

// ── REST ────────────────────────────────────────────────────────────────────
if (Enabled("rest-basics"))
{
    var log = new Transcript();
    log.Add("# rest: health, token-mint, create document, session, deltas");
    await RecordHttp(log, new HttpRequestMessage(HttpMethod.Get, "/health"));
    await RecordHttp(log, new HttpRequestMessage(HttpMethod.Head, "/health"));

    var mintReq = new HttpRequestMessage(HttpMethod.Post, $"/api/tenants/{tenant}/token-mint");
    mintReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {mintSecret}");
    await RecordHttp(log, mintReq, "{\"documentId\":\"wire-rest-doc\"}");

    var token = Jwt.Mint(tenant, "wire-rest-doc", allScopes, "wirediff-user", jwtSecret, now, 3600);
    var create = new HttpRequestMessage(HttpMethod.Post, $"/documents/{tenant}");
    create.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    await RecordHttp(log, create, "{\"id\":\"wire-rest-doc\"}");

    var session = new HttpRequestMessage(HttpMethod.Get, $"/documents/{tenant}/session/wire-rest-doc");
    session.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    await RecordHttp(log, session);

    var deltas = new HttpRequestMessage(HttpMethod.Get, $"/deltas/{tenant}/wire-rest-doc");
    deltas.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
    await RecordHttp(log, deltas);

    var noAuth = new HttpRequestMessage(HttpMethod.Get, $"/deltas/{tenant}/wire-rest-doc");
    await RecordHttp(log, noAuth);
    await Save("rest-basics", log);
}

// ── Socket.IO: handshake + write connect + submitOp + disconnect ────────────
if (Enabled("socketio-write-connect-op"))
{
    var log = new Transcript();
    log.Add("# socketio: handshake, connect_document (write), submitOp, leave");
    var doc = "wire-sio-write";
    var token = Jwt.Mint(tenant, doc, allScopes, "wirediff-user", jwtSecret, now, 3600, "WireDiff User");
    await using var rec = await OpenSocketIo(log);
    var client = ClientJson("write", "wirediff-user", "WireDiff User");
    await rec.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, token, "write", client)}]", ct);
    await rec.WaitForAsync("connect_document_success", wait);
    await rec.SettleAsync(settle);

    var clientId = ExtractLast(log, "connect_document_success", "clientId");
    log.Add($"# extracted clientId={clientId}");
    var op = "{\"clientSequenceNumber\":1,\"referenceSequenceNumber\":1,\"type\":\"op\"," +
             "\"contents\":\"{\\\"key\\\":\\\"value\\\"}\",\"metadata\":{\"batch\":true},\"traces\":[]}";
    await rec.SendAsync($"42[\"submitOp\",\"{clientId}\",[[{op}]]]", ct);
    await rec.SettleAsync(TimeSpan.FromSeconds(1));
    await Save("socketio-write-connect-op", log);
}

// ── Socket.IO: read-mode connect + submitOp → nack ──────────────────────────
if (Enabled("socketio-read-nack"))
{
    var log = new Transcript();
    log.Add("# socketio: read-mode connect, submitOp must nack");
    var doc = "wire-sio-read";
    var token = Jwt.Mint(tenant, doc, readOnlyScopes, "wirediff-reader", jwtSecret, now, 3600);
    await using var rec = await OpenSocketIo(log);
    var client = ClientJson("read", "wirediff-reader", "Reader");
    await rec.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, token, "read", client)}]", ct);
    await rec.WaitForAsync("connect_document_success", wait);
    await rec.SettleAsync(settle);
    var clientId = ExtractLast(log, "connect_document_success", "clientId");
    await rec.SendAsync($"42[\"submitOp\",\"{clientId}\",[[{{\"clientSequenceNumber\":1," +
                        "\"referenceSequenceNumber\":0,\"type\":\"op\",\"contents\":\"{}\"}]]]", ct);
    await rec.SettleAsync(TimeSpan.FromSeconds(1));
    await Save("socketio-read-nack", log);
}

// ── Socket.IO: expired token + bad signature ────────────────────────────────
if (Enabled("socketio-auth-failures"))
{
    var log = new Transcript();
    log.Add("# socketio: expired token, then bad signature");
    var doc = "wire-sio-authfail";
    var expired = Jwt.Mint(tenant, doc, allScopes, "wirediff-user", jwtSecret, now - 7200, 3600);
    await using (var rec = await OpenSocketIo(log))
    {
        await rec.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, expired, "write", ClientJson("write", "u", "U"))}]", ct);
        await rec.SettleAsync(TimeSpan.FromSeconds(1));
    }

    var badSig = Jwt.Mint(tenant, doc, allScopes, "wirediff-user", "wrong-secret", now, 3600);
    await using (var rec2 = await OpenSocketIo(log))
    {
        await rec2.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, badSig, "write", ClientJson("write", "u", "U"))}]", ct);
        await rec2.SettleAsync(TimeSpan.FromSeconds(1));
    }

    await Save("socketio-auth-failures", log);
}

// ── Socket.IO: unicode escaping probe ───────────────────────────────────────
if (Enabled("socketio-unicode"))
{
    var log = new Transcript();
    log.Add("# socketio: non-ASCII + HTML-sensitive chars in user.name");
    var doc = "wire-sio-unicode";
    var name = "Ünïcödé <&> 日本語";
    var token = Jwt.Mint(tenant, doc, allScopes, "wirediff-unicode", jwtSecret, now, 3600, name);
    await using var rec = await OpenSocketIo(log);
    await rec.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, token, "write", ClientJson("write", "wirediff-unicode", name))}]", ct);
    await rec.WaitForAsync("connect_document_success", wait);
    await rec.SettleAsync(settle);
    await Save("socketio-unicode", log);
}

// ── Socket.IO: two clients, signals (broadcast + targeted), leave op ────────
if (Enabled("signals-broadcast-targeted-leave"))
{
    var log = new Transcript();
    log.Add("# two Socket.IO write clients A/B + one Phoenix client P;");
    log.Add("# A broadcasts a legacy signal; P sends a v2 contentBatches signal targeting B only;");
    log.Add("# then B disconnects and A observes the leave op");
    var doc = "wire-sio-signals";
    var topic = $"document:{tenant}:{doc}";
    var tokenA = Jwt.Mint(tenant, doc, allScopes, "user-a", jwtSecret, now, 3600, "User A");
    var tokenB = Jwt.Mint(tenant, doc, allScopes, "user-b", jwtSecret, now, 3600, "User B");
    var tokenP = Jwt.Mint(tenant, doc, allScopes, "user-p", jwtSecret, now, 3600, "User P");
    await using var a = await OpenSocketIo(log, "A");
    await a.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, tokenA, "write", ClientJson("write", "user-a", "User A"))}]", ct);
    await a.WaitForAsync("connect_document_success", wait);
    var clientA = ExtractLast(log, "connect_document_success", "clientId", "A");

    await using var b = await OpenSocketIo(log, "B");
    await b.SendAsync($"42[\"connect_document\",{ConnectPayload(doc, tokenB, "write", ClientJson("write", "user-b", "User B"))}]", ct);
    await b.WaitForAsync("connect_document_success", wait);
    var clientB = ExtractLast(log, "connect_document_success", "clientId", "B");

    await using var p = new SocketRecorder(log, "P");
    await p.ConnectAsync(new Uri($"{wsBase}/socket/websocket?vsn=2.0.0"), ct);
    await p.SendAsync($"[\"1\",\"1\",\"{topic}\",\"phx_join\",{{\"token\":\"{tokenP}\"}}]", ct);
    await p.WaitForAsync("phx_reply", wait);
    await p.SendAsync($"[\"1\",\"2\",\"{topic}\",\"connect_document\",{ConnectPayload(doc, tokenP, "write", ClientJson("write", "user-p", "User P"))}]", ct);
    await p.WaitForAsync("connect_document_success", wait);
    var clientP = ExtractLast(log, "connect_document_success", "clientId", "P");
    log.Add($"# clientA={clientA} clientB={clientB} clientP={clientP}");
    await a.SettleAsync(settle);
    await b.SettleAsync(settle);

    log.Add("# legacy broadcast signal from A (list of {content} objects): everyone but A receives it");
    await a.SendAsync($"42[\"submitSignal\",\"{clientA}\",[{{\"content\":\"{{\\\"type\\\":\\\"broadcast-probe\\\",\\\"content\\\":1}}\"}}]]", ct);
    await b.WaitForAsync("broadcast-probe", wait);
    await a.SettleAsync(settle);

    log.Add($"# v2 targeted signal from P naming B ({clientB}): only B receives it");
    await p.SendAsync($"[\"1\",\"3\",\"{topic}\",\"submitSignal\",{{\"clientId\":\"{clientP}\",\"contentBatches\":" +
                      $"[{{\"targetClientId\":\"{clientB}\",\"content\":{{\"type\":\"targeted-probe\",\"content\":2}}}}]}}]", ct);
    await b.WaitForAsync("targeted-probe", wait);
    await a.SettleAsync(settle);

    log.Add("# closing B; A should observe leave");
    await b.DisposeAsync();
    await a.SettleAsync(TimeSpan.FromSeconds(1));
    await Save("signals-broadcast-targeted-leave", log);
}

// ── Phoenix: two-phase join + connect + submitOp + heartbeat + leave ────────
if (Enabled("phoenix-write-connect-op"))
{
    var log = new Transcript();
    log.Add("# phoenix: vsn=2.0.0, phx_join, connect_document push, submitOp, heartbeat, phx_leave");
    var doc = "wire-phx-write";
    var topic = $"document:{tenant}:{doc}";
    var token = Jwt.Mint(tenant, doc, allScopes, "wirediff-phx", jwtSecret, now, 3600, "Phx User");
    await using var rec = new SocketRecorder(log);
    await rec.ConnectAsync(new Uri($"{wsBase}/socket/websocket?vsn=2.0.0"), ct);
    await rec.SendAsync($"[\"1\",\"1\",\"{topic}\",\"phx_join\",{{\"token\":\"{token}\"}}]", ct);
    await rec.WaitForAsync("phx_reply", wait);
    var client = ClientJson("write", "wirediff-phx", "Phx User");
    await rec.SendAsync($"[\"1\",\"2\",\"{topic}\",\"connect_document\",{ConnectPayload(doc, token, "write", client)}]", ct);
    await rec.WaitForAsync("connect_document_success", wait);
    await rec.SettleAsync(settle);
    var clientId = ExtractLast(log, "connect_document_success", "clientId");
    log.Add($"# extracted clientId={clientId}");
    await rec.SendAsync($"[\"1\",\"3\",\"{topic}\",\"submitOp\",{{\"clientId\":\"{clientId}\",\"messageBatches\":" +
                        "[[{\"clientSequenceNumber\":1,\"referenceSequenceNumber\":1,\"type\":\"op\"," +
                        "\"contents\":\"{\\\"phx\\\":true}\"}]]}]", ct);
    await rec.SettleAsync(TimeSpan.FromSeconds(1));
    await rec.SendAsync("[null,\"4\",\"phoenix\",\"heartbeat\",{}]", ct);
    await rec.WaitForAsync("phx_reply", wait);
    await rec.SendAsync($"[\"1\",\"5\",\"{topic}\",\"phx_leave\",{{}}]", ct);
    await rec.SettleAsync(TimeSpan.FromSeconds(1));
    await Save("phoenix-write-connect-op", log);
}

// ── Phoenix: bad vsn must be rejected before upgrade ────────────────────────
if (Enabled("phoenix-bad-vsn"))
{
    var log = new Transcript();
    log.Add("# phoenix: vsn=1.0.0 must be rejected before upgrade");
    try
    {
        await using var rec = new SocketRecorder(log);
        await rec.ConnectAsync(new Uri($"{wsBase}/socket/websocket?vsn=1.0.0"), ct);
        log.Add("# UNEXPECTED: upgrade accepted");
    }
    catch (System.Net.WebSockets.WebSocketException e)
    {
        log.Add($"# upgrade rejected: {e.Message}");
    }

    await Save("phoenix-bad-vsn", log);
}

Console.WriteLine("done");
return 0;
