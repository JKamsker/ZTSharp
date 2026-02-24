namespace ZTSharp;

/// <summary>
/// Canonical high-level event types emitted by <see cref="Node"/>.
/// </summary>
public enum EventCode
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
    NetworkFrameReceived,
    NetworkFrameSent,
    IdentityInitialized,
    StateFlushed
}
