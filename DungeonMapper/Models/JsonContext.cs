using System.Text.Json.Serialization;

namespace DungeonMapper.Models;

// compile time json metadata for every type the app saves or loads. the
// reflection based serializer needs type info that trimming strips from the
// published exe, this does not, so the exe can be trimmed.
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(MapFileDto))]
[JsonSerializable(typeof(ProjectFileDto))]
[JsonSerializable(typeof(MapExportDto))]
[JsonSerializable(typeof(MapSerializer.LibraryWire))]
[JsonSerializable(typeof(MapSerializer.ObjectTypesWire))]
[JsonSerializable(typeof(PresetsWire))]
internal partial class DmJson : JsonSerializerContext { }
