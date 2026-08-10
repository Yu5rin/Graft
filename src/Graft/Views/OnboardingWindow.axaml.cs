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

    // 細かいユーザビリティ改善6: 「サンプルで試す」で生成した一時プロジェクトのルート。
    // 「サンプルを削除」ボタンの有効化・削除対象の特定に使う。未生成の間はnull。
    private string? _sampleProjectRoot;

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

    /// <summary>
    /// 細かいユーザビリティ改善6: 「サンプルで試す」。実データを一切触らずに、
    /// 登録→貼り付け→適用→履歴確認の流れを1回体験できるようにする。<see cref="OnboardingSample"/>が
    /// 一時フォルダへサンプルプロジェクト（1ファイル）とサンプルパッチを生成し、ここではそれを
    /// （フォルダ選択登録と同じ経路で）登録し、パッチ本文をクリップボードへコピーするところまでを
    /// 行う。そこから先（貼り付け・解析・適用・履歴確認）は、実際のGraftの画面操作をそのまま
    /// 体験してもらうため、あえて自動化しない。
    /// </summary>
    private async void OnTryOnboardingSampleClicked(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("サンプルの生成", async () =>
        {
            var sample = OnboardingSample.Create();

            // 登録の経路はOnRegisterProjectClickedと同じ（_projectPaneがあればシェルの一覧・
            // ドロップダウンへ即座に反映、無ければ従来どおり登録のみ）。
            var result = _projectPane is not null
                ? await _projectPane.RegisterFolderAsync(sample.ProjectRoot).ConfigureAwait(true)
                : await _projectStore.RegisterAsync(sample.ProjectRoot, null).ConfigureAwait(true);

            if (!result.IsSuccess)
            {
                OnboardingSample.Cleanup(sample.ProjectRoot);
                ProjectResultText.Text = "サンプルの登録に失敗しました。";
                return;
            }

            _sampleProjectRoot = sample.ProjectRoot;
            DeleteSampleButton.IsVisible = true;

            if (Clipboard is not null)
            {
                await Clipboard.SetTextAsync(sample.PatchText).ConfigureAwait(true);
            }

            ProjectResultText.Text =
                $"サンプルプロジェクト「{result.Value.Name}」を登録し、サンプルパッチをクリップボードへ" +
                $"コピーしました（生成先: {sample.ProjectRoot}）。ツールバーの「解析」→「適用」を押すと、" +
                "greeting.pyへの変更を体験できます。適用後は履歴タブでも確認できます。一時フォルダに" +
                "生成したものなので、あとで削除して構いません（下の「サンプルを削除」でもまとめて消せます）。";
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 細かいユーザビリティ改善6: 体験後にサンプルを片付ける導線。プロジェクト一覧からも取り除く
    /// （登録したまま一時フォルダだけ消すと、存在しないフォルダを指すプロジェクトが一覧に残って
    /// しまうため）。
    /// </summary>
    private async void OnDeleteSampleClicked(object? sender, RoutedEventArgs e)
    {
        await SafeHandler.RunAsync("サンプルの削除", async () =>
        {
            if (_sampleProjectRoot is not { } root) return;

            var projects = await _projectStore.LoadAsync().ConfigureAwait(true);
            if (projects.IsSuccess)
            {
                var match = projects.Value.FirstOrDefault(p => PathsEqual(p.Root, root));
                if (match is not null)
                {
                    await _projectStore.RemoveAsync(match.Id, deleteHistory: true).ConfigureAwait(true);
                    // _projectStoreへ直接書き込んだだけでは、シェルが保持する一覧（メモリ上）には
                    // 反映されない（OnRegisterProjectClickedのコメントと同じ理由）。_projectPaneが
                    // あれば再読み込みして同期する。
                    if (_projectPane is not null) await _projectPane.LoadAsync().ConfigureAwait(true);
                }
            }

            OnboardingSample.Cleanup(root);
            _sampleProjectRoot = null;
            DeleteSampleButton.IsVisible = false;
            ProjectResultText.Text = "サンプルを削除しました。";
        }).ConfigureAwait(true);
    }

    private static bool PathsEqual(string a, string b) => OperatingSystem.IsWindows()
        ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        : string.Equals(a, b, StringComparison.Ordinal);

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
