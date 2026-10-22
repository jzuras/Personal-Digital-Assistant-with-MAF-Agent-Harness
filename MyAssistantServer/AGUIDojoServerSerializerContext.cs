using System.Text.Json.Serialization;

namespace MyAssistantServer;

[JsonSerializable(typeof(List<string>))]
internal sealed partial class AGUIDojoServerSerializerContext : JsonSerializerContext;
