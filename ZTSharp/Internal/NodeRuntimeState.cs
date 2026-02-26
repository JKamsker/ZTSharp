namespace ZTSharp.Internal;

internal sealed class NodeRuntimeState
{
    public NodeState State { get; set; } = NodeState.Created;

    public NodeId NodeId { get; set; }

    public bool Disposed { get; set; }
}

