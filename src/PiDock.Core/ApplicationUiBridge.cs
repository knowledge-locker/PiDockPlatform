using System.Text.Json;

namespace PiDock.Core;

public sealed record ApplicationUiBridgeGrant(string CredentialId, string? CommandsPath, string[] Commands,
    string? StreamPath, string Version = "1.0")
{
    public string[] Commands { get; init; } = Commands ?? [];
}
public sealed record ApplicationUiCommand(int SchemaVersion, Guid CommandId, string Name, JsonElement Arguments);
public sealed record ApplicationUiCommandResult(int SchemaVersion, Guid CommandId, bool Succeeded, JsonElement? Data, string? ErrorCode);
public sealed record ApplicationUiCursor(string Epoch, long Sequence);
public sealed record ApplicationUiEvent(int SchemaVersion, string Epoch, long Sequence, string Kind, DateTimeOffset ObservedAt, JsonElement Data);
public sealed record ApplicationUiStreamMessage(string Type, string? Status, ApplicationUiEvent? Event);
public sealed record ApplicationUiBridgeCapabilities(string Version, string[] Commands, bool Streaming);
public sealed record ApplicationUiBridgeAsset(string Name, string Path, string Sha256, string MediaType);
public sealed record ApplicationUiBridgeDiscovery(int SchemaVersion, string Version, string Transport,
    int MaximumCommandBytes, int MaximumEventBytes, ApplicationUiBridgeAsset[] Assets, string[] Limitations);

public static class ApplicationUiBridgeRules
{
    public const string Version = "1.0";
    public const int MaximumCommandBytes = 8192;
    public const int MaximumEventBytes = 32768;
    public const long MaximumSequence = 9007199254740991; // Lossless in browser JSON numbers.
    public static bool Name(string? value) => value is { Length: > 0 and <= 64 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    public static bool Grant(ApplicationUiBridgeGrant? grant) => grant is null ||
        (grant.Version == Version && IdentityRules.IsId(grant.CredentialId) && grant.Commands is { Length: <= 16 } &&
        grant.Commands.All(Name) && grant.Commands.Distinct(StringComparer.Ordinal).Count() == grant.Commands.Length &&
        (grant.Commands.Length == 0 ? grant.CommandsPath is null : ApplicationUiRules.SafePath(grant.CommandsPath)) &&
        (grant.StreamPath is null || ApplicationUiRules.SafePath(grant.StreamPath)) && (grant.Commands.Length > 0 || grant.StreamPath is not null));
    public static bool Cursor(ApplicationUiCursor? cursor) => cursor is null ||
        (Name(cursor.Epoch) && cursor.Sequence is >= 0 and <= MaximumSequence);
    public static bool Event(ApplicationUiEvent value) => value.SchemaVersion == 1 && Name(value.Epoch) &&
        value.Sequence is >= 0 and <= MaximumSequence && value.Kind is "snapshot" or "update" or "heartbeat" &&
        value.ObservedAt >= DateTimeOffset.UnixEpoch && value.Data.ValueKind is not JsonValueKind.Undefined;
}

public sealed class ApplicationUiEventOrder(ApplicationUiCursor? initial)
{
    public ApplicationUiCursor? Cursor { get; private set; } = initial;
    public bool Accept(ApplicationUiEvent next)
    {
        if (!ApplicationUiBridgeRules.Event(next)) return false;
        if (next.Kind == "snapshot")
        {
            if (Cursor?.Epoch == next.Epoch && next.Sequence < Cursor.Sequence) return false;
            Cursor = new(next.Epoch, next.Sequence); return true;
        }
        if (Cursor is null || Cursor.Epoch != next.Epoch) return false;
        if (next.Kind == "heartbeat") return next.Sequence == Cursor.Sequence;
        if (next.Sequence != Cursor.Sequence + 1) return false;
        Cursor = new(next.Epoch, next.Sequence); return true;
    }
}
