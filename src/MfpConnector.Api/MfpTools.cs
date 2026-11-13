using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MfpConnector.Api;

[McpServerToolType]
public sealed class MfpTools(Mfp mfp, IConfiguration config)
{
    private const string DateHelp = "Date as YYYY-MM-DD, or 'today' / 'yesterday'. Defaults to today.";
    private const string MealHelp = "Meal name as it appears in the diary, e.g. Breakfast, Lunch, Dinner, Snacks.";

    [McpServerTool(Name = "get_diary", ReadOnly = true)]
    [Description("Get the MyFitnessPal food diary for a day: every entry with its meal and nutrition, the day's totals, the goals for that day, and what remains.")]
    public async Task<string> GetDiary([Description(DateHelp)] string? date = null, CancellationToken ct = default)
    {
        var day = ParseDate(date);
        var diaryTask = mfp.GetAsync($"api/services/diary/read_diary?entry_date={day:yyyy-MM-dd}&fields=all&types=food_entry", ct);
        var goalsTask = mfp.GetAsync($"api/services/diary/nutrient_goals?date={day:yyyy-MM-dd}", ct);

        var entries = Items(await diaryTask).Select(Entry).ToList();
        var totals = Sum(entries.Select(e => e["nutrition"]!.AsObject()));

        JsonObject? goals = null, remaining = null;
        try
        {
            var g = await goalsTask;
            var weekday = day.DayOfWeek.ToString().ToLowerInvariant();
            var chosen = (g?["daily_goals"] as JsonArray)?.FirstOrDefault(d => Str(d?["day_of_week"]) == weekday) ?? g?["default_goal"];
            if (chosen is not null)
            {
                goals = Macros(chosen);
                remaining = new JsonObject();
                foreach (var key in new[] { "calories", "carbs_g", "fat_g", "protein_g" })
                    remaining[key] = R(Num(goals[key]) - Num(totals[key]));
            }
        }
        catch (McpException) { /* goals are optional */ }

        return new JsonObject
        {
            ["date"] = $"{day:yyyy-MM-dd}",
            ["entries"] = new JsonArray(entries.ToArray<JsonNode>()),
            ["totals"] = totals,
            ["goals"] = goals,
            ["remaining"] = remaining,
        }.ToJsonString();
    }

    [McpServerTool(Name = "search_foods", ReadOnly = true)]
    [Description(
        "Find foods the user logged in the last 14 days or created themselves. Recent foods come first, with how the user last logged them. " +
        "MyFitnessPal's public food database can NOT be searched: for anything not found, use quick_add with your best estimate.")]
    public async Task<string> SearchFoods(
        [Description("Words to look for in the food name or brand.")] string query,
        [Description("Maximum results.")] int limit = 10,
        CancellationToken ct = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(JsonNode f) => words.All(w => $"{Str(f["description"])} {Str(f["brand_name"])}".Contains(w, StringComparison.OrdinalIgnoreCase));

        var today = Today();
        var days = Enumerable.Range(0, 14)
            .Select(i => mfp.GetAsync($"api/services/diary/read_diary?entry_date={today.AddDays(-i):yyyy-MM-dd}&fields=all&types=food_entry", ct))
            .ToList();
        var mine = mfp.GetAsync($"api/services/users/foods/mine?search={Uri.EscapeDataString(query)}", ct);
        await Task.WhenAll(days.Append(mine));

        var recent = days.SelectMany(d => Items(d.Result))
            .Where(e => e["food"] is { } f && Str(f["description"])?.StartsWith("Quick Add") != true && Matches(f))
            .GroupBy(e => e["food"]!["id"]!.ToString())
            .OrderByDescending(g => g.Count())
            .Select(g =>
            {
                var last = g.MaxBy(e => Str(e["date"]))!;
                var food = Food(last["food"]!);
                food["source"] = "recent_diary";
                food["times_logged_recently"] = g.Count();
                food["last_logged"] = Str(last["date"]);
                food["last_servings"] = R(Num(last["servings"]), 2);
                food["last_serving_size_index"] = ServingIndex(last["food"]!, last["serving_size"]);
                return food;
            });
        var own = Items(mine.Result).Select(f => { var food = Food(f); food["source"] = "my_foods"; return food; });

        var results = recent.Concat(own).DistinctBy(f => Str(f["food_id"])).Take(Math.Clamp(limit, 1, 25));
        return new JsonArray(results.ToArray<JsonNode>()).ToJsonString();
    }

