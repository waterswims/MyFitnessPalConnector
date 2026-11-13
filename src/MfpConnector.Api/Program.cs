using System.Net;
using Amazon.SimpleSystemsManagement;
using MfpConnector.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi); // no-op outside Lambda

// The MCP endpoint lives at /mcp/{secret}. The URL is the credential, so keep it private.
var secret = builder.Configuration["McpPathSecret"];
if (secret is null || secret.Length < 32)
    throw new InvalidOperationException("McpPathSecret must be a random string of at least 32 characters (e.g. `openssl rand -hex 32`).");

// Optional defence in depth: only accept requests from these CIDR ranges (e.g. Anthropic's published egress IPs).
var allowed = (builder.Configuration["AllowedSourceCidrs"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(IPNetwork.Parse)
    .ToList();

if (builder.Configuration["MfpCookieParameter"] is not null)
    builder.Services.AddSingleton<IAmazonSimpleSystemsManagement>(_ => new AmazonSimpleSystemsManagementClient());
builder.Services.AddSingleton<CookieStore>();
builder.Services.AddHttpClient<Mfp>(c =>
    {
        c.BaseAddress = new Uri("https://www.myfitnesspal.com/");
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        c.Timeout = TimeSpan.FromSeconds(20);
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false, // a redirect to /account/login means the session expired
        UseCookies = false,        // the Cookie header is set explicitly
        AutomaticDecompression = DecompressionMethods.All,
    });

builder.Services.AddMcpServer(o => o.ServerInfo = new() { Name = "myfitnesspal", Version = "1.0.0" })
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<MfpTools>();

var app = builder.Build();

if (allowed.Count > 0)
{
    app.Use(async (ctx, next) =>
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is { IsIPv4MappedToIPv6: true }) ip = ip.MapToIPv4();
        if (ip is null || !allowed.Any(n => n.Contains(ip)))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await next();
    });
}

app.MapMcp($"/mcp/{secret}");
app.Run();

public partial class Program;
