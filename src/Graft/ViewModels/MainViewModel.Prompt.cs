using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Infra;

namespace Graft.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> の分割ファイル（1ファイル400行上限のため）。
/// 仕様書4.8.4「コピー操作」。コマンドバー「プロンプト」ボタンでテンプレート選択ドロップダウンを
/// 開く操作と、Ctrl+Shift+C（グローバルホットキー・ウィンドウ内共通）での即時コピーを担う。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly AppPaths _appPaths = new();
    private ProjectStore _projectStoreForPrompt = null!;
    private PromptTemplateStore _promptTemplateStore = null!;
    private PromptTemplateRenderer _promptTemplateRenderer = null!;

    /// <summary>10章コンテキスト収集ViewModel。プロジェクト選択が変わるたびに作り直す。</summary>
    public ContextCollectViewModel? ContextCollect { get; private set; }

    /// <summary>4.8.4 プロンプトコピーViewModel（コマンドバー「プロンプト」ドロップダウンのDataContext）。</summary>
    public PromptCopyViewModel? PromptCopy { get; private set; }

    /// <summary>コマンドバー「プロンプト」ボタン。テンプレート選択ドロップダウンを開く。</summary>
    public ICommand OpenPromptDropdownCommand { get; private set; } = null!;

    /// <summary>
    /// 4.8.4: Ctrl+Shift+C（ウィンドウ内ショートカット・起動処理担当が配線するグローバルホットキー
    /// の両方から呼ばれる）。ドロップダウンを開かず、推奨テンプレートを即座にコピーする。
    /// </summary>
    public ICommand CopyPromptCommand { get; private set; } = null!;

    /// <summary>MainViewModelのコンストラクタから呼び出す初期化。</summary>
    private void InitializePrompt(ProjectStore projectStore)
    {
        _projectStoreForPrompt = projectStore;
        _promptTemplateStore = new PromptTemplateStore(_appPaths);
        _promptTemplateRenderer = new PromptTemplateRenderer(new ContextCollector(_appPaths));

        OpenPromptDropdownCommand = new RelayCommand(
            () => { if (PromptCopy is not null) PromptCopy.IsOpen = true; },
            () => PromptCopy is not null);

        CopyPromptCommand = new AsyncRelayCommand(
            async () => { if (PromptCopy is not null) await PromptCopy.QuickCopyAsync().ConfigureAwait(true); },
            () => PromptCopy is not null);
    }

    /// <summary>プロジェクトが切り替わるたびに、コンテキスト収集・プロンプトコピーの両ViewModelを作り直す。</summary>
    private void RebuildPromptContext(Project project)
    {
        ContextCollect = new ContextCollectViewModel(_appPaths, _projectStoreForPrompt, project, _settings);
        PromptCopy = new PromptCopyViewModel(
            _promptTemplateStore, _promptTemplateRenderer, _revisionStore, _dialogs, ContextCollect, project, _settings);
        OnPropertyChanged(nameof(ContextCollect));
        OnPropertyChanged(nameof(PromptCopy));
    }
}
