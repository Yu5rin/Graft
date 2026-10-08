using System.Collections.ObjectModel;
using System.Windows.Input;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;

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
    private readonly IDialogService _dialogs;
    private readonly IUiServices _ui;
    private Project _project;
    private Settings _settings;

    private PromptTemplateOptionViewModel? _selectedTemplate;
    private bool _isOpen;
    private string? _statusMessage;
    private bool _appendSelectedFiles;
    private int _refreshGeneration;

    public PromptCopyViewModel(
        PromptTemplateStore templateStore,
        PromptTemplateRenderer renderer,
        RevisionStore revisionStore,
        IDialogService dialogs,
        ContextCollectViewModel context,
        Project project,
        Settings settings,
        IUiServices ui)
    {
        _templateStore = templateStore ?? throw new ArgumentNullException(nameof(templateStore));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _revisionStore = revisionStore ?? throw new ArgumentNullException(nameof(revisionStore));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _appendSelectedFiles = _settings.Context.AppendFilesToPrompt;

        CopyCommand = new AsyncRelayCommand(CopySelectedAsync, () => SelectedTemplate is not null, context: "プロンプトのコピー");
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

    /// <summary>
    /// 「選んだファイルも付ける」。オンのとき、指示文の後ろにコンテキスト収集で選んだファイルの内容も
    /// 付けて、1回のコピーで渡せるようにする（<c>{{files}}</c>を含むテンプレートでは、そこへファイルが
    /// 入るので二重には付かない。<see cref="PromptTemplateRenderer.RenderAsync"/>参照）。
    ///
    /// 【置き場所と既定】テンプレートごとの性質ではなく「今回のコピーでどこまで渡すか」の選択なので、
    /// テンプレートの本文（組み込み・利用者が編集したもの）には手を入れず、コピーの選択肢として
    /// ドロップダウンに置いた。既定はオフ（従来どおり指示文だけ）。利用者の習慣を急に変えないため。
    /// 保存先は設定（<c>context.appendFilesToPrompt</c>）で、チェックを切り替えるたびに
    /// <see cref="AppendSelectedFilesChangeCommitted"/>経由で常駐の設定ViewModelへ渡して保存する
    /// （設定画面の同名のチェックと同じ項目。次回起動時にも選択が効く）。
    /// </summary>
    public bool AppendSelectedFiles
    {
        get => _appendSelectedFiles;
        set
        {
            if (!SetProperty(ref _appendSelectedFiles, value)) return;
            AppendSelectedFilesChangeCommitted?.Invoke(this, value);
            RefreshEstimatesIfListed();
        }
    }

    /// <summary>
    /// 利用者が「選んだファイルも付ける」を切り替えたことの通知。設定への保存を依頼する
    /// （StartupCoordinatorが購読し、常駐のSettingsViewModel経由で保存する。
    /// ShellViewModel.DiffSideBySideChangeRequestedと同じ流儀）。設定側からの反映
    /// （<see cref="ApplySettings"/>）では発火しない（保存の往復で無限に呼び合わないため）。
    /// </summary>
    public event EventHandler<bool>? AppendSelectedFilesChangeCommitted;

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

    /// <summary>
    /// 設定が変わったとき（設定画面での変更・「選んだファイルも付ける」の保存確定）に呼ぶ。
    /// トークン概算比率などの最新値を取り込み、「選んだファイルも付ける」も設定に合わせる。
    /// <see cref="UpdateContext"/>と違い、一覧や選択中のテンプレートは捨てない。
    /// </summary>
    public void ApplySettings(Settings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (_appendSelectedFiles == settings.Context.AppendFilesToPrompt) return;

        // 設定側の値の反映なので、保存依頼のイベントは発火させない。
        _appendSelectedFiles = settings.Context.AppendFilesToPrompt;
        OnPropertyChanged(nameof(AppendSelectedFiles));
        RefreshEstimatesIfListed();
    }

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

    /// <summary>
    /// 一覧が既に出来ているとき、「選んだファイルも付ける」の切り替えで変わる推定トークン数を計算し直す。
    /// 選択中のテンプレートは変えない（切り替えのたびに既定の選択へ戻ってしまうと、選び直しが要る）。
    /// </summary>
    private void RefreshEstimatesIfListed()
    {
        if (Templates.Count > 0) _ = RefreshAsync(keepSelection: true);
    }

    /// <summary>テンプレート一覧を読み込み、各テンプレートの推定トークン数を計算し直す。</summary>
    /// <param name="keepSelection">true なら、選択中のテンプレートを（同じIdが残っていれば）選び直さず維持する。</param>
    /// <remarks>
    /// 【一覧を作り切ってから差し替える理由】「選んだファイルも付ける」の切り替えでも再計算するように
    /// なったため、ドロップダウンを開いた直後の読み込みと切り替えの再計算が重なりうる。以前のように
    /// 1件ずつ <c>Templates.Add</c> しながら進めると、2つの計算が同じ一覧へ交互に追加して
    /// 同じテンプレートが二重に並んでしまう。そこで世代番号を振り、手元の一覧を作り切ってから
    /// （最後に await を挟まず）1回で差し替える。後から始まった計算がある場合、古い計算の結果は捨てる。
    /// </remarks>
    private async Task RefreshAsync(bool keepSelection = false)
    {
        var generation = ++_refreshGeneration;
        var previousId = keepSelection ? SelectedTemplate?.Template.Id : null;
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

        var appendFiles = AppendSelectedFiles; // 計算の途中で切り替えられても、1回の計算では同じ値で通す。
        var options = new List<PromptTemplateOptionViewModel>();
        foreach (var template in loaded.Value)
        {
            var rendered = await _renderer.RenderAsync(template, request, lastRevision, appendFilesIfAbsent: appendFiles)
                .ConfigureAwait(true);
            var tokens = rendered.IsSuccess ? TokenEstimator.Estimate(rendered.Value, _settings.Context.TokenRatio) : 0;
            options.Add(new PromptTemplateOptionViewModel(template, tokens));
        }

        if (generation != _refreshGeneration) return; // より新しい計算が始まっている。そちらの結果を使う。

        Templates.Clear();
        foreach (var option in options) Templates.Add(option);

        // 4.8.1: 直近1時間以内にコピー済みなら継続用（短縮版）を既定表示にする。
        // keepSelection のときは、利用者が選んでいるテンプレートをそのまま残す。
        SelectedTemplate = (previousId is null ? null : Templates.FirstOrDefault(t => t.Template.Id == previousId))
            ?? Templates.FirstOrDefault(t => t.Template.IsContinuation == useContinuation)
            ?? Templates.FirstOrDefault();
    }

    private async Task CopySelectedAsync()
    {
        var template = SelectedTemplate;
        if (template is null) return;

        var request = BuildRequest();
        var lastRevision = await GetLastRevisionSummaryAsync().ConfigureAwait(true);
        var rendered = await _renderer.RenderAsync(
            template.Template, request, lastRevision, appendFilesIfAbsent: AppendSelectedFiles).ConfigureAwait(true);
        if (!rendered.IsSuccess)
        {
            StatusMessage = "コピーに失敗しました。";
            await _dialogs.ShowMessageAsync("コピーに失敗しました",
                string.Join(Environment.NewLine, rendered.Errors.Select(i => i.ToDisplayText()))).ConfigureAwait(true);
            return;
        }

        _ui.Clipboard.SetText(rendered.Value);
        _templateStore.RecordCopy(_project.Id, DateTimeOffset.Now);
        IsOpen = false;
        StatusMessage = $"「{template.Template.Name}」をコピーしました。";
        await _dialogs.ShowMessageAsync("プロンプトをコピーしました",
            $"「{template.Template.Name}」（約{template.EstimatedTokens}トークン）をクリップボードへコピーしました。").ConfigureAwait(true);
    }

    /// <summary>
    /// コンテキスト収集の選択状態から、テンプレート展開用の要求を組み立てる。
    ///
    /// 【SinceRevisionを渡す理由（以前の不具合）】ここで<see cref="ContextRequest.SinceRevision"/>を
    /// 渡していなかったため、収集モードが「差分のみ」のとき、{{files}}の展開
    /// （<see cref="ContextCollector"/>.ResolveTargetsAsyncは SinceRevision が null だと
    /// 対象を空にする）が常に空になり、指示文だけがコピーされていた。コンテキスト収集の窓の
    /// コピー（ContextCollectViewModel.CollectAsync）は同じ値を渡していたので、窓から
    /// コピーしたときだけ差分が出るという食い違いがあった。
    /// </summary>
    private ContextRequest BuildRequest() => new()
    {
        Project = _project,
        Settings = _settings,
        Mode = Context.SelectedMode,
        SinceRevision = Context.SelectedRevision?.Revision,
        SelectedPaths = Context.Files
            .Where(f => f is { IsDirectory: false, IsExcluded: false, State: ContextFileState.Full })
            .Select(f => f.RelativePath)
            .ToArray(),
        HiddenPaths = Context.Files
            .Where(f => f is { IsDirectory: false, IsExcluded: false, State: ContextFileState.Hidden })
            .Select(f => f.RelativePath)
            .ToArray(),
    };

    private async Task<string?> GetLastRevisionSummaryAsync()
    {
        var list = await _revisionStore.ListAsync(_project.Id).ConfigureAwait(true);
        return list.IsSuccess ? list.Value.FirstOrDefault()?.Manifest.Summary : null;
    }
}
