namespace JKamsker.LibZt;

/// <summary>
/// Canonical high-level event types emitted by <see cref="ZtNode"/>.
/// </summary>
public enum ZtEventCode
{
    NodeCreated,
    NodeStarting,
    NodeStarted,
    NodeStopping,
    NodeStopped,
    NodeFaulted,
    NetworkJoinRequested,
    NetworkJoined,
    NetworkLeft,
    IdentityInitialized,
    StateFlushed
}
