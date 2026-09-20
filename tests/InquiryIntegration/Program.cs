using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

// Run from the API directory. Creates and drops only its own disposable localhost database.
var configuration = new ConfigurationBuilder().AddUserSecrets<ApplicationDbContext>().AddEnvironmentVariables().Build();
var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Default")
    ?? throw new Exception("Configure the local development database first."));
if (connection.Host is not ("localhost" or "127.0.0.1" or "::1")) throw new Exception("Tests require localhost PostgreSQL.");
var database = "hsl_inquiry_test_" + Guid.NewGuid().ToString("N");
connection.Database = "postgres";
await using var management = new NpgsqlConnection(connection.ConnectionString);
await management.OpenAsync();
await new NpgsqlCommand($"CREATE DATABASE {database}", management).ExecuteNonQueryAsync();
connection.Database = database;
var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection.ConnectionString).Options;
Process? process = null;
try
{
    await using var db = new ApplicationDbContext(options);
    await db.Database.MigrateAsync();
    var adminRole = new IdentityRole("Admin");
    var clientRole = new IdentityRole("Client");
    var admin = new ApplicationUser { UserName = "admin@example.test", Email = "admin@example.test", SecurityStamp = Guid.NewGuid().ToString() };
    var client = new ApplicationUser { UserName = "client@example.test", Email = "client@example.test", SecurityStamp = Guid.NewGuid().ToString() };
    db.Roles.AddRange(adminRole, clientRole);
    db.Users.AddRange(admin, client);
    db.UserRoles.AddRange(new IdentityUserRole<string> { RoleId = adminRole.Id, UserId = admin.Id }, new IdentityUserRole<string> { RoleId = clientRole.Id, UserId = client.Id });
    await db.SaveChangesAsync();
    var key = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    var jwt = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Jwt:Key"] = key, ["Jwt:Issuer"] = "inquiry-tests", ["Jwt:Audience"] = "inquiry-tests"
    }).Build();
    var tokens = new JwtTokenService(jwt);
    var adminToken = tokens.CreateToken(admin, ["Admin"]).Token;
    var clientToken = tokens.CreateToken(client, ["Client"]).Token;
    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
    var api = Path.GetFullPath("bin/Release/net9.0/HendersonSoftwareLabsAPI.dll");
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    start.ArgumentList.Add(api); start.ArgumentList.Add("--urls"); start.ArgumentList.Add($"http://127.0.0.1:{port}");
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
    start.Environment["ConnectionStrings__Default"] = connection.ConnectionString;
    start.Environment["Jwt__Key"] = key;
    start.Environment["Jwt__Issuer"] = "inquiry-tests";
    start.Environment["Jwt__Audience"] = "inquiry-tests";
    start.Environment["Cors__AllowedOrigin"] = "http://localhost:5173";
    process = Process.Start(start)!;
    process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
    process.BeginOutputReadLine(); process.BeginErrorReadLine();
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(15) };
    for (var i = 0; ; i++)
    {
        try { await http.GetAsync("/api/admin/inquiries"); break; }
        catch (HttpRequestException) when (i < 60) { await Task.Delay(250); }
    }
    var ip = 0;
    async Task<HttpResponseMessage> Send(string method, string path, object? body = null, string? token = null, string? address = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("X-Forwarded-For", address ?? $"192.0.2.{++ip}");
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request);
    }
    static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
    object Payload(Guid? id = null, string name = " Test Visitor ", string message = " Test inquiry ", string email = "visitor@example.test", string website = "") => new { submissionId = id ?? Guid.NewGuid(), name, message, email, website };
    var submission = Guid.NewGuid();
    var response = await Send("POST", "/api/contact", Payload(submission));
    Check(response.IsSuccessStatusCode, "public submission succeeds");
    Check(response.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://localhost:5173", "submission CORS");
    Check(await db.Inquiries.CountAsync() == 1 && (await db.Inquiries.SingleAsync()).Name == "Test Visitor", "persisted and trimmed");
    await Send("POST", "/api/contact", Payload(submission));
    Check(await db.Inquiries.CountAsync() == 1, "retry is idempotent");
    var concurrent = Guid.NewGuid();
    await Task.WhenAll(Send("POST", "/api/contact", Payload(concurrent)), Send("POST", "/api/contact", Payload(concurrent)));
    Check(await db.Inquiries.CountAsync(x => x.SubmissionId == concurrent) == 1, "concurrent retries create one record");
    foreach (var invalid in new[] { Payload(name: " "), Payload(email: "invalid"), Payload(message: new string('a', 5001)), Payload(Guid.Empty), Payload(website: "spam") })
        Check((await Send("POST", "/api/contact", invalid)).StatusCode == HttpStatusCode.BadRequest, "invalid or honeypot request rejected");
    Check(await db.Inquiries.CountAsync() == 2, "rejected requests not persisted");
    var oversized = await Send("POST", "/api/contact", Payload(message: new string('a', 40000)));
    Check(oversized.StatusCode is HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.BadRequest, $"oversized request rejected ({(int)oversized.StatusCode})");
    for (var i = 0; i < 3; i++) await Send("POST", "/api/contact", Payload(submission), address: "198.51.100.1");
    var limited = await Send("POST", "/api/contact", Payload(), address: "198.51.100.1");
    Check(limited.StatusCode == HttpStatusCode.TooManyRequests && limited.Headers.Contains("Access-Control-Allow-Origin"), "throttling and CORS");
    var first = (await db.Inquiries.AsNoTracking().FirstAsync()).Id;
    foreach (var token in new string?[] { null, clientToken })
    {
        var expected = token == null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
        Check((await Send("GET", "/api/admin/inquiries", token: token)).StatusCode == expected, "inbox authorization");
        Check((await Send("GET", $"/api/admin/inquiries/{first}", token: token)).StatusCode == expected, "detail authorization");
        Check((await Send("PATCH", $"/api/admin/inquiries/{first}/status", new { status = "Archived" }, token)).StatusCode == expected, "status authorization");
    }
    for (var i = 0; i < 26; i++) db.Inquiries.Add(new Inquiry { SubmissionId = Guid.NewGuid(), Name = "Pagination test", Email = "test@example.test", Message = "<script>plain text</script>", CreatedAt = DateTime.UtcNow.AddMinutes(i), StatusUpdatedAt = DateTime.UtcNow });
    await db.SaveChangesAsync();
    var list = await (await Send("GET", "/api/admin/inquiries", token: adminToken)).Content.ReadFromJsonAsync<JsonElement>();
    Check(list.GetProperty("items").GetArrayLength() == 25 && list.GetProperty("newCount").GetInt32() == 28, "pagination and new count");
    var secondPage = await (await Send("GET", "/api/admin/inquiries?page=2", token: adminToken)).Content.ReadFromJsonAsync<JsonElement>();
    Check(secondPage.GetProperty("items").GetArrayLength() == 3, "second page");
    foreach (var status in new[] { "Contacted", "Archived", "New" })
    {
        Check((await Send("PATCH", $"/api/admin/inquiries/{first}/status", new { status }, adminToken)).IsSuccessStatusCode, "status " + status);
        var detail = await (await Send("GET", $"/api/admin/inquiries/{first}", token: adminToken)).Content.ReadFromJsonAsync<JsonElement>();
        Check(detail.GetProperty("status").GetString() == status, "status persisted");
    }
    Check((await Send("PATCH", $"/api/admin/inquiries/{first}/status", new { status = "7" }, adminToken)).StatusCode == HttpStatusCode.BadRequest, "invalid status rejected");
    await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Inquiries\" RENAME TO \"InquiriesOffline\"");
    var failed = await Send("POST", "/api/contact", Payload());
    Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable && failed.Headers.Contains("Access-Control-Allow-Origin"), "database failure and CORS");
    Console.WriteLine("All inquiry integration checks passed.");
}
finally
{
    if (process is { HasExited: false }) { process.Kill(true); await process.WaitForExitAsync(); }
    NpgsqlConnection.ClearAllPools();
    await new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", management).ExecuteNonQueryAsync();
}
