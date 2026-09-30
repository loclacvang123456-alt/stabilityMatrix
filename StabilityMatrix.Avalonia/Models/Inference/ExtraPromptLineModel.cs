namespace StabilityMatrix.Avalonia.Models.Inference;

/// <summary>One queued prompt-con line with optional grandchild preset.</summary>
public sealed class ExtraPromptLineModel
{
    public string Text { get; init; } = "";

    /// <summary>0 = first grandchild preset, 1 = second, 2 = third.</summary>
    public int GrandchildPresetIndex { get; init; }
}
