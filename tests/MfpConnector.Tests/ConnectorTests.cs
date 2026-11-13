using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MfpConnector.Api;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace MfpConnector.Tests;

public sealed class Factory(string? cookie = null) : WebApplicationFactory<Program>
{
    public const string Secret = "test-secret-0123456789abcdef0123456789abcdef";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("McpPathSecret", Secret);
        builder.UseSetting("MfpCookie", cookie ?? "not-a-real-cookie");
    }

    public Task<McpClient> ConnectAsync()
    {
        var http = CreateClient();
        return McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, $"/mcp/{Secret}"),
            TransportMode = HttpTransportMode.StreamableHttp,
        }, http));
    }
}

public sealed class ConnectorTests
{
    [Fact]
    public async Task Only_the_secret_path_answers()
    {
        using var factory = new Factory();
        var http = factory.CreateClient();
        var body = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/mcp", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/mcp/wrong-secret", body)).StatusCode);

        await using var client = await factory.ConnectAsync();
        var tools = await client.ListToolsAsync();
        Assert.Equal(
            ["delete_diary_entry", "get_diary", "get_weight_history", "log_food", "log_weight", "quick_add", "search_foods"],
            tools.Select(t => t.Name).Order());
    }
}

/// <summary>
/// Against the real MyFitnessPal account. Skipped unless MFP_LIVE_COOKIE is set:
///   MFP_LIVE_COOKIE="$(sed -n 's/^MFP_COOKIE=//p' .env)" dotnet test --filter Category=Live
/// MFP_LIVE_WRITES=1 additionally writes to 2020-01-01 (only if empty) and deletes everything again.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveTests(ITestOutputHelper output)
{
    private static readonly string? Cookie = Environment.GetEnvironmentVariable("MFP_LIVE_COOKIE");

    private async Task<JsonElement> Call(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        Assert.True(result.IsError != true, $"{tool}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Read_tools_work()
    {
        if (Cookie is null) return;
        using var factory = new Factory(Cookie);
        await using var client = await factory.ConnectAsync();

        var diary = await Call(client, "get_diary", new());
        output.WriteLine($"today: {diary.GetProperty("entries").GetArrayLength()} entries, {diary.GetProperty("totals").GetProperty("calories")} kcal, goals: {diary.GetProperty("goals").ValueKind}");

        var weights = await Call(client, "get_weight_history", new() { ["days"] = 120 });
        output.WriteLine($"weights: {weights.GetArrayLength()}");

        var food = diary.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("food").GetString()!).FirstOrDefault(f => f != "Quick Add");
        if (food is not null)
        {
            var found = await Call(client, "search_foods", new() { ["query"] = food.Split(' ')[0] });
            Assert.Equal("recent_diary", found[0].GetProperty("source").GetString());
            output.WriteLine($"search: {found.GetArrayLength()} results");
        }
    }

    [Fact]
    public async Task Write_tools_round_trip_on_2020_01_01()
    {
        if (Cookie is null || Environment.GetEnvironmentVariable("MFP_LIVE_WRITES") != "1") return;
        using var factory = new Factory(Cookie);
        await using var client = await factory.ConnectAsync();
        const string day = "2020-01-01";
        var mfp = factory.Services.GetRequiredService<Mfp>();
        async Task<JsonElement> Entries() => (await Call(client, "get_diary", new() { ["date"] = day })).GetProperty("entries");
        async Task<List<string>> WeightIds() =>
            (await mfp.GetAsync($"api/user-measurements/measurements?types=Weight&from={day}&to={day}", default))!["items"]!.AsArray()
            .Select(w => w!["id"]!.ToString()).ToList();

        Assert.Equal(0, (await Entries()).GetArrayLength());
        Assert.Empty(await WeightIds());
        try
        {
            var name = (await Call(client, "get_diary", new())).GetProperty("entries").EnumerateArray()
                .Select(e => e.GetProperty("food").GetString()!).First(f => f != "Quick Add");
            var food = (await Call(client, "search_foods", new() { ["query"] = name.Split(' ')[0] }))[0];

            var logged = await Call(client, "log_food", new()
            {
                ["food_id"] = food.GetProperty("food_id").GetString(),
                ["food_version"] = food.GetProperty("food_version").GetString(),
                ["serving_size_index"] = food.GetProperty("last_serving_size_index").ValueKind == JsonValueKind.Number
                    ? food.GetProperty("last_serving_size_index").GetInt32() : 0,
                ["meal"] = "snack", ["servings"] = 1, ["date"] = day,
            });
            Assert.Equal("Snacks", logged.GetProperty("meal").GetString());

            var quick = await Call(client, "quick_add", new() { ["meal"] = "Snacks", ["calories"] = 33, ["protein_g"] = 3, ["date"] = day });
            Assert.Equal(33, quick.GetProperty("nutrition").GetProperty("calories").GetDouble());

            var weight = await Call(client, "log_weight", new() { ["value"] = 176.37, ["unit"] = "lb", ["date"] = day });
            Assert.Equal(80, weight.GetProperty("kg").GetDouble());

            Assert.Equal(2, (await Entries()).GetArrayLength());
            var deleted = await client.CallToolAsync("delete_diary_entry",
                new Dictionary<string, object?> { ["entry_id"] = logged.GetProperty("entry_id").GetString() });
            Assert.True(deleted.IsError != true);
            Assert.Equal(1, (await Entries()).GetArrayLength());
            output.WriteLine($"logged {logged.GetProperty("nutrition").GetProperty("calories")} kcal, quick add, weight; delete_diary_entry works");
        }
        finally
        {
            foreach (var e in (await Entries()).EnumerateArray())
                await mfp.SendAsync(HttpMethod.Delete, $"api/services/diary/{e.GetProperty("entry_id").GetString()}", null, default);
            foreach (var id in await WeightIds())
                await mfp.SendAsync(HttpMethod.Delete, $"api/user-measurements/measurements/{id}", null, default);
        }

        Assert.Equal(0, (await Entries()).GetArrayLength());
        Assert.Empty(await WeightIds());
        output.WriteLine("cleanup verified: 2020-01-01 is empty");
    }
}
