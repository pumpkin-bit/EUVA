// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace EUVA.Core.Robots.Patterns;

public sealed class TransformRule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;
    [JsonPropertyName("replacement")]
    public string Replacement { get; set; } = string.Empty;
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "regex";
    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 50;
    [JsonPropertyName("requires_context")]
    public bool RequiresContext { get; set; } = false;
    [JsonPropertyName("context_key")]
    public string? ContextKey { get; set; }
    [JsonPropertyName("guard")]
    public string? Guard { get; set; }

    public override string ToString() => $"[{Id}] {Description}";
}
