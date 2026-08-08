using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.Views;

/// <summary>
/// 初回起動ガイド（仕様書8.12）。導入・プロジェクト登録・プロンプトのコピーの3画面を順に案内し、
/// 完了・スキップのいずれでも表示済みフラグを書き出す。v2.0のWPF版からの移植（19章 L3）。
/// クリップボードへの書き込みはAvaloniaでは非同期APIのため、
/// <see cref="AvaloniaUiServices"/> 経由ではなくウィンドウ自身のクリップボードを使う。
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int LastStepIndex = 2;

    private readonly AppPaths _appPaths;
    private readonly ProjectStore _projectStore;
    private readonly ProjectPaneViewModel? _projectPane;
    private readonly IDialogService _dialogService;
    private int _step;

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

    /// <summary><see cref="AppPaths.BaseDirectory"/> 直下の表示済みフラグファイルの絶対パス。</summary>
    public static string GetMarkerFilePath(AppPaths appPaths) => Path.Combine(appPaths.BaseDirectory, "onboarding.done");

    /// <summary>表示済みフラグの有無を返す。アプリ起動時にこれを見て初回起動ガイドの要否を判断する。</summary>
    public static bool HasCompleted(AppPaths appPaths) => File.Exists(GetMarkerFilePath(appPaths));

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) _ = FinishAsync();
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

    private void OnSkipClicked(object? sender, RoutedEventArgs e) => _ = FinishAsync();

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

        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step >= LastStepIndex ? "完了" : "次へ";
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
