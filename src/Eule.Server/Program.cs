using System.Reflection;
using System.Threading.RateLimiting;
using Eule.Server;
using Eule.Server.Agents;
using Eule.Server.Auth;
using Eule.Server.Data;
using Eule.Server.Notes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

// Admin commands ("user add …") have their own flags (--name, --password-stdin) that the configuration parser would
// misread, e.g. swallowing the next word as a value. In that mode only "--key=value" settings reach the configuration.
bool adminCommand = args.Length > 0 && args[0] == "user";
var builder = WebApplication.CreateBuilder(adminCommand ? args.Where(a => a.StartsWith("--") && a.Contains('=')).ToArray() : args);

// ---- configuration (read lazily through DI, so test hosts and environment variables can override it)
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<EuleDbContext>((sp, o) => Database.Configure(o, sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();
// Dictation agent: GPT (one system-wide key, OpenAI__ApiKey) when configured, else the rule-based parser.
builder.Services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("OpenAI").Get<OpenAiOptions>() ?? new OpenAiOptions());
builder.Services.AddHttpClient(OpenAiDictationAgent.HttpClientName);
builder.Services.AddScoped<SaveNoteAgent>();
builder.Services.AddScoped<OpenAiDictationAgent>();
builder.Services.AddScoped<IDictationAgent>(sp => sp.GetRequiredService<OpenAiOptions>().IsConfigured
    ? sp.GetRequiredService<OpenAiDictationAgent>()
    : sp.GetRequiredService<SaveNoteAgent>());

// Admin commands run before anything web-related: `dotnet Eule.Server.dll user add ...`
if (adminCommand)
{
    using var cliHost = builder.Build();
    await Database.PrepareAsync(cliHost.Services);
    return await UserCli.RunAsync(cliHost.Services, args[1..]);
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<JwtOptions, TimeProvider>((o, jwt, time) =>
    {
        var skew = TimeSpan.FromSeconds(30);
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = jwt.Key(),
            ValidateIssuerSigningKey = true,
            ClockSkew = skew,
            // Check expiry with the same clock that issued the token (TokenService uses TimeProvider too).
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return (notBefore == null || notBefore.Value <= now + skew) && (expires == null || expires.Value > now - skew);
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();
// A malformed request body is the client's mistake: answer 400 in every environment, never 500.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = false);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in: 10 tries a minute per address (RateLimit:LoginPerMinute), against password guessing.
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:LoginPerMinute", 10),
            Window = TimeSpan.FromMinutes(1),
        }));
    // Everything else: 120 requests a minute per user.
    o.AddPolicy("user", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

// Coolify puts Traefik in front: trust its X-Forwarded-* headers for the real client address and https.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();
app.Services.GetRequiredService<JwtOptions>().Validate(); // refuse to start without a real signing key
await Database.PrepareAsync(app.Services);

app.UseForwardedHeaders();
app.UseExceptionHandler();
// The web app (wwwroot) keeps tokens in the browser: allow only our own scripts, no framing.
app.Use((ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["X-Frame-Options"] = "DENY";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    return next();
});
app.UseStaticFiles(new StaticFileOptions
{
    // Asset links carry a content hash (see WebApp), so a changed file always has a new address.
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
app.MapGet("/", (HttpContext ctx, IWebHostEnvironment env) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return WebApp.Page(env);
});
app.MapGet("/health", async (EuleDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "ok", version })
        : Results.Problem(statusCode: 503, title: "database_unreachable"));
app.MapAuth();
app.MapNotes();

app.Run();
return 0;

public partial class Program;
