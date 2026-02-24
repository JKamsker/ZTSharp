using System.Text.Json.Serialization;

namespace ZTSharp;

[JsonSerializable(typeof(NetworkState))]
internal partial class JsonContext : JsonSerializerContext;
