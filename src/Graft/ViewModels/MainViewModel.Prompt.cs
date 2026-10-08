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
    // プロンプトコピー機能用に保持していたが、不具合2対応（MainViewModel.Apply.cs の
    // リビジョン番号消費）でも同じ ProjectStore を使うようになったため、用途を限定しない
    // 名前に変更した（保持自体はコンストラクタ経由でここが初出のため、このファイルに残す）。
    private ProjectStore _projectStore = null!;
    private PromptTemplateStore _promptTemplateStore = null!;
    private PromptTemplateRenderer _promptTemplateRenderer = null!;

    /// <summary>10章コンテキスト収集ViewModel。プロジェクト選択が変わるたびに作り直す。</summary>
    public ContextCollectViewModel? ContextCollect { get; private set; }

    /// <summary>4.8.4 プロンプトコピーViewModel（コマンドバー「プロンプト」ドロップダウンのDataContext）。</summary>
    public PromptCopyViewModel? PromptCopy { get; private set; }

    /// <summary>コマンドバー「プロンプト」ボタン。テンプレート選択ドロップダウンを開く。</summary>
    public ICommand OpenPromptDropdownCommand { get; private set; } = null!;

    /// <summary>
    /// 10章コンテキスト収集ウィンドウを開く。プロジェクトのフォルダ構成・ファイル一覧を
    /// 確認できる唯一の画面（Graftは汎用のファイルブラウザを持たない設計のため）。
    /// 実際にウィンドウを生成して表示する処理はUI層（MainWindow側）に委譲する。
    /// </summary>
    public ICommand OpenContextCollectCommand { get; private set; } = null!;

    /// <summary>
    /// プロンプトのコピーの「選んだファイルも付ける」を利用者が切り替えたことの通知。
    /// StartupCoordinatorが購読し、常駐のSettingsViewModel経由で設定へ保存する
    /// （<see cref="PromptCopyViewModel.AppendSelectedFiles"/>参照）。PromptCopyはプロジェクトを
    /// 切り替えるたびに作り直すため、購読者がその都度つなぎ直さなくて済むよう、
    /// 作り直しをまたいで生きるこのクラスのイベントに集約している。
    /// </summary>
    public event EventHandler<bool>? AppendFilesToPromptChangeRequested;

    /// <summary>
    /// MainWindow側でContextCollectWindowを開くための通知。
    /// <see cref="ContextCollectOpenEventArgs.RequestedPaths"/> が空でなければ、窓を開いたあとに
    /// そのファイルを「内容も出す」に設定する（AIの「このファイルも見せて」への対応）。
    /// </summary>
    public event EventHandler<ContextCollectOpenEventArgs>? RequestOpenContextCollect;

    /// <summary>
    /// AIの「このファイルも見せて」（E710）に応える操作。コンテキスト収集の窓を開き、AIが求めた
    /// ファイルを「内容も出す」に設定する。
    ///
    /// 【置き場所を中央ペインのエラー表示にした理由】E710は中央ペインに「エラー」として出る。
    /// 利用者が次にやることは「そのファイルをAIへ渡す」ことで、その入口がエラー表示の
    /// すぐ下にあれば、窓を自分で開いて該当ファイルを探し直す手間が無くなる。コマンドバーなど
    /// 常設の場所に置くと、E710でないときは意味の無いボタンが常に並ぶことになる。
    /// 求められたファイルが無い（語だけの合図）ときは出さない（<see cref="RequestedFilesActionText"/>が空）。
    /// </summary>
    public ICommand AddRequestedFilesToContextCommand { get; private set; } = null!;

    /// <summary>
    /// 中央ペインのエラー表示に出す、要求ファイルの収集追加ボタンの文言。E710で求められたファイルが
    /// あるときだけ値を持ち、それ以外は空（空ならボタンごと非表示。EmptyStateView.IssueActionText）。
    /// </summary>
    public string RequestedFilesActionText
        => CenterError?.RequestedPaths is { Count: > 0 } paths ? $"要求されたファイルを収集に追加（{paths.Count}件）" : string.Empty;

    /// <summary>
    /// 4.8.4: Ctrl+Shift+C（ウィンドウ内ショートカット・起動処理担当が配線するグローバルホットキー
    /// の両方から呼ばれる）。ドロップダウンを開かず、推奨テンプレートを即座にコピーする。
    /// </summary>
    public ICommand CopyPromptCommand { get; private set; } = null!;

    /// <summary>MainViewModelのコンストラクタから呼び出す初期化。</summary>
    private void InitializePrompt(ProjectStore projectStore)
    {
        _projectStore = projectStore;
        _promptTemplateStore = new PromptTemplateStore(_appPaths);
        _promptTemplateRenderer = new PromptTemplateRenderer(new ContextCollector(_appPaths));

        OpenPromptDropdownCommand = new RelayCommand(
            () => { if (PromptCopy is not null) PromptCopy.IsOpen = true; },
            () => PromptCopy is not null);

        CopyPromptCommand = new AsyncRelayCommand(
            async () => { if (PromptCopy is not null) await PromptCopy.QuickCopyAsync().ConfigureAwait(true); },
            () => PromptCopy is not null,
            context: "プロンプトのクイックコピー");

        OpenContextCollectCommand = new RelayCommand(
            () => RequestOpenContextCollect?.Invoke(this, ContextCollectOpenEventArgs.None),
            () => ContextCollect is not null);

        AddRequestedFilesToContextCommand = new RelayCommand(
            () =>
            {
                if (CenterError?.RequestedPaths is not { Count: > 0 } paths) return;
                RequestOpenContextCollect?.Invoke(this, new ContextCollectOpenEventArgs(paths));
            },
            () => ContextCollect is not null && CenterError?.RequestedPaths is { Count: > 0 });
    }

    private void OnAppendSelectedFilesChangeCommitted(object? sender, bool value)
        => AppendFilesToPromptChangeRequested?.Invoke(this, value);

    /// <summary>プロジェクトが切り替わるたびに、コンテキスト収集・プロンプトコピーの両ViewModelを作り直す。</summary>
    private void RebuildPromptContext(Project project)
    {
        // 差し替え前のインスタンスが持つ3状態永続化用のデバウンスタイマーを止める
        // （課題2追加要件: プロジェクト切替後に古いタイマーが発火して不要な保存が走るのを防ぐ）。
        ContextCollect?.Dispose();
        if (PromptCopy is not null) PromptCopy.AppendSelectedFilesChangeCommitted -= OnAppendSelectedFilesChangeCommitted;
        ContextCollect = new ContextCollectViewModel(_appPaths, _projectStore, project, _settings, _ui, _dialogs);
        PromptCopy = new PromptCopyViewModel(
            _promptTemplateStore, _promptTemplateRenderer, _revisionStore, _dialogs, ContextCollect, project, _settings, _ui);
        PromptCopy.AppendSelectedFilesChangeCommitted += OnAppendSelectedFilesChangeCommitted;
        OnPropertyChanged(nameof(ContextCollect));
        OnPropertyChanged(nameof(PromptCopy));
    }
}

/// <summary>
/// コンテキスト収集ウィンドウを開く通知の引数。AIが求めたファイルがあれば運ぶ。
/// </summary>
public sealed class ContextCollectOpenEventArgs : EventArgs
{
    /// <summary>ファイルの要求を伴わない、通常の「開く」。</summary>
    public static ContextCollectOpenEventArgs None { get; } = new(Array.Empty<string>());

    public ContextCollectOpenEventArgs(IReadOnlyList<string> requestedPaths)
    {
        RequestedPaths = requestedPaths ?? throw new ArgumentNullException(nameof(requestedPaths));
    }

    /// <summary>AIが求めたファイルのパス（書かれたまま）。無ければ空。</summary>
    public IReadOnlyList<string> RequestedPaths { get; }
}
