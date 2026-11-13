using System.Text.Json.Serialization;
using MuseLite.AgenticUI;
using MyAssistant.DurableContext;

namespace MuseLite;

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Plan))]
[JsonSerializable(typeof(Step))]
[JsonSerializable(typeof(StepStatus))]
[JsonSerializable(typeof(JsonPatchOperation))]
[JsonSerializable(typeof(List<JsonPatchOperation>))]
[JsonSerializable(typeof(ImageItem))]
[JsonSerializable(typeof(ImageResult))]
[JsonSerializable(typeof(List<ImageItem>))]
internal sealed partial class AGUIDojoServerSerializerContext : JsonSerializerContext;
