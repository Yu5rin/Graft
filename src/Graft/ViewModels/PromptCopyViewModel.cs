using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Views;

namespace Graft.ViewModels;

/// <summary>
/// テンプレート1件分の選択肢。展開後の推定トークン数を併記する（仕様書4.8.4）。
/// </summary>
public sealed class PromptTemplateOptionViewModel
{
    public PromptTemplateOptionViewModel(PromptTemplate template, int estimatedTokens)
    {
        Template = template;
        EstimatedTokens = estimatedTokens;
    }

    public PromptTemplate Template { get; }
    public int EstimatedTokens { get; }
    public string DisplayText => $"{Template.Name}（約{EstimatedTokens}トークン）";

    /// <summary>色のみに依存しないための読み上げ用テキスト（8.14）。</summary>
    public string AutomationName => DisplayText;
}

/// <summary>
/// 4.8.4「コピー操作」。テンプレート選択・推定トークン数の表示・クリップボードへのコピーを担う。
/// 変数展開は <see cref="PromptTemplateRenderer"/> に委譲することで、コンテキスト収集（10章）と
/// 同一の出力パイプラインを共有する（<see cref="Context"/> の選択状態がそのまま {{files}} に入る）。
/// </summary>
public sealed class PromptCopyViewModel : ObservableObject
{
    private readonly PromptTemplateStore _templateStore;
    private readonly PromptTemplateRenderer _renderer;
    private readonly RevisionStore _revisionStore;
    private readonly DialogService _dialogs;
    private Project _project;
    private Settings _settings;

    private PromptTemplateOptionViewModel? _selectedTemplate;
    private bool _isOpen;
    private string? _statusMessage;

    public PromptCopyViewModel(
        PromptTemplateStore templateStore,
        PromptTemplateRenderer renderer,
        RevisionStore revisionStore,
        DialogService dialogs,
        ContextCollectViewModel context,
        Project project,
        Settings settings)
    {
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        CopyCommand = new AsyncRelayCommand(CopySelectedAsync, () => SelectedTemplate is not null);
    }

    /// <summary>
    /// 10章のコンテキスト収集ViewModel（収集モード・ファイル選択の唯一の情報源）。
    /// コンテキスト収集ウィンドウが開かれる場合もこのインスタンスをそのまま使うことで、
    /// 「収集モードで選んだファイルがそのまま{{files}}に展開される」（4.8.4）を満たす。
    /// </summary>
    public ContextCollectViewModel Context { get; }

    /// <summary>テンプレート一覧（推定トークン数付き）。ドロップダウンを開くたびに再計算する。</summary>
    public ObservableCollection<PromptTemplateOptionViewModel> Templates { get; } = new();

    public PromptTemplateOptionViewModel? SelectedTemplate
    {
        get => _selectedTemplate;
        set => SetProperty(ref _selectedTemplate, value, () => ((AsyncRelayCommand)CopyCommand).RaiseCanExecuteChanged());
    }

    /// <summary>コマンドバー「プロンプト」ボタンで開閉するドロップダウンの表示状態。</summary>
    public bool IsOpen
    {
        get => _isOpen;
        set => SetProperty(ref _isOpen, value, OnIsOpenChanged);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>ドロップダウンから選択したテンプレートをコピーする。</summary>
    public ICommand CopyCommand { get; }

    /// <summary>プロジェクト切り替え時に呼ぶ。</summary>
    public void UpdateContext(Project project, Settings settings)
    {
        _project = project;
        _settings = settings;
        Templates.Clear();
        SelectedTemplate = null;
    }

    /// <summary>
    /// Ctrl+Shift+C（グローバルホットキー・ウィンドウ内ショートカット共通）。
    /// ドロップダウンを開かず、推奨テンプレート（4.8.1の継続判定に従う）を即座にコピーする。
    /// </summary>
    public async Task QuickCopyAsync()
    {
        if (Templates.Count == 0)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        SelectedTemplate ??= Templates.FirstOrDefault();
        await CopySelectedAsync().ConfigureAwait(true);
    }

    private void OnIsOpenChanged()
    {
        if (_isOpen)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>テンプレート一覧を読み込み、各テンプレートの推定トークン数を計算し直す。</summary>
    private async Task RefreshAsync()
    {
        StatusMessage = null;
        if (Context.Files.Count == 0 && !Context.IsScanning)
        {
            // {{tree}}/{{files}} を空のまま提示しないよう、未走査なら先にスキャンしておく。
            await Context.InitializeAsync().ConfigureAwait(true);
        }

        var loaded = await _templateStore.LoadAsync().ConfigureAwait(true);
        if (!loaded.IsSuccess)
        {
            StatusMessage = "テンプレートの読み込みに失敗しました。";
            return;
        }

        var request = BuildRequest();
        var lastRevision = await GetLastRevisionSummaryAsync().ConfigureAwait(true);
        var useContinuation = _templateStore.ShouldUseContinuation(_project.Id, DateTimeOffset.Now);

        Templates.Clear();
        foreach (var template in loaded.Value)
        {
            var rendered = await _renderer.RenderAsync(template, request, lastRevision).ConfigureAwait(true);
            var tokens = rendered.IsSuccess ? TokenEstimator.Estimate(rendered.Value, _settings.Context.TokenRatio) : 0;
            Templates.Add(new PromptTemplateOptionViewModel(template, tokens));
        }

        // 4.8.1: 直近1時間以内にコピー済みなら継続用（短縮版）を既定表示にする。
        SelectedTemplate = Templates.FirstOrDefault(t => t.Template.IsContinuation == useContinuation)
            ?? Templates.FirstOrDefault();
    }

    private async Task CopySelectedAsync()
    {
        var template = SelectedTemplate;
        if (template is null) return;

        var request = BuildRequest();
        var lastRevision = await GetLastRevisionSummaryAsync().ConfigureAwait(true);
        var rendered = await _renderer.RenderAsync(template.Template, request, lastRevision).ConfigureAwait(true);
        if (!rendered.IsSuccess)
        {
            StatusMessage = "コピーに失敗しました。";
            await _dialogs.ShowMessageAsync("コピーに失敗しました",
                string.Join(Environment.NewLine, rendered.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        Clipboard.SetText(rendered.Value);
        _templateStore.RecordCopy(_project.Id, DateTimeOffset.Now);
        IsOpen = false;
        StatusMessage = $"「{template.Template.Name}」をコピーしました。";
        await _dialogs.ShowMessageAsync("プロンプトをコピーしました",
            $"「{template.Template.Name}」（約{template.EstimatedTokens}トークン）をクリップボードへコピーしました。").ConfigureAwait(true);
    }

    private ContextRequest BuildRequest() => new()
    {
        Project = _project,
        Settings = _settings,
        Mode = Context.SelectedMode,
        SelectedPaths = Context.Files
            .Where(f => f is { IsDirectory: false, IsExcluded: false, IsChecked: true })
            .Select(f => f.RelativePath)
            .ToArray(),
    };

    private async Task<string?> GetLastRevisionSummaryAsync()
    {
        var list = await _revisionStore.ListAsync(_project.Id).ConfigureAwait(true);
        return list.IsSuccess ? list.Value.FirstOrDefault()?.Manifest.Summary : null;
    }
}
