using System.Diagnostics.CodeAnalysis;

namespace ZTSharp;

/// <summary>
/// Event produced by <see cref="Node"/> to signal lifecycle and network changes.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1710:IdentifiersShouldHaveCorrectSuffix", Justification = "Public event argument type name is part of API contract.")]
public sealed class NodeEvent : EventArgs
{
    public NodeEvent(
        EventCode code,
        DateTimeOffset timestampUtc,
        ulong? networkId = null,
        string? message = null,
        [AllowNull] Exception? error = null)
    {
        Code = code;
        TimestampUtc = timestampUtc;
        NetworkId = networkId;
        Message = message;
        Error = error;
    }

    public EventCode Code { get; }

    public DateTimeOffset TimestampUtc { get; }

    public ulong? NetworkId { get; }

    public string? Message { get; }

    [AllowNull] public Exception? Error { get; }
}
