using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Saneia.Api.Monitoring;

namespace Saneia.Tests;

public sealed class MonitoringIntegrationTests
{
    [Fact]
    public async Task RejectsMissingExpiredTamperedAndWrongRoleTokens()
    {
        await using var server = new ApiFactory();
        using var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sensors")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.Token("OPERATOR", expired: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sensors")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.Token("OPERATOR", wrongKey: true));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sensors")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", server.Token("RESIDENT"));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sensors")).StatusCode);
    }

    [Fact]
    public async Task ReadsJavaCatalogAndReturnsOfflineForNewSensor()
    {
        await using var server = new ApiFactory();
        using var client = server.Operator();
        using var response = await client.GetAsync("/api/sensors?page=0&size=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2", response.Headers.GetValues("X-Total-Pages").Single());
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<Sensor[]>())!);
        Assert.Equal("21", item.Id);
        Assert.Equal("offline", item.Status);
        Assert.Null(item.Volume);
        Assert.Null(item.LastReadingAt);
        Assert.Empty(item.Readings);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/sensors/99")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/sensors?size=0")).StatusCode);
    }

    [Fact]
    public async Task SavesOneReadingOnReplayAndClosesOneAlarmAfterRecovery()
    {
        await using var server = new ApiFactory();
        using var client = server.Operator();
        const string route = "/api/sensors/21/readings";
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var sample = new ReadingInput("S21", "a", at, 80);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, sample)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(route, sample)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, sample with { Volume = 81 })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, sample with
            { SampleId = "b", At = at.AddSeconds(10), Volume = 85 })).StatusCode);
        var alarm = await client.GetFromJsonAsync<Sensor>("/api/sensors/21");
        Assert.Equal("alarm", alarm!.Status);
        Assert.Equal(new decimal[] { 80, 85 }, alarm.Readings);
        Assert.Null(Assert.Single(alarm.Events).EndedAt);
        Assert.Equal(1, alarm.AlarmsSixMonths);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, sample with
            { SampleId = "c", At = at.AddSeconds(20), Volume = 40 })).StatusCode);
        Assert.Equal("alarm", (await client.GetFromJsonAsync<Sensor>("/api/sensors/21"))!.Status);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, sample with
            { SampleId = "d", At = at.AddSeconds(30), Volume = 30 })).StatusCode);
        var recovered = await client.GetFromJsonAsync<Sensor>("/api/sensors/21");
        Assert.Equal("normal", recovered!.Status);
        Assert.Equal(at.AddSeconds(30), Assert.Single(recovered.Events).EndedAt);
        Assert.Equal(4, recovered.Samples.Length);
    }

    [Fact]
    public async Task ConcurrentReplaysPersistOnlyOneSample()
    {
        await using var server = new ApiFactory();
        using var client = server.Operator();
        var sample = new ReadingInput("S21", "same", DateTimeOffset.UtcNow, 83);
        var responses = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => client.PostAsJsonAsync("/api/sensors/21/readings", sample)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(5, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
        var sensor = await client.GetFromJsonAsync<Sensor>("/api/sensors/21");
        Assert.Single(sensor!.Samples);
        Assert.Single(sensor.Events);
    }

    [Fact]
    public async Task RejectsInvalidMeasurementAndOutOfOrderWrite()
    {
        await using var server = new ApiFactory();
        using var client = server.Operator();
        const string route = "/api/sensors/21/readings";
        var sample = new ReadingInput("S21", "s1", DateTimeOffset.UtcNow.AddSeconds(-10), 30);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, sample with { Volume = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, sample with { DeviceId = "other" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(route, new { sample.DeviceId, sample.SampleId, sample.At })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(route, sample)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(route, sample with { SampleId = "old", At = sample.At.AddSeconds(-1) })).StatusCode);
    }

    [Fact]
    public async Task RateLimitReturns429AndRetryAfter()
    {
        await using var server = new ApiFactory(limit: 1);
        using var client = server.Operator();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sensors")).StatusCode);
        using var response = await client.GetAsync("/api/sensors");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task HealthDistinguishesProcessFromUnavailableJava()
    {
        await using var server = new ApiFactory(javaDown: true);
        using var client = server.Operator();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        var report = await readiness.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", report.GetProperty("checks").GetProperty("telemetry_db").GetString());
        Assert.Equal("Unhealthy", report.GetProperty("checks").GetProperty("java_catalog").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/sensors")).StatusCode);
    }

    [Fact]
    public async Task OpenApiDocumentsAuthenticationAndReplayResponses()
    {
        await using var server = new ApiFactory();
        using var client = server.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var routes = document.GetProperty("paths");
        var responses = routes.GetProperty("/api/sensors/{id}/readings").GetProperty("post").GetProperty("responses");
        foreach (var status in new[] { "200", "201", "400", "401", "403", "404", "409", "429", "503" })
            Assert.True(responses.TryGetProperty(status, out _), $"Missing OpenAPI response {status}");
        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("Bearer", out _));
    }

    private sealed class ApiFactory(int limit = 120, bool javaDown = false) : WebApplicationFactory<Program>
    {
        private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
        private readonly string path = Path.Combine(Path.GetTempPath(), $"saneia-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={path};Pooling=False");
            builder.UseSetting("RateLimit:PermitsPerMinute", limit.ToString());
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(secret));
                services.AddHttpClient<JavaCatalog>().ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler(javaDown));
                services.AddHttpClient("java-health").ConfigurePrimaryHttpMessageHandler(() => new CatalogHandler(javaDown));
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            File.Delete(path);
        }

        public HttpClient Operator()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", Token("OPERATOR"));
            return client;
        }

        public string Token(string role, bool expired = false, bool wrongKey = false)
        {
            var now = DateTime.UtcNow;
            var signature = new SigningCredentials(
                new SymmetricSecurityKey(wrongKey ? RandomNumberGenerator.GetBytes(32) : secret), SecurityAlgorithms.HmacSha256);
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                issuer: "saneia-api", claims: [new Claim("sub", "21"), new Claim("role", role)],
                notBefore: now.AddMinutes(-10), expires: expired ? now.AddMinutes(-1) : now.AddMinutes(5),
                signingCredentials: signature));
        }
    }

    private sealed class CatalogHandler(bool down) : HttpMessageHandler
    {
        private static readonly CatalogSensor Fixture =
            new("21", "S21", "Reservatório", "Rua A", "Centro", new(-23.5, -46.6), 100, 80, "tank", 150, 5, null);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (down) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (path.EndsWith("/actuator/health"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            HttpResponseMessage response = path switch
            {
                "/api/sensors" => new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Fixture }) },
                "/api/sensors/21" => new(HttpStatusCode.OK) { Content = JsonContent.Create(Fixture) },
                _ => new(HttpStatusCode.NotFound)
            };
            response.Headers.Add("X-Total-Pages", "2");
            return Task.FromResult(response);
        }
    }
}
