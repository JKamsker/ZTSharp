using System.Diagnostics.CodeAnalysis;

namespace JKamsker.LibZt;

/// <summary>
/// Event produced by <see cref="ZtNode"/> to signal lifecycle and network changes.
/// </summary>
public sealed record ZtEvent(
    ZtEventCode Code,
    DateTimeOffset TimestampUtc,
    ulong? NetworkId = null,
    string? Message = null,
    [property: AllowNull] Exception? Error = null);
