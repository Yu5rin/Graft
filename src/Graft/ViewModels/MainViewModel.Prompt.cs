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
    // 【データ保存先は、起動時に決まったものを使う】 以前は「new AppPaths()」で、MainViewModel を作るたびに
    // ポインタファイル（datapath.txt）などから保存先を解決し直していた。起動時の StartupCoordinator が決めた保存先
    // （旧配置への移行に失敗して旧配置のまま起動したときなど、解決し直した結果と食い違いうる）と別の場所の
    // focus.json・templates.json を見ることになり、設定で作った観点がチップに出ない。ハーネス経由のテストも、
    // 実行ファイルの隣の data を見てしまい、自作の観点を実際の経路で確かめられなかった。
    // ProjectStore が持つ AppPaths（起動時に渡されたもの）を InitializePrompt で受け取って使う。
    private AppPaths _appPaths = null!;
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
        _appPaths = projectStore.Paths;
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

    /// <summary>
    /// 右クリックメニュー「AIに渡すファイルに追加」の実体（エクスプローラ・エディタのタブ・検索結果）。
    /// 「AIに渡すファイルを選ぶ」画面でそのファイルを「内容も出す」にする。画面は開かず、結果を数秒で消える
    /// 通知で伝える（成功を知らせるだけなのでOKを押させない）。AIが求めたファイルを追加する操作
    /// （<see cref="AddRequestedFilesToContextCommand"/>）と同じ <see cref="ContextCollectViewModel.ApplyRequestedFilesAsync"/>
    /// を使うため、保存の経路・除外の判定・収集モードの切り替えは同じ挙動になる。
    /// </summary>
    public async Task AddFilesToContextAsync(IReadOnlyList<string> relativePaths)
    {
        if (ContextCollect is null || relativePaths.Count == 0) return;

        var result = await ContextCollect.ApplyRequestedFilesAsync(relativePaths).ConfigureAwait(true);
        if (result.Matched.Count > 0)
        {
            var name = result.Matched.Count == 1 ? $"「{Path.GetFileName(result.Matched[0])}」" : $"{result.Matched.Count}件";
            Notice.Show($"{name}をAIに渡すファイルに追加しました"
                + (result.ModeChanged ? "（収集モードを「ツリー＋選択」に切り替えました）" : string.Empty));
        }
        else if (result.Excluded.Count > 0)
        {
            Notice.Show("除外の設定に当てはまるファイルのため、AIに渡すファイルに追加できませんでした");
        }
        else
        {
            Notice.Show("プロジェクトの中に見つからず、AIに渡すファイルに追加できませんでした");
        }
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
        // Excel VBA プロジェクトのときだけ、走査の前に Excel から最新を取り込む処理を渡す（仕様書_VBA連携 7.2。
        // 通常のファイルプロジェクトでは null で、従来どおり何も増えない）。
        ContextCollect = new ContextCollectViewModel(
            _appPaths, _projectStore, project, _settings, _ui, _dialogs, vbaPull: CreateVbaContextPull(project),
            vbaWorkbookInfo: CreateVbaWorkbookInfoSource(project));
        PromptCopy = new PromptCopyViewModel(
            _promptTemplateStore, _promptTemplateRenderer, _revisionStore, _dialogs, ContextCollect, project, _settings, _ui);
        PromptCopy.AppendSelectedFilesChangeCommitted += OnAppendSelectedFilesChangeCommitted;
        PromptCopy.Notify = text => Notice.Show(text);
        // 依頼の観点: プロジェクトを開いた時点で、保存済みの選択（と自作の観点）を読み込んでおく。
        // ドロップダウンや窓を開くたびにも読み直すが、開く前にチップの状態を確定させておけば、
        // 最初の描画で「前回の選択」が見える。失敗しても依頼文の作成は止めない（ReloadFocusAsync内で記録のみ）。
        _ = ContextCollect.ReloadFocusAsync();
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
