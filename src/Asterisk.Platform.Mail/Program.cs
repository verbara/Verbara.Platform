using System.Text.Json;
using Asterisk.Platform.Core.Email;
using Asterisk.Platform.Mail;
using Asterisk.Platform.Mail.Endpoints;
using Asterisk.Platform.Mail.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ── JSON serialization ──
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, MailJsonContext.Default);
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

// ── SMTP ──
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddSingleton<IEmailService, SmtpSender>();
builder.Services.AddSingleton<IEmailTemplateService, TemplateRenderer>();

// ── Microsoft Graph ──
builder.Services.AddSingleton<GraphMailboxService>();
builder.Services.AddHttpClient("MicrosoftAuth");
builder.Services.AddHttpClient("MicrosoftGraph", client =>
{
    client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
});

// ── PostgreSQL ──
var postgresConn = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(postgresConn))
{
    var dataSource = NpgsqlDataSource.Create(postgresConn);
    builder.Services.AddSingleton(dataSource);
}
builder.Services.AddSingleton<TokenStore>();

// ── Token refresh background service ──
builder.Services.AddHostedService<TokenRefreshService>();

// ── DataProtection for OAuth state cookies ──
builder.Services.AddDataProtection()
    .SetApplicationName("Asterisk.Platform.Mail");

// ── Authentication ──
var serviceKey = builder.Configuration["Services:ServiceKey"] ?? string.Empty;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;

        var jwtAuthority = builder.Configuration["Jwt:Authority"];
        var jwtAudience = builder.Configuration["Jwt:Audience"];
        var jwtIssuerSigningKey = builder.Configuration["Jwt:SigningKey"];

        if (!string.IsNullOrEmpty(jwtAuthority))
            options.Authority = jwtAuthority;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrEmpty(jwtAuthority),
            ValidateAudience = !string.IsNullOrEmpty(jwtAudience),
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = !string.IsNullOrEmpty(jwtIssuerSigningKey),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };

        if (!string.IsNullOrEmpty(jwtIssuerSigningKey))
        {
            var keyBytes = Convert.FromBase64String(jwtIssuerSigningKey);
            options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(keyBytes);
        }
    });

builder.Services.AddAuthorization();

// ── Health checks ──
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Middleware ──
app.UseAuthentication();
app.UseAuthorization();

// ── Health check ──
app.MapHealthChecks("/health").AllowAnonymous();

// ── Service-key-protected internal endpoints ──
var internalApi = app.MapGroup("/api/v1")
    .AddEndpointFilter(async (context, next) =>
    {
        var httpContext = context.HttpContext;
        var requestKey = httpContext.Request.Headers["X-Service-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(serviceKey) || requestKey != serviceKey)
            return Results.Unauthorized();
        return await next(context);
    });

internalApi.MapSendEndpoint();
internalApi.MapTemplateEndpoint();

// ── OAuth auth endpoints (mixed: anonymous + JWT) ──
var authGroup = app.MapGroup("/auth");
authGroup.MapMicrosoftAuthEndpoints();

// ── JWT-protected mailbox endpoints ──
var mailboxApi = app.MapGroup("/api/v1/mailbox")
    .RequireAuthorization();

mailboxApi.MapMailboxEndpoints();

app.Run();
