using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsyncAwaitBestPractices;
using Avalonia.Controls.Notifications;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using Injectio.Attributes;
using Microsoft.Extensions.Logging;
using OpenIddict.Client;
using Refit;
using StabilityMatrix.Avalonia.Controls;
using StabilityMatrix.Avalonia.Languages;
using StabilityMatrix.Avalonia.Models;
using StabilityMatrix.Avalonia.Models.Inference;
using StabilityMatrix.Avalonia.Models.TagCompletion;
using StabilityMatrix.Avalonia.Services;
using StabilityMatrix.Avalonia.ViewModels.Base;
using StabilityMatrix.Avalonia.ViewModels.Dialogs;
using StabilityMatrix.Avalonia.ViewModels.Inference.Modules;
using StabilityMatrix.Avalonia.ViewModels.Settings;
using StabilityMatrix.Core.Api;
using StabilityMatrix.Core.Api.PromptGenApi;
using StabilityMatrix.Core.Attributes;
using StabilityMatrix.Core.Exceptions;
using StabilityMatrix.Core.Helper;
using StabilityMatrix.Core.Helper.Cache;
using StabilityMatrix.Core.Models;
using StabilityMatrix.Core.Models.Api.Comfy.Nodes;
using StabilityMatrix.Core.Models.Api.Lykos;
using StabilityMatrix.Core.Models.PromptSyntax;
using StabilityMatrix.Core.Models.Settings;
using StabilityMatrix.Core.Processes;
using StabilityMatrix.Core.Services;
using YamlDotNet.Core.Tokens;
using Prompt = StabilityMatrix.Avalonia.Models.Inference.Prompt;
using TeachingTip = StabilityMatrix.Core.Models.Settings.TeachingTip;

namespace StabilityMatrix.Avalonia.ViewModels.Inference;

