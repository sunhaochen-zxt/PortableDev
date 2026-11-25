using System.Text.Json.Nodes;

namespace PortableDev.Core.Config;

public enum SettingKind
{
    String,
    Int,
    Bool,
    Duration,
    Enum,
    Path,
    Url,
}

/// <summary>One entry of the settings table (kernel.md 2.1).</summary>
public sealed record SettingDefinition(
    string Path,
    SettingKind Kind,
    JsonNode? DefaultValue,
    string[]? EnumValues = null,
    double? Min = null,
    double? Max = null,
    bool AllowZero = false,
    bool RestartRequired = false);
