using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 初回起動ガイド（仕様書8.12）。導入・プロジェクト登録・プロンプトのコピー・最終選択の4画面を
/// 順に案内し、完了・スキップのいずれでも表示済みフラグを書き出す。v2.0のWPF版からの移植
/// （19章 L3）。クリップボードへの書き込みはAvaloniaでは非同期APIのため、
/// <see cref="AvaloniaUiServices"/> 経由ではなくウィンドウ自身のクリップボードを使う。
///
/// 利用者からの指摘（「接ぎ木が体験できないので、ソフトの中核を体験できない」）への対応:
/// 最終画面（Screen4）は「使い方を学ぶ」「チュートリアルを終了」の2択にした。「使い方を学ぶ」を
/// 選ぶと、このガイド自体は（表示済みフラグを書き出して）終了しつつ、<see cref="StartTutorialRequested"/>
/// を立てて閉じる。実際のシェル画面上でコントロールを指しながら接ぎ木を体験させる画面上
/// チュートリアル本体は<c>Graft.Views.ShellWindow</c>側（ShellWindow.Tutorial.cs）が担う。
/// このウィンドウはシェルの実際のコントロール（GraftPanel・HistoryPane等）を一切知らない
/// ため、ここでは「学ぶことを選んだ」という意思表示（bool 1つ）を返すだけに留める
/// （StartupCoordinator.StartAsyncがShowDialog後にこれを見て、シェル側のチュートリアルを開始する）。
///
/// スキップ（どの画面からでも押せるSkipButton）・Escは、当然この最終選択も経由せずそのまま
/// ガイドを終了する（＝チュートリアルも開始しない。OnSkipClicked/OnTunnelKeyDownは従来どおり
/// StartTutorialRequestedをfalseのままFinishAsyncを呼ぶ）。
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int LastStepIndex = 3;

    private readonly AppPaths _appPaths;
    private readonly ProjectStore _projectStore;
    private readonly ProjectPaneViewModel? _projectPane;
    private readonly IDialogService _dialogService;
    private int _step;

    /// <summary>
    /// 最終画面で「使い方を学ぶ」が選ばれたかどうか。StartupCoordinator.StartAsyncが
    /// <see cref="Window.ShowDialog(Window)"/>の完了後にこれを見て、シェル側の画面上
    /// チュートリアルを開始するかどうかを判断する。既定はfalse
    /// （スキップ・「チュートリアルを終了」・Escのいずれでもfalseのまま）。
    /// </summary>
    public bool StartTutorialRequested { get; private set; }

    /// <summary>headlessテスト・デザイナ用の引数なしコンストラクタ。プロジェクト登録はできるが、
    /// シェル側の一覧とは連携しない（<see cref="_projectPane"/> がnullのため）。</summary>
    public OnboardingWindow() : this(new AppPaths())
    {
    }

    /// <summary>
    /// <paramref name="appPaths"/> のみを指定するコンストラクタ。プロジェクト登録はできるが、
    /// シェル側の一覧とは連携しない（テストで基準ディレクトリだけ差し替えたい場合に使う）。
    /// </summary>
    public OnboardingWindow(AppPaths appPaths)
    {
        InitializeComponent();
        _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
        _projectStore = new ProjectStore(_appPaths);
        _dialogService = new AvaloniaDialogService();

        TemplatePreviewText.Text = PromptTemplateStore.BuiltIns.First(t => t.Id == "builtin-full").Body;
        UpdateStepUi();
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        // 細かいユーザビリティ改善5: 開いた直後の初期フォーカスを既定ボタン（次へ）へ当てる。
        // NextButtonは既にIsDefault="True"（axaml）のためEnterキーはこれが無くても効くが、
        // 開いた直後にどこにもフォーカスが無い状態を避けるために明示する。画面が切り替わっても
        // （UpdateStepUi）NextButton自体は常に存在するため、ステップごとに配線し直す必要は無い。
        Loaded += (_, _) => NextButton.Focus();
    }

    /// <summary>
    /// 実際の起動経路（StartupCoordinator）から使うコンストラクタ。<paramref name="appPaths"/> は
    /// StartupCoordinatorが保持しているのと同じインスタンス、<paramref name="projectPane"/> には
    /// シェルの左ペイン・上部ドロップダウンが参照しているのと同じ <see cref="ProjectPaneViewModel"/>
    /// インスタンスを渡す。
    /// バグ修正: 従来は本ウィンドウが独自の<see cref="ProjectStore"/>でprojects.jsonへ直接書き込む
    /// だけだったため、登録自体は成功してもシェル側が保持する一覧（起動時に読み込み済み）には
    /// 反映されず、チュートリアルを閉じてもプロジェクトが現れなかった（再起動すると現れる＝
    /// ディスク上のprojects.jsonは正しいが、メモリ上の一覧が古いままという状態）。
    /// 同じインスタンスを介して<see cref="ProjectPaneViewModel.RegisterFolderAsync"/>を呼ぶことで、
    /// 登録・一覧再読み込み・新規プロジェクトの選択までが、シェルの一覧・ドロップダウンが
    /// バインドしているコレクションそのものに対して行われるようにする。
    /// </summary>
    public OnboardingWindow(AppPaths appPaths, ProjectPaneViewModel projectPane) : this(appPaths)
    {
        _projectPane = projectPane ?? throw new ArgumentNullException(nameof(projectPane));
    }

    /// <summary>
    /// テスト向けコンストラクタ。フォルダ選択ダイアログはOSのネイティブダイアログを開くため
    /// headlessテストから駆動できない。<paramref name="dialogService"/> にフェイク実装を渡すことで、
    /// 「フォルダを選択して登録」ボタンの実クリックからシェルの一覧・ドロップダウンへの反映までを
    /// end-to-endで検証できるようにする（本番経路は<see cref="AvaloniaDialogService"/>を使う）。
    /// </summary>
    public OnboardingWindow(AppPaths appPaths, ProjectPaneViewModel projectPane, IDialogService dialogService)
        : this(appPaths, projectPane)
    {
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    /// <summary>
    /// <see cref="AppPaths.BaseDirectory"/> 直下の表示済みフラグファイルの絶対パス。
    /// 不具合2の修正: ファイル名は<see cref="AppPaths.OnboardingMarkerFilePath"/>を単一の情報源
    /// として使う（<see cref="Infra.DataDirectoryMigrator"/>のコピー対象一覧と食い違わないように
    /// するため。詳細はそちらのコメント参照）。
    /// </summary>
    public static string GetMarkerFilePath(AppPaths appPaths) => appPaths.OnboardingMarkerFilePath;

    /// <summary>表示済みフラグの有無を返す。アプリ起動時にこれを見て初回起動ガイドの要否を判断する。</summary>
    public static bool HasCompleted(AppPaths appPaths) => File.Exists(GetMarkerFilePath(appPaths));

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // スキップと同じ扱い: Escで閉じた場合も、学ぶ画面（最終選択）を経由せずそのまま
        // ガイドを終了し、チュートリアルは開始しない。
        if (e.Key == Key.Escape)
        {
            StartTutorialRequested = false;
            _ = FinishAsync();
        }
    }

    private void OnNextClicked(object? sender, RoutedEventArgs e)
    {
        if (_step >= LastStepIndex)
        {
            _ = FinishAsync();
            return;
        }

        _step++;
        UpdateStepUi();
    }

    private void OnBackClicked(object? sender, RoutedEventArgs e)
    {
        if (_step <= 0) return;

        _step--;
        UpdateStepUi();
    }

    /// <summary>
    /// スキップは当然チュートリアルの終了として扱う。どの画面からスキップしても、
    /// 最終選択画面（学ぶ／終了の2択）を経由せずそのままガイドを終了する。
    /// </summary>
    private void OnSkipClicked(object? sender, RoutedEventArgs e)
    {
        StartTutorialRequested = false;
        _ = FinishAsync();
    }

    private async void OnRegisterProjectClicked(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("プロジェクトの登録", async () =>
        {
            var folder = await _dialogService.PickFolderAsync("登録するプロジェクトのフォルダを選択してください")
                .ConfigureAwait(true);
            if (folder is null) return;

            // _projectPaneが渡されているとき（実際の起動経路）はシェルと同じインスタンスを介して
            // 登録し、一覧・ドロップダウンへ即座に反映させる。渡されていないとき（headlessテスト・
            // デザイナ用の引数なしコンストラクタ）は従来どおり登録のみ行う。
            var result = _projectPane is not null
                ? await _projectPane.RegisterFolderAsync(folder).ConfigureAwait(true)
                : await _projectStore.RegisterAsync(folder, null).ConfigureAwait(true);
            ProjectResultText.Text = result.IsSuccess
                ? $"「{result.Value.Name}」を登録しました（{result.Value.Root}）。"
                : "プロジェクトの登録に失敗しました。";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 最終画面「使い方を学ぶ」。<see cref="StartTutorialRequested"/>を立ててからガイドを終了する
    /// （表示済みフラグの書き出し・ウィンドウを閉じる処理自体は<see cref="OnFinishOnboardingClicked"/>
    /// と共有する<see cref="FinishAsync"/>を使う）。実際のチュートリアル本体は、このウィンドウが
    /// 閉じた後にStartupCoordinator.StartAsyncがShellWindow側で開始する（クラスコメント参照）。
    /// </summary>
    private async void OnStartTutorialClicked(object? sender, RoutedEventArgs e)
    {
        StartTutorialRequested = true;
        await FinishAsync().ConfigureAwait(true);
    }

    /// <summary>最終画面「チュートリアルを終了」。学ぶ画面は開始せず、そのままガイドを終了する。</summary>
    private async void OnFinishOnboardingClicked(object? sender, RoutedEventArgs e)
    {
        StartTutorialRequested = false;
        await FinishAsync().ConfigureAwait(true);
    }

    private async void OnCopyTemplateClicked(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("プロンプトのコピー", async () =>
        {
            if (Clipboard is null) return;

            await Clipboard.SetTextAsync(TemplatePreviewText.Text ?? string.Empty).ConfigureAwait(true);
            CopyResultText.Text = "クリップボードにコピーしました。";
        }).ConfigureAwait(true);
    }

    private void UpdateStepUi()
    {
        Screen1.IsVisible = _step == 0;
        Screen2.IsVisible = _step == 1;
        Screen3.IsVisible = _step == 2;
        Screen4.IsVisible = _step == 3;

        BackButton.IsEnabled = _step > 0;
        // 最終画面（Screen4）は下段のNext/Skipではなく、画面内の2択（「使い方を学ぶ」/
        // 「チュートリアルを終了」）で完結させる。NextButtonを隠すだけでなくIsDefaultも
        // 外し、Enterキーが画面内の「使い方を学ぶ」（Screen4側でIsDefault="True"）へ
        // 届くようにする（2つのIsDefaultが同時に有効だと曖昧になるため）。
        var isFinalScreen = _step >= LastStepIndex;
        NextButton.IsVisible = !isFinalScreen;
        NextButton.IsDefault = !isFinalScreen;
        SkipButton.IsVisible = !isFinalScreen;
        StepIndicatorText.Text = $"{_step + 1} / {LastStepIndex + 1}";
    }

    private async Task FinishAsync()
    {
        try
        {
            Directory.CreateDirectory(_appPaths.BaseDirectory);
            await File.WriteAllTextAsync(GetMarkerFilePath(_appPaths), string.Empty).ConfigureAwait(true);
        }
        catch (IOException)
        {
            // マーカーファイルの作成に失敗しても、ガイド自体は閉じられる必要があるため
            // ここでは無視する（次回起動時に再度ガイドが表示されるだけで実害は小さい）。
        }

        Close();
    }
}
