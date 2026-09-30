using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StabilityMatrix.Avalonia.ViewModels.Base;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

public partial class ExtraPromptRowViewModel : ViewModelBase
{
    private readonly PromptCardViewModel parent;

    public ExtraPromptRowViewModel(
        PromptCardViewModel parent,
        string initialText = "",
        int grandchildPresetIndex = 0
    )
    {
        this.parent = parent;
        Text = initialText;
        GrandchildPresetIndex = ClampGrandchildIndex(grandchildPresetIndex);
    }

    private static int ClampGrandchildIndex(int value) =>
        Math.Clamp(value, 0, PromptCardViewModel.GrandchildPresetCount - 1);

    [ObservableProperty]
    private string text = "";

    [ObservableProperty]
    private int grandchildPresetIndex;

    [RelayCommand]
    private void RemoveRow() => parent.RemoveExtraRow(this);
}