[View(typeof(PromptCard))]
[ManagedService]
[RegisterTransient<PromptCardViewModel>]
public partial class PromptCardViewModel
    : DisposableLoadableViewModelBase,
        IParametersLoadableState,
        IComfyStep
{
    public const int GrandchildPresetCount = 3;

    private readonly IModelIndexService modelIndexService;
    private readonly ISettingsManager settingsManager;
    private readonly TabContext tabContext;
    private readonly IPromptGenApi promptGenApi;
    private readonly INotificationService notificationService;
    private readonly ILogger<PromptCardViewModel> logger;
    private readonly IAccountsService accountsService;
    private readonly IServiceManager<ViewModelBase> vmFactory;

    /// <summary>
    /// Cache of prompt text to tokenized Prompt
    /// </summary>
    private LRUCache<string, Prompt> PromptCache { get; } = new(4);

    public ICompletionProvider CompletionProvider { get; }
    public ITokenizerProvider TokenizerProvider { get; }
    public SharedState SharedState { get; }

    /// <summary>Root positive prompt shown in the card.</summary>
    public TextDocument BasePromptDocument { get; } = new();

    /// <summary>Working copy used during ApplyStep; mirrors <see cref="BasePromptDocument"/> when idle.</summary>
    public TextDocument PromptDocument { get; } = new();

    public TextDocument NegativePromptDocument { get; } = new();

    public ObservableCollection<ExtraPromptRowViewModel> ExtraPromptRows { get; } = new();

    public StackEditableCardViewModel ModulesCardViewModel { get; }

    [ObservableProperty]
    private bool isAutoCompletionEnabled;

    [ObservableProperty]
    private bool isHelpButtonTeachingTipOpen;

    [ObservableProperty]
    private bool isPromptAmplifyTeachingTipOpen;

    [ObservableProperty]
    private bool isNegativePromptEnabled = true;

    /// <summary>When false, the negative prompt editor is hidden to save vertical space (content is unchanged).</summary>
    [ObservableProperty]
    private bool isNegativePromptExpanded = true;

    /// <summary>
    /// When enabled, Generate runs each non-empty child prompt combined with the base as its own forward pass, sequentially.
    /// </summary>
    [ObservableProperty]
    private bool isLineQueueModeEnabled;

    [ObservableProperty]
    private bool isThinkingEnabled;

    [ObservableProperty]
    private bool isFocused;

    [ObservableProperty]
    private bool isBalanced = true;

    [ObservableProperty]
    private bool isImaginative;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLowTokenWarning), nameof(LowTokenWarningText))]
    private int tokensRemaining = -1;

    [ObservableProperty]
    private bool isFlyoutOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLowTokenWarning))]
    private int lowTokenThreshold = 25;

    [ObservableProperty]
    public partial bool IsStackCardEnabled { get; set; } = true;

    [ObservableProperty]
    private string grandchildPreset1 = "";

    [ObservableProperty]
    private string grandchildPreset2 = "";

    [ObservableProperty]
    private string grandchildPreset3 = "";

    /// <summary>
    /// Upper panel (positive) share of vertical space vs negative panel; used when saving/restoring splitter.
    /// </summary>
    [ObservableProperty]
    private double promptMainNegativeSplitRatio = 0.55;

    public bool ShowLowTokenWarning => TokensRemaining <= LowTokenThreshold && TokensRemaining >= 0;

    public string LowTokenWarningText =>
        $"{TokensRemaining} amplification{(TokensRemaining == 1 ? "" : "s")} remaining (resets in {Utilities.GetNumDaysTilBeginningOfNextMonth()} days)";

    /// <inheritdoc />
    public PromptCardViewModel(
        ICompletionProvider completionProvider,
        ITokenizerProvider tokenizerProvider,
        ISettingsManager settingsManager,
        IModelIndexService modelIndexService,
        IServiceManager<ViewModelBase> vmFactory,
        IPromptGenApi promptGenApi,
        INotificationService notificationService,
        ILogger<PromptCardViewModel> logger,
        IAccountsService accountsService,
        SharedState sharedState,
        TabContext tabContext
    )
    {
        this.modelIndexService = modelIndexService;
        this.settingsManager = settingsManager;
        this.tabContext = tabContext;
        this.promptGenApi = promptGenApi;
        this.notificationService = notificationService;
        this.logger = logger;
        this.accountsService = accountsService;
        this.vmFactory = vmFactory;
        CompletionProvider = completionProvider;
        TokenizerProvider = tokenizerProvider;
        SharedState = sharedState;

        // Subscribe to tab context state changes
        tabContext.StateChanged += OnTabContextStateChanged;

        ModulesCardViewModel = vmFactory.Get<StackEditableCardViewModel>(vm =>
        {
            vm.Title = "Styles";
            vm.AvailableModules = [typeof(PromptExpansionModule)];
        });

        AddDisposable(
            settingsManager.RelayPropertyFor(
                this,
                vm => vm.IsAutoCompletionEnabled,
                settings => settings.IsPromptCompletionEnabled,
                true
            )
        );

        BasePromptDocument.TextChanged += (_, _) => SyncPromptDocumentFromBase();
        SyncPromptDocumentFromBase();
    }

    private void SyncPromptDocumentFromBase() => PromptDocument.Text = BasePromptDocument.Text;

    public void RemoveExtraRow(ExtraPromptRowViewModel row) => ExtraPromptRows.Remove(row);

    [RelayCommand]
    private void AddExtraPromptRow() => ExtraPromptRows.Add(new ExtraPromptRowViewModel(this));

    [RelayCommand]
    private void ToggleNegativePromptExpanded() => IsNegativePromptExpanded = !IsNegativePromptExpanded;

    private void OnTabContextStateChanged(object? sender, TabContext.TabStateChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabContext.SelectedModel))
        {
            // Handle selected model change
            // Could use this to update prompt suggestions based on the model
        }
    }

    public override void OnUnloaded()
    {
        base.OnUnloaded();

        // Unsubscribe from events when view is unloaded
        tabContext.StateChanged -= OnTabContextStateChanged;
    }

    partial void OnIsHelpButtonTeachingTipOpenChanging(bool oldValue, bool newValue)
    {
        // If the teaching tip is being closed, save the setting
        if (oldValue && !newValue)
        {
            settingsManager.Transaction(settings =>
            {
                settings.SeenTeachingTips.Add(TeachingTip.InferencePromptHelpButtonTip);
            });

            if (!settingsManager.Settings.SeenTeachingTips.Contains(TeachingTip.InferencePromptAmplifyTip))
            {
                IsPromptAmplifyTeachingTipOpen = true;
            }
        }
    }

    partial void OnIsPromptAmplifyTeachingTipOpenChanging(bool oldValue, bool newValue)
    {
        if (oldValue && !newValue)
        {
            settingsManager.Transaction(settings =>
            {
                settings.SeenTeachingTips.Add(TeachingTip.InferencePromptAmplifyTip);
            });
        }
    }

    /// <inheritdoc />
    public override async Task OnLoadedAsync()
    {
        await base.OnLoadedAsync();

        // Show teaching tip for help button if not seen
        if (!settingsManager.Settings.SeenTeachingTips.Contains(TeachingTip.InferencePromptHelpButtonTip))
        {
            IsHelpButtonTeachingTipOpen = true;
        }

        if (
            !IsHelpButtonTeachingTipOpen
            && !settingsManager.Settings.SeenTeachingTips.Contains(TeachingTip.InferencePromptAmplifyTip)
        )
        {
            IsPromptAmplifyTeachingTipOpen = true;
        }
    }

    protected override Task OnInitialLoadedAsync()
    {
        Task.Run(async () =>
            {
                try
                {
                    var isLoggedIn = await accountsService.HasStoredLykosAccountAsync();
                    if (!isLoggedIn)
                    {
                        return;
                    }

                    SetTokenThreshold();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error refreshing account data");
                }

                try
                {
                    var result = await promptGenApi.AccountMeTokens();
                    TokensRemaining = result.Available;
                }
                catch (ApiException e)
                {
                    if (
                        e.StatusCode != HttpStatusCode.Unauthorized
                        && e.StatusCode != HttpStatusCode.NotFound
                    )
                    {
                        notificationService.Show(
                            "Error retrieving prompt amplifier data",
                            e.Message,
                            NotificationType.Error
                        );
                        return;
                    }

                    TokensRemaining = -1;
                }
            })
            .SafeFireAndForget(onException: ex => logger.LogError(ex, "Error getting prompt amplifier data"));

        return Task.CompletedTask;
    }

    private void SetTokenThreshold()
    {
        if (accountsService.LykosStatus is not { User: not null } status)
            return;

        if (status.User.Roles.Count is 1 && status.User.Roles.Contains(LykosRole.Basic.ToString()))
        {
            LowTokenThreshold = 25;
        }
    }

    /// <summary>
    /// Applies the prompt step.
    /// Requires:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.Base"/> - Model, Clip</item>
    /// </list>
    /// Provides:
    /// <list type="number">
    /// <item><see cref="ComfyNodeBuilder.NodeBuilderConnections.Base"/> - Conditioning</item>
    /// </list>
    /// </summary>
    public void ApplyStep(ModuleApplyStepEventArgs e)
    {
        // Load prompts
        var positivePrompt = GetPrompt();
        positivePrompt.Process();
        e.Builder.Connections.PositivePrompt = positivePrompt.ProcessedText;

        var negativePrompt = GetNegativePrompt();
        negativePrompt.Process();
        e.Builder.Connections.NegativePrompt = negativePrompt.ProcessedText;

        // Apply modules / styles that may modify the prompt
        ModulesCardViewModel.ApplyStep(e);

        foreach (var modelConnections in e.Builder.Connections.Models.Values)
        {
            if (modelConnections.Model is not { } model || modelConnections.Clip is not { } clip)
                continue;

            // If need to load loras, add a group
            if (positivePrompt.ExtraNetworks.Count > 0)
            {
                var loras = positivePrompt.GetExtraNetworksAsLocalModels(modelIndexService).ToList();

                // Add group to load loras onto model and clip in series
                var lorasGroup = e.Builder.Group_LoraLoadMany(
                    $"Loras_{modelConnections.Name}",
                    model,
                    clip,
                    loras
                );

                // Set last outputs as model and clip
                modelConnections.Model = lorasGroup.Output1;
                modelConnections.Clip = lorasGroup.Output2;
            }

            // Clips
            var positiveClip = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = $"PositiveCLIP_{modelConnections.Name}",
                    Clip = e.Builder.Connections.Base.Clip!,
                    Text = e.Builder.Connections.PositivePrompt,
                }
            );
            var negativeClip = e.Nodes.AddTypedNode(
                new ComfyNodeBuilder.CLIPTextEncode
                {
                    Name = $"NegativeCLIP_{modelConnections.Name}",
                    Clip = e.Builder.Connections.Base.Clip!,
                    Text = e.Builder.Connections.NegativePrompt,
                }
            );

            // Set conditioning from Clips
            modelConnections.Conditioning = (positiveClip.Output, negativeClip.Output);
        }
    }

    /// <summary>
    /// Gets the tokenized Prompt for given text and caches it
    /// </summary>
    private Prompt GetOrCachePrompt(string text)
    {
        // Try get from cache
        if (PromptCache.Get(text, out var cachedPrompt))
        {
            return cachedPrompt!;
        }
        var prompt = Prompt.FromRawText(text, TokenizerProvider);
        PromptCache.Add(text, prompt);
        return prompt;
    }

    /// <summary>
    /// Processes current positive prompt text into a Prompt object
    /// </summary>
    public Prompt GetPrompt() => GetOrCachePrompt(PromptDocument.Text);

    /// <summary>
    /// Processes current negative prompt text into a Prompt object
    /// </summary>
    public Prompt GetNegativePrompt() => GetOrCachePrompt(NegativePromptDocument.Text);

    /// <summary>
    /// Validates both prompts, shows an error dialog if invalid
    /// </summary>
    public async Task<bool> ValidatePrompts()
    {
        var promptText = BasePromptDocument.Text;
        var negPromptText = NegativePromptDocument.Text;

        try
        {
            var prompt = GetOrCachePrompt(promptText);
            prompt.Process(processWildcards: false);
            prompt.ValidateExtraNetworks(modelIndexService);
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, promptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        try
        {
            var negPrompt = GetOrCachePrompt(negPromptText);
            negPrompt.Process();
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(e, negPromptText, modelIndexService);
            await dialog.ShowAsync();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates prompts for image generation — including per-line validation when line queue mode is on.
    /// </summary>
    public async Task<bool> ValidatePromptsForGenerationAsync()
    {
        if (!IsLineQueueModeEnabled)
            return await ValidatePrompts();

        var lines = BuildQueuedPositiveLines();
        if (lines.Count == 0)
        {
            notificationService.Show(
                "Prompt queue",
                "Điền prompt gốc hoặc thêm ít nhất một prompt con (ô không trống).",
                NotificationType.Warning
            );
            return false;
        }

        foreach (var line in lines)
        {
            try
            {
                var prompt = GetOrCachePrompt(line);
                prompt.Process(processWildcards: false);
                prompt.ValidateExtraNetworks(modelIndexService);
            }
            catch (PromptError e)
            {
                var dialog = DialogHelper.CreatePromptErrorDialog(e, line, modelIndexService);
                await dialog.ShowAsync();
                return false;
            }
        }

        try
        {
            var negPrompt = GetOrCachePrompt(NegativePromptDocument.Text);
            negPrompt.Process();
        }
        catch (PromptError e)
        {
            var dialog = DialogHelper.CreatePromptErrorDialog(
                e,
                NegativePromptDocument.Text,
                modelIndexService
            );
            await dialog.ShowAsync();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Lines to run: base only when queue is off; base+child per child when queue is on (or base only if no children).
    /// </summary>
    public IReadOnlyList<string> GetQueuedPositivePromptLines() => BuildQueuedPositiveLines();

    private List<string> BuildQueuedPositiveLines()
    {
        var baseText = BasePromptDocument.Text;
        if (!IsLineQueueModeEnabled)
        {
            return string.IsNullOrWhiteSpace(baseText) ? [] : [baseText];
        }

        var lines = new List<string>();
        foreach (var row in ExtraPromptRows)
        {
            var child = row.Text.Trim();
            if (string.IsNullOrWhiteSpace(child))
                continue;

            var line = CombineQueuedPositive(baseText, child);
            var gc = GrandchildPresetTextAt(row.GrandchildPresetIndex);
            lines.Add(CombineQueuedPositive(line, gc));
        }

        if (lines.Count == 0)
        {
            return string.IsNullOrWhiteSpace(baseText) ? [] : [baseText.Trim()];
        }

        return lines;
    }

    private string GrandchildPresetTextAt(int index)
    {
        var i = Math.Clamp(index, 0, GrandchildPresetCount - 1);
        return i switch
        {
            0 => GrandchildPreset1,
            1 => GrandchildPreset2,
            _ => GrandchildPreset3,
        };
    }

    private static string CombineQueuedPositive(string baseText, string childText)
    {
        var b = baseText.Trim();
        var c = childText.Trim();
        if (string.IsNullOrEmpty(b))
            return c;
        if (string.IsNullOrEmpty(c))
            return b;
        return $"{b} {c}";
    }

    [RelayCommand]
    private async Task ShowHelpDialog()
    {
        var md = $$"""
            ## {{Resources.Label_Emphasis}}
            You can also use (`Ctrl+Up`/`Ctrl+Down`) in the editor to adjust the 
            weight emphasis of the token under the caret or the currently selected text.
            ```prompt
            (keyword)
            (keyword:1.0)
            ```

            ## {{Resources.Label_Deemphasis}}
            ```prompt
            [keyword]
            ```

            ## {{Resources.Label_EmbeddingsOrTextualInversion}}
            They may be used in either the positive or negative prompts. 
            Essentially they are text presets, so the position where you place them 
            could make a difference. 
            ```prompt
            <embedding:model>
            <embedding:model:1.0>
            ```

            ## {{Resources.Label_NetworksLoraOrLycoris}}
            Unlike embeddings, network tags do not get tokenized to the model, 
            so the position in the prompt where you place them does not matter.
            ```prompt
            <lora:model>
            <lora:model:1.0>
            <lyco:model>
            <lyco:model:1.0>
            ```

            ## {{Resources.Label_Comments}}
            Inline comments can be marked by a hashtag ' # '. 
            All text after a ' # ' on a line will be disregarded during generation.
            ```prompt
            # comments
            a red cat # also comments
            detailed
            ```

            ## {{Resources.Label_Wildcards}}
            Wildcards can be used to select a random value from a list of options.
            ```prompt
            {red|green|blue} cat
            ```
            In this example, a color will be randomly chosen at the start of each generation. 
            The final output could be "red cat", "green cat", or "blue cat".

            You can also use networks and embeddings in wildcards. For example:
            ```prompt
            {<lora:model:1>|<embedding:model>} cat
            ```
            """;

        var dialog = DialogHelper.CreateMarkdownDialog(md, "Prompt Syntax", TextEditorPreset.Prompt);
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private async Task DebugShowTokens()
    {
        var prompt = GetPrompt();

        try
        {
            prompt.Process();
        }
        catch (PromptError e)
        {
            await DialogHelper.CreatePromptErrorDialog(e, prompt.RawText, modelIndexService).ShowAsync();
            return;
        }

        var tokens = prompt.TokenizeResult.Tokens;

        var builder = new StringBuilder();

        try
        {
            var astBuilder = new PromptSyntaxBuilder(prompt.TokenizeResult, prompt.RawText);
            var ast = astBuilder.BuildAST();
            builder.AppendLine("## AST:");
            builder.AppendLine("```csharp");
            builder.AppendLine(ast.ToDebugString());
            builder.AppendLine("```");
        }
        catch (PromptError e)
        {
            builder.AppendLine($"## AST (Error)");
            builder.AppendLine($"({e.GetType().Name} - {e.Message})");
            builder.AppendLine("```csharp");
            builder.AppendLine(e.StackTrace);
            builder.AppendLine("```");
            throw;
        }

        builder.AppendLine($"## Tokens ({tokens.Length}):");
        builder.AppendLine("```csharp");
        builder.AppendLine(prompt.GetDebugText());
        builder.AppendLine("```");

        try
        {
            if (prompt.ExtraNetworks is { } networks)
            {
                builder.AppendLine($"## Networks ({networks.Count}):");
                builder.AppendLine("```csharp");
                builder.AppendLine(
                    JsonSerializer.Serialize(networks, new JsonSerializerOptions() { WriteIndented = true })
                );
                builder.AppendLine("```");
            }

            builder.AppendLine("## Formatted for server:");
            builder.AppendLine("```csharp");
            builder.AppendLine(prompt.ProcessedText);
            builder.AppendLine("```");
        }
        catch (PromptError e)
        {
            builder.AppendLine($"##{e.GetType().Name} - {e.Message}");
            builder.AppendLine("```csharp");
            builder.AppendLine(e.StackTrace);
            builder.AppendLine("```");
            throw;
        }

        var dialog = DialogHelper.CreateMarkdownDialog(builder.ToString(), "Prompt Tokens");
        dialog.MinDialogWidth = 800;
        dialog.MaxDialogHeight = 1000;
        dialog.MaxDialogWidth = 1000;
        await dialog.ShowAsync();
    }

    [RelayCommand]
    private void EditorCopy(TextEditor? textEditor)
    {
        textEditor?.Copy();
    }

    [RelayCommand]
    private void EditorPaste(TextEditor? textEditor)
    {
        textEditor?.Paste();
    }

    [RelayCommand]
    private void EditorCut(TextEditor? textEditor)
    {
        textEditor?.Cut();
    }

    [RelayCommand]
    private async Task AmplifyPrompt()
    {
        if (!settingsManager.Settings.SeenTeachingTips.Contains(TeachingTip.PromptAmplifyDisclaimer))
        {
            var dialog = DialogHelper.CreateMarkdownDialog(Resources.PromptAmplifier_Disclaimer);
            dialog.PrimaryButtonText = "Continue";
            dialog.CloseButtonText = "Back";
            dialog.IsPrimaryButtonEnabled = true;
            dialog.DefaultButton = ContentDialogButton.Primary;

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                settingsManager.Transaction(settings =>
                {
                    settings.SeenTeachingTips.Add(TeachingTip.PromptAmplifyDisclaimer);
                });
            }
            else
            {
                return;
            }
        }

        var valid = await ValidatePrompts();
        if (!valid)
            return;

        var prompt = GetPrompt();
        if (string.IsNullOrWhiteSpace(prompt.RawText))
        {
            notificationService.Show("Prompt Amplifier Error", "Prompt is empty", NotificationType.Error);
            return;
        }

        var negativePrompt = GetNegativePrompt();
        var selectedModel = tabContext.SelectedModel;
        var modelTags = selectedModel?.Local?.ConnectedModelInfo?.BaseModel?.ToLower() switch
        {
            { } baseModel when baseModel.Contains("flux") => new List<ModelTags> { ModelTags.Flux },
            { } baseModel when baseModel.Contains("sdxl") => [ModelTags.Sdxl],
            "pony" => [ModelTags.Pony],
            "noobai" => [ModelTags.Illustrious],
            "illustrious" => [ModelTags.Illustrious],
            _ => [],
        };
        var mode =
            IsFocused ? PromptExpansionRequestMode.Focused
            : IsImaginative ? PromptExpansionRequestMode.Imaginative
            : PromptExpansionRequestMode.Balanced;
        try
        {
            var expandedPrompt = await promptGenApi.ExpandPrompt(
                new PromptExpansionRequest
                {
                    Prompt = new PromptToEnhance
                    {
                        PositivePrompt = prompt.ProcessedText ?? prompt.RawText,
                        NegativePrompt = negativePrompt.ProcessedText ?? negativePrompt.RawText,
                        Model = selectedModel?.Local?.DisplayModelName,
                    },
                    Model = IsThinkingEnabled ? "PromptV1ThinkingDev" : "PromptV1Dev",
                    Mode = mode,
                    ModelTags = modelTags,
                }
            );

            BasePromptDocument.Text = expandedPrompt.Response.PositivePrompt;
            NegativePromptDocument.Text = expandedPrompt.Response.NegativePrompt;

            TokensRemaining = expandedPrompt.AvailableTokens;
        }
        catch (ApiException e)
        {
            logger.LogError(e, "Error amplifying prompt");
            switch (e.StatusCode)
            {
                case HttpStatusCode.PaymentRequired:
                {
                    var dialog = DialogHelper.CreateMarkdownDialog(
                        string.Format(
                            Resources.Label_PromptAmplifierRateLimitMessage,
                            Utilities.GetNumDaysTilBeginningOfNextMonth()
                        ),
                        Resources.Label_PromptAmplifierRateLimitTitle
                    );
                    dialog.PrimaryButtonText = Resources.Action_Upgrade;
                    dialog.PrimaryButtonCommand = new RelayCommand(() =>
                        ProcessRunner.OpenUrl(Assets.MembershipUrl)
                    );
                    dialog.IsPrimaryButtonEnabled = true;
                    dialog.DefaultButton = ContentDialogButton.Primary;

                    await dialog.ShowAsync();
                    break;
                }
                case HttpStatusCode.BadRequest:
                    notificationService.Show(
                        Resources.Label_ModerationError,
                        Resources.Label_PromptFlaggedMessage,
                        NotificationType.Error
                    );
                    break;
                case HttpStatusCode.Unauthorized:
                    if (await ShowLoginDialog())
                    {
                        await AmplifyPrompt();
                    }
                    else
                    {
                        notificationService.Show(
                            Resources.Label_PromptAmplifierError,
                            Resources.Label_LoginRequiredMessage,
                            NotificationType.Error
                        );
                    }
                    break;
                default:
                    notificationService.Show(
                        Resources.Label_PromptAmplifierError,
                        Resources.Label_RequestErrorMessage,
                        NotificationType.Error
                    );
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error amplifying prompt");
            notificationService.Show(Resources.Label_PromptAmplifierError, e.Message, NotificationType.Error);
        }
    }

    [RelayCommand]
    private Task ShowAmplifierDisclaimer() =>
        DialogHelper.CreateMarkdownDialog(Resources.PromptAmplifier_Disclaimer).ShowAsync();

    partial void OnIsBalancedChanged(bool value)
    {
        switch (value)
        {
            case false when !IsFocused && !IsImaginative:
                IsBalanced = true;
                return;
            case false:
                return;
            default:
                IsFocused = false;
                IsImaginative = false;
                break;
        }
    }

    partial void OnIsFocusedChanged(bool value)
    {
        if (!value)
            return;

        IsBalanced = false;
        IsImaginative = false;
    }

    partial void OnIsImaginativeChanged(bool value)
    {
        if (!value)
            return;

        IsBalanced = false;
        IsFocused = false;
    }

    /// <inheritdoc />
    public override JsonObject SaveStateToJsonObject()
    {
        var baseText = BasePromptDocument.Text;
        return SerializeModel(
            new PromptCardModel
            {
                BasePrompt = baseText,
                Prompt = baseText,
                ExtraPromptLines = ExtraPromptRows
                    .Select(r => new ExtraPromptLineModel
                    {
                        Text = r.Text ?? "",
                        GrandchildPresetIndex = r.GrandchildPresetIndex,
                    })
                    .ToList(),
                GrandchildPresets =
                [
                    GrandchildPreset1 ?? "",
                    GrandchildPreset2 ?? "",
                    GrandchildPreset3 ?? "",
                ],
                NegativePrompt = NegativePromptDocument.Text,
                IsLineQueueModeEnabled = IsLineQueueModeEnabled,
                IsNegativePromptExpanded = IsNegativePromptExpanded,
                PromptMainNegativeSplitRatio = PromptMainNegativeSplitRatio,
                ModulesCardState = ModulesCardViewModel.SaveStateToJsonObject(),
            }
        );
    }

    /// <inheritdoc />
    public override void LoadStateFromJsonObject(JsonObject state)
    {
        var model = DeserializeModel<PromptCardModel>(state);

        var baseText = model.BasePrompt ?? model.Prompt ?? "";
        BasePromptDocument.Text = baseText;

        ExtraPromptRows.Clear();
        if (model.ExtraPromptLines is not null)
        {
            foreach (var line in model.ExtraPromptLines)
            {
                ExtraPromptRows.Add(
                    new ExtraPromptRowViewModel(this, line.Text ?? "", line.GrandchildPresetIndex)
                );
            }
        }
        else
        {
            foreach (var t in model.ExtraPrompts ?? [])
            {
                ExtraPromptRows.Add(new ExtraPromptRowViewModel(this, t));
            }
        }

        LoadGrandchildPresets(model.GrandchildPresets);

        PromptDocument.Text = baseText;
        NegativePromptDocument.Text = model.NegativePrompt ?? "";
        IsLineQueueModeEnabled = model.IsLineQueueModeEnabled;
        IsNegativePromptExpanded = model.IsNegativePromptExpanded ?? true;
        if (model.PromptMainNegativeSplitRatio is { } rx)
            PromptMainNegativeSplitRatio = Math.Clamp(rx, 0.12, 0.88);

        if (model.ModulesCardState is not null)
        {
            ModulesCardViewModel.LoadStateFromJsonObject(model.ModulesCardState);
        }
    }

    private void LoadGrandchildPresets(IReadOnlyList<string>? list)
    {
        GrandchildPreset1 = list?.ElementAtOrDefault(0) ?? "";
        GrandchildPreset2 = list?.ElementAtOrDefault(1) ?? "";
        GrandchildPreset3 = list?.ElementAtOrDefault(2) ?? "";
    }

    /// <inheritdoc />
    public void LoadStateFromParameters(GenerationParameters parameters)
    {
        BasePromptDocument.Text = parameters.PositivePrompt ?? "";
        ExtraPromptRows.Clear();
        PromptDocument.Text = BasePromptDocument.Text;
        NegativePromptDocument.Text = parameters.NegativePrompt ?? "";
    }

    /// <inheritdoc />
    public GenerationParameters SaveStateToParameters(GenerationParameters parameters)
    {
        return parameters with
        {
            PositivePrompt = BasePromptDocument.Text,
            NegativePrompt = NegativePromptDocument.Text,
        };
    }

    private async Task<bool> ShowLoginDialog()
    {
        var dialog = DialogHelper.CreateTaskDialog(
            "Lykos Account Required",
            "You need to be logged in to use this feature. Please log in to your Lykos account."
        );

        dialog.Buttons =
        [
            new TaskDialogButton(Resources.Action_Login, TaskDialogStandardResult.OK),
            TaskDialogButton.CloseButton,
        ];

        if (await dialog.ShowAsync(true) is not TaskDialogStandardResult.OK)
            return false;

        var vm = vmFactory.Get<OAuthDeviceAuthViewModel>();
        vm.ChallengeRequest = new OpenIddictClientModels.DeviceChallengeRequest
        {
            ProviderName = OpenIdClientConstants.LykosAccount.ProviderName,
        };
        await vm.ShowDialogAsync();

        if (vm.AuthenticationResult is not { } result)
            return false;

        await accountsService.LykosAccountV2LoginAsync(
            new LykosAccountV2Tokens(result.AccessToken, result.RefreshToken, result.IdentityToken)
        );

        var tokens = await promptGenApi.AccountMeTokens();
        TokensRemaining = tokens.Available;
        SetTokenThreshold();

        return true;
    }
}