    [McpServerTool(Name = "log_food")]
    [Description("Add a food from search_foods to the diary. Amount = servings × the chosen serving size.")]
    public async Task<string> LogFood(
        [Description("food_id from search_foods.")] string food_id,
        [Description(MealHelp)] string meal,
        [Description("Number of servings; can be fractional.")] double servings,
        [Description("food_version from search_foods.")] string? food_version = null,
        [Description("Index of the serving size from search_foods.")] int serving_size_index = 0,
        [Description(DateHelp)] string? date = null,
        CancellationToken ct = default)
    {
        if (servings <= 0) throw new McpException("servings must be greater than 0.");
        var (position, _) = await ResolveMealAsync(meal, ct);
        var version = string.IsNullOrEmpty(food_version) ? "" : $"?version={Uri.EscapeDataString(food_version)}";
        var food = await mfp.GetAsync($"api/services/foods/{Uri.EscapeDataString(food_id)}{version}", ct)
                   ?? throw new McpException("Food not found.");
        var serving = ((food["serving_sizes"] as JsonArray) ?? [])
            .Select((s, i) => (Index: (int)(Num(s?["index"]) ?? i), Size: s))
            .FirstOrDefault(s => s.Index == serving_size_index).Size
            ?? throw new McpException($"This food has no serving size with index {serving_size_index}.");

        var created = await mfp.SendAsync(HttpMethod.Post, "api/services/diary", Items(new JsonObject
        {
            ["type"] = "food_entry",
            ["date"] = $"{ParseDate(date):yyyy-MM-dd}",
            ["food"] = new JsonObject { ["id"] = food["id"]?.ToString(), ["version"] = food["version"]?.ToString() },
            ["servings"] = servings,
            ["meal_position"] = position,
            ["serving_size"] = new JsonObject
            {
                ["nutrition_multiplier"] = Num(serving["nutrition_multiplier"]),
                ["unit"] = Str(serving["unit"]),
                ["value"] = Num(serving["value"]),
            },
        }), ct);
        return Entry(Items(created).First()).ToJsonString();
    }

    [McpServerTool(Name = "quick_add")]
    [Description("Log calories (and optionally macros) directly to a meal, for foods search_foods can't find. Tell the user what you estimated.")]
    public async Task<string> QuickAdd(
        [Description(MealHelp)] string meal,
        [Description("Calories (kcal).")] double calories,
        [Description("Carbohydrates (g).")] double carbs_g = 0,
        [Description("Fat (g).")] double fat_g = 0,
        [Description("Protein (g).")] double protein_g = 0,
        [Description(DateHelp)] string? date = null,
        CancellationToken ct = default)
    {
        var (_, mealName) = await ResolveMealAsync(meal, ct);
        var created = await mfp.SendAsync(HttpMethod.Post, "api/services/diary", Items(new JsonObject
        {
            ["type"] = "quick_add",
            ["meal_name"] = mealName,
            ["date"] = $"{ParseDate(date):yyyy-MM-dd}",
            ["nutritional_contents"] = new JsonObject
            {
                ["energy"] = new JsonObject { ["value"] = calories, ["unit"] = "calories" },
                ["carbohydrates"] = carbs_g, ["fat"] = fat_g, ["protein"] = protein_g,
            },
        }), ct);
        return Entry(Items(created).First()).ToJsonString();
    }

