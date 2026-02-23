namespace JKamsker.LibZt.ZeroTier.Protocol;

internal enum ZtZeroTierVerb : byte
{
    Nop = 0x00,
    Hello = 0x01,
    Error = 0x02,
    Ok = 0x03,
    Whois = 0x04,
    Rendezvous = 0x05,
    Frame = 0x06,
    ExtFrame = 0x07,
    Echo = 0x08,
    MulticastLike = 0x09,
    NetworkCredentials = 0x0A,
    NetworkConfigRequest = 0x0B,
    NetworkConfig = 0x0C,
    MulticastGather = 0x0D,
    MulticastFrame = 0x0E,
    PushDirectPaths = 0x10,
    Ack = 0x12,
    QosMeasurement = 0x13,
    UserMessage = 0x14,
    RemoteTrace = 0x15,
    PathNegotiationRequest = 0x16,
}

