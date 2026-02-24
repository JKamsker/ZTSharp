using System.Text.Json.Serialization;

namespace JKamsker.LibZt;

[JsonSerializable(typeof(NetworkState))]
internal partial class JsonContext : JsonSerializerContext;
