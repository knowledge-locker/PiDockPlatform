using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiDock.Core;

public static class ApplicationDefinition
{
    public const int MaximumBytes = 16384;
    private static readonly JsonSerializerOptions Options = new(StateFiles.Json)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<ApplicationReleaseRequest> Read(Stream input, CancellationToken ct = default)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (bytes.Length + count > MaximumBytes) throw new ArgumentException("App definitions must be at most 16 KiB.");
            bytes.Write(buffer, 0, count);
        }
        return Parse(bytes.ToArray());
    }

    public static ApplicationReleaseRequest Parse(ReadOnlyMemory<byte> input)
    {
        if (input.Length is 0 or > MaximumBytes) throw new ArgumentException("App definitions must be 1–16384 bytes.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
        if (Duplicates(document.RootElement)) throw new ArgumentException("Duplicate definition fields are not allowed.");
        var definition = JsonSerializer.Deserialize<ApplicationReleaseRequest>(input.Span, Options)
            ?? throw new ArgumentException("An app definition is required.");
        Validate(definition);
        return definition;
    }

    public static void Validate(ApplicationReleaseRequest definition)
    {
        ApplicationContractRules.Validate(definition);
        if (definition.Installation is null) throw new ArgumentException("This release has no installation settings. Upload an installable app definition.");
        if (JsonSerializer.SerializeToUtf8Bytes(definition, StateFiles.Json).Length > MaximumBytes)
            throw new ArgumentException("App definition exceeds 16 KiB.");
    }

    private static bool Duplicates(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.EnumerateObject().Count()
            || value.EnumerateObject().Any(p => Duplicates(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(Duplicates),
        _ => false
    };
}
