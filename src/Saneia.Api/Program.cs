using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Saneia.Application.Monitoring;
using Saneia.Infrastructure;
using Saneia.Api.Monitoring;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
builder.Services.AddSaneiaInfrastructure(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TelemetryService>();

var jwtAlgorithm = config["Jwt:Algorithm"] ?? "HS256";
var issuer = config["Jwt:Issuer"] ?? "saneia-api";
var audience = config["Jwt:Audience"];
SecurityKey? key;
switch (jwtAlgorithm)
{
    case "HS256":
        var secret = config["Jwt:SharedSecret"];
        if (!string.IsNullOrEmpty(secret) && Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("O segredo HS256 precisa ter pelo menos 32 bytes.");
        key = string.IsNullOrEmpty(secret) ? null : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        break;
    case "RS256":
        var publicKeyPath = config["Jwt:PublicKeyPath"]
            ?? throw new InvalidOperationException("Configure o arquivo de chave pública em Jwt:PublicKeyPath.");
        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
        key = new RsaSecurityKey(rsa);
        if (string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("Configure Jwt:Audience para RS256.");
        break;
    default:
        throw new InvalidOperationException("Jwt:Algorithm deve ser HS256 ou RS256.");
}
if (key is null && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Configure a chave de verificação do JWT Java.");
if (key is null)
    key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.IncludeErrorDetails = false;
    options.TokenValidationParameters = new()
    {
        ValidateIssuerSigningKey = true, IssuerSigningKey = key, RequireSignedTokens = true,
        ValidAlgorithms = [jwtAlgorithm], ValidateIssuer = true, ValidIssuer = issuer,
        ValidateAudience = !string.IsNullOrWhiteSpace(audience), ValidAudience = audience,
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
        RoleClaimType = "role", NameClaimType = "sub"
    };
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = context =>
        {
            if (string.IsNullOrWhiteSpace(context.Principal?.FindFirstValue("sub")))
                context.Fail("JWT sem identificador de usuário.");
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorizationBuilder().AddPolicy("Operator", policy =>
    policy.RequireAuthenticatedUser().RequireRole("OPERATOR", "operator"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellation) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        await context.HttpContext.Response.WriteAsJsonAsync(
            new ApiError("Limite de requisições excedido. Tente novamente em instantes."), cancellation);
    };
    options.AddPolicy("monitoring", context =>
    {
        var user = context.User.FindFirstValue("sub");
        var partition = user is not null ? $"user:{user}" : $"ip:{context.Connection.RemoteIpAddress}";
        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(config.GetValue("RateLimit:PermitsPerMinute", 120), 1, 10000),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        });
    });
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
    policy.WithOrigins(origins).WithMethods("GET", "POST", "OPTIONS")
        .WithHeaders("Authorization", "Content-Type").WithExposedHeaders("X-Total-Pages", "Retry-After");
}));

builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Saneia — Monitoramento", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Token de operadora obtido em POST /api/auth/login no Java."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = []
    });
});
builder.Services.AddHealthChecks()
    .AddCheck("api", () => HealthCheckResult.Healthy(), tags: ["live"]);

var app = builder.Build();
if (await app.Services.PrepareTelemetryDatabase(config) is { } schema)
{
    Console.WriteLine(schema);
    return;
}

app.UseExceptionHandler(error => error.Run(async context =>
{
    context.Response.StatusCode = 500;
    await context.Response.WriteAsJsonAsync(new ApiError("Ocorreu uma falha interna."));
}));
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    await response.WriteAsJsonAsync(new ApiError(response.StatusCode switch
    {
        400 => "A requisição contém dados inválidos.",
        401 => "Sessão inválida ou expirada.",
        403 => "A operação exige o perfil de operadora.",
        404 => "Recurso não encontrado.",
        415 => "Formato de conteúdo não aceito.",
        _ => "Não foi possível concluir a requisição."
    }));
});
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapMonitoring();
app.MapHealthChecks("/health/live", HealthOptions("live"));
app.MapHealthChecks("/health/ready", HealthOptions("ready"));
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
app.Run();

static HealthCheckOptions HealthOptions(string tag) => new()
{
    Predicate = registration => registration.Tags.Contains(tag),
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString())
    })
};

public partial class Program;
