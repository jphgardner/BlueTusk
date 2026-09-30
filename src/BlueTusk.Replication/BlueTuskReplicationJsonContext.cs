using System.Text.Json.Serialization;

namespace BlueTusk.Replication;

[JsonSerializable(typeof(string[]))]
internal sealed partial class BlueTuskReplicationJsonContext : JsonSerializerContext;
