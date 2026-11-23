using System.Text.Json;
using System.Text.Json.Serialization;
using Claims.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "data", "session-keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "claims-demo-session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.ExpireTimeSpan = TimeSpan.FromHours(2);
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ClaimsStore>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Kafka:BootstrapServers"]))
    builder.Services.AddHostedService<KafkaOutbox>();

var app = builder.Build();
app.Services.GetRequiredService<ClaimsStore>().Initialize();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api");
api.MapGet("/health", () => Results.Ok(new { status = "ok" }));
api.MapPost("/session", async (SignInRequest request, HttpContext context) =>
{
    var user = DemoAccounts.Authenticate(request.Account, request.Password);
    if (user is null) return Results.Problem("Check the demo account and password.", statusCode: 401);

    await context.SignInAsync(user.ToPrincipal());
    return Results.Ok(user);
});
api.MapGet("/session", (HttpContext context) => Results.Ok(DemoAccounts.FromPrincipal(context.User)))
    .RequireAuthorization();
api.MapDelete("/session", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.NoContent();
}).RequireAuthorization();
api.MapClaimEndpoints();

app.Run();