    [McpServerTool(Name = "delete_diary_entry", Destructive = true)]
    [Description("Delete a diary entry (entry_id from get_diary, log_food or quick_add). Confirm with the user first.")]
    public async Task<string> DeleteDiaryEntry(string entry_id, CancellationToken ct = default)
    {
        await mfp.SendAsync(HttpMethod.Delete, $"api/services/diary/{Uri.EscapeDataString(entry_id)}", null, ct);
        return $"Deleted {entry_id}.";
    }

    [McpServerTool(Name = "get_weight_history", ReadOnly = true)]
    [Description("Get recorded weigh-ins (kg) for the last N days, oldest first.")]
    public async Task<string> GetWeightHistory([Description("Days to look back.")] int days = 30, CancellationToken ct = default)
    {
        var to = Today();
        var json = await mfp.GetAsync($"api/user-measurements/measurements?types=Weight&from={to.AddDays(-Math.Clamp(days, 1, 730)):yyyy-MM-dd}&to={to:yyyy-MM-dd}&limit=500", ct);
        var weights = Items(json)
            .OrderBy(w => Str(w["date"]))
            .Select(w => (JsonNode)new JsonObject { ["date"] = Str(w["date"]), ["kg"] = R(Num(w["value"]), 2), ["id"] = w["id"]?.ToString() });
        return new JsonArray(weights.ToArray()).ToJsonString();
    }

    [McpServerTool(Name = "log_weight", Destructive = true)]
    [Description("Record a weigh-in. Replaces any weigh-in already recorded for that date.")]
    public async Task<string> LogWeight(
        [Description("Body weight.")] double value,
        [Description("'kg', 'lb' or 'st'.")] string unit = "kg",
        [Description(DateHelp)] string? date = null,
        CancellationToken ct = default)
    {
        var kg = unit.Trim().ToLowerInvariant() switch
        {
            "kg" => value,
            "lb" or "lbs" => value * 0.45359237,
            "st" => value * 6.35029318,
            _ => throw new McpException("unit must be kg, lb or st."),
        };
        var json = await mfp.SendAsync(HttpMethod.Put, "api/user-measurements/measurements", Items(new JsonObject
        {
            ["type"] = "Weight", ["value"] = Math.Round(kg, 2), ["unit"] = "kilograms", ["date"] = $"{ParseDate(date):yyyy-MM-dd}",
        }), ct);
        var w = Items(json).First();
        return new JsonObject { ["date"] = Str(w["date"]), ["kg"] = R(Num(w["value"]), 2), ["id"] = w["id"]?.ToString() }.ToJsonString();
    }

    // ---- helpers ----

    private async Task<(int Position, string Name)> ResolveMealAsync(string meal, CancellationToken ct)
    {
        var settings = await mfp.GetAsync("api/services/account/diary_settings", ct);
        var meals = ((settings?["meal_names"] as JsonArray) ?? [])
            .Where(m => Str(m?["description"]) is not null)
            .Select(m => (Position: (int)Num(m!["position"])!.Value - 1, Name: Str(m["description"])!)) // positions are 1-based here, 0-based on entries
            .ToList();
        var wanted = meal.Trim();
        var match = meals.FirstOrDefault(m => m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                                              || m.Name.Equals(wanted + "s", StringComparison.OrdinalIgnoreCase));
        return match.Name is not null
            ? match
            : throw new McpException($"Unknown meal '{meal}'. This diary's meals are: {string.Join(", ", meals.Select(m => m.Name))}.");
    }

