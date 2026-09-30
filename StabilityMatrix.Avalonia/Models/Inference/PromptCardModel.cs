using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace StabilityMatrix.Avalonia.Models.Inference;

public class PromptCardModel
{
    /// <summary>Legacy: full positive text; use <see cref="BasePrompt"/> when present.</summary>
    public string? Prompt { get; init; }

    /// <summary>Root positive prompt (prefix for each queued child).</summary>
    public string? BasePrompt { get; init; }

    /// <summary>Legacy: only prompt-con text (no grandchild); use <see cref="ExtraPromptLines"/> when present.</summary>
    public List<string>? ExtraPrompts { get; init; }

    /// <summary>Prompt-con rows with grandchild preset index.</summary>
    public List<ExtraPromptLineModel>? ExtraPromptLines { get; init; }

    /// <summary>Up to three preset strings appended after each prompt-con when queue mode is on.</summary>
    public List<string>? GrandchildPresets { get; init; }

    public string? NegativePrompt { get; init; }

    /// <summary>
    /// When true, each non-empty child prompt is combined with the base and run as a separate generation.
    /// If there are no children, a single run uses the base only.
    /// </summary>
    public bool IsLineQueueModeEnabled { get; init; }

    /// <summary>False = negative editor collapsed to one line. Null/absent JSON = expanded.</summary>
    public bool? IsNegativePromptExpanded { get; init; }

    /// <summary>Share of the main split (prompt block vs negative): top * / (top * + bottom *), 0–1.</summary>
    public double? PromptMainNegativeSplitRatio { get; init; }

    public JsonObject? ModulesCardState { get; init; }
}
