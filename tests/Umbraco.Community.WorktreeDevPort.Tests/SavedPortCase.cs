using System.Text.Json;

namespace Umbraco.Community.WorktreeDevPort.Tests;

/// <summary>
/// A saved-port file's text and the port it should read as (null when it should be ignored).
/// Shared with the Node package's tests, so both sides agree on what's saved.
/// </summary>
public sealed record SavedPortCase(string Text, int? Port)
{
    public static TheoryData<SavedPortCase> All()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "saved-port-cases.json"));
        var cases = JsonSerializer.Deserialize<List<SavedPortCase>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        return new TheoryData<SavedPortCase>(cases);
    }

    public override string ToString() => JsonSerializer.Serialize(Text);
}
