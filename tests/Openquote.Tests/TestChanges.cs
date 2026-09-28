using System.Text;
using System.Text.Json.Nodes;
using Openquote.Vault;

namespace Openquote.Tests;

/// <summary>Builds change files for tests. Ids are UUIDv7-shaped and sort in the order given by <c>n</c>.</summary>
internal static class TestChanges
{
    public static string Id(int n) => $"01900000-0000-7000-8000-{n:x12}";

    public static JsonObject Json(int n, string entityId, string op = "update", int[]? baseIds = null,
        JsonObject? fields = null, string device = "dev1", string entityType = "session") => new()
    {
        ["format"] = "openquote.change/0",
        ["id"] = Id(n),
        ["device"] = device,
        ["at"] = $"2026-01-01T09:{n % 60:00}:00+09:00",
        ["entity"] = new JsonObject { ["type"] = entityType, ["id"] = entityId },
        ["op"] = op,
        ["base"] = new JsonArray((baseIds ?? []).Select(b => (JsonNode)JsonValue.Create(Id(b))!).ToArray()),
        ["fields"] = fields ?? new JsonObject(),
    };

    public static VaultFile File(JsonObject change, string folder = "subjects/s1", string? name = null) =>
        File($"{folder}/{name ?? $"{change["id"]}.{change["device"]}"}.json", change.ToJsonString());

    public static VaultFile File(string path, string content) => new(path, Encoding.UTF8.GetBytes(content));
}
