namespace ZTSharp.Internal;

internal sealed class NodeRuntimeState
{
    public NodeState State { get; set; } = NodeState.Created;

    public NodeId NodeId { get; set; }

    private int _disposed;

    public bool Disposed
    {
        get => Volatile.Read(ref _disposed) != 0;
        set => Volatile.Write(ref _disposed, value ? 1 : 0);
    }
}