    private DateOnly Today()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(config["UserTimeZone"] ?? "Europe/London");
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime);
    }

    private DateOnly ParseDate(string? date) => date?.Trim().ToLowerInvariant() switch
    {
        null or "" or "today" => Today(),
        "yesterday" => Today().AddDays(-1),
        var d when DateOnly.TryParseExact(d, "yyyy-MM-dd", out var parsed) => parsed,
        _ => throw new McpException($"Invalid date '{date}'. Use YYYY-MM-DD."),
    };

    private static JsonObject Entry(JsonNode e)
    {
        var name = Str(e["food"]?["description"]) ?? "Unknown";
        return new JsonObject
        {
            ["entry_id"] = e["id"]?.ToString(),
            ["meal"] = Str(e["meal_name"]),
            ["food"] = name.StartsWith("Quick Add") ? "Quick Add" : name,
            ["brand"] = Str(e["food"]?["brand_name"]),
            ["servings"] = R(Num(e["servings"]), 2),
            ["serving_size"] = $"{Num(e["serving_size"]?["value"])?.ToString(CultureInfo.InvariantCulture)} {Str(e["serving_size"]?["unit"])}".Trim(),
            ["nutrition"] = Macros(e["nutritional_contents"]),
        };
    }

    private static JsonObject Food(JsonNode f)
    {
        var sizes = ((f["serving_sizes"] as JsonArray) ?? []).Select((s, i) => (JsonNode)new JsonObject
        {
            ["index"] = (int)(Num(s?["index"]) ?? i),
            ["value"] = Num(s?["value"]),
            ["unit"] = Str(s?["unit"]),
            ["calories"] = R(Num(f["nutritional_contents"]?["energy"]?["value"]) * (Num(s?["nutrition_multiplier"]) ?? 1), 0),
        });
        return new JsonObject
        {
            ["food_id"] = f["id"]?.ToString(),
            ["food_version"] = f["version"]?.ToString(),
            ["name"] = Str(f["description"]),
            ["brand"] = Str(f["brand_name"]),
            ["serving_sizes"] = new JsonArray(sizes.ToArray()),
            ["nutrition_per_multiplier_1"] = Macros(f["nutritional_contents"]),
        };
    }

    private static int? ServingIndex(JsonNode food, JsonNode? used) =>
        ((food["serving_sizes"] as JsonArray) ?? [])
            .Select((s, i) => (Index: (int)(Num(s?["index"]) ?? i), Size: s))
            .Where(s => Str(s.Size?["unit"]) == Str(used?["unit"]) && Num(s.Size?["nutrition_multiplier"]) == Num(used?["nutrition_multiplier"]))
            .Select(s => (int?)s.Index)
            .FirstOrDefault();

    private static JsonObject Macros(JsonNode? n) => new()
    {
        ["calories"] = R(Num(n?["energy"]?["value"]), 0),
        ["carbs_g"] = R(Num(n?["carbohydrates"])),
        ["fat_g"] = R(Num(n?["fat"])),
        ["protein_g"] = R(Num(n?["protein"])),
        ["fiber_g"] = R(Num(n?["fiber"])),
        ["sugar_g"] = R(Num(n?["sugar"])),
        ["sodium_mg"] = R(Num(n?["sodium"]), 0),
    };

    private static JsonObject Sum(IEnumerable<JsonObject> items)
    {
        var total = new JsonObject();
        foreach (var key in Macros(null).Select(kv => kv.Key))
            total[key] = R(items.Sum(i => Num(i[key]) ?? 0), key is "calories" or "sodium_mg" ? 0 : 1);
        return total;
    }

    /// <summary>MFP returns collections as a bare array or as { items: [...] }; request bodies use { items: [...] }.</summary>
    private static IEnumerable<JsonNode> Items(JsonNode? n) =>
        (n switch { JsonArray a => a, JsonObject o when o["items"] is JsonArray a => a, _ => [] }).OfType<JsonNode>();

    private static JsonObject Items(JsonObject item) => new() { ["items"] = new JsonArray(item) };

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue(out double d) ? d : null;
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    private static double R(double? v, int digits = 1) => Math.Round(v ?? 0, digits, MidpointRounding.AwayFromZero);
}
