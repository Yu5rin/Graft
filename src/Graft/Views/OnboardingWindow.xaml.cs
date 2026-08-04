using System.IO;
using System.Windows;
using System.Windows.Input;
using Graft.Features;
using Graft.Infra;

namespace Graft.Views;

/// <summary>
/// 8.15章の初回起動ガイド。プロジェクトの登録とプロンプトテンプレートのコピーへ誘導する
/// 3画面構成のウィザード。スキップ可能で、一度閉じると以降は表示しない。表示済みフラグは
/// <see cref="AppPaths.BaseDirectory"/> 直下の <c>onboarding.done</c>（空ファイル）で管理する。
/// 依存はコンストラクタ引数を持たず（呼び出し側の公開APIが引数無しのため）、内部で
/// <see cref="AppPaths"/>・<see cref="ProjectStore"/>・<see cref="DialogService"/> を組み立てる。
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int LastStepIndex = 2;

    private readonly AppPaths _appPaths;
    private readonly ProjectStore _projectStore;
    private readonly DialogService _dialogService = new();
    private int _step;

    public OnboardingWindow()
    {
        InitializeComponent();
        _appPaths = new AppPaths();
        _projectStore = new ProjectStore(_appPaths);

        TemplatePreviewText.Text = PromptTemplateStore.BuiltIns.First(t => t.Id == "builtin-full").Body;
        UpdateStepUi();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary><see cref="AppPaths.BaseDirectory"/> 直下の表示済みフラグファイルの絶対パス。</summary>
    public static string GetMarkerFilePath(AppPaths appPaths) => Path.Combine(appPaths.BaseDirectory, "onboarding.done");

    /// <summary>表示済みフラグの有無を返す。アプリ起動時にこれを見て初回起動ガイドの要否を判断する。</summary>
    public static bool HasCompleted(AppPaths appPaths) => File.Exists(GetMarkerFilePath(appPaths));

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _ = FinishAsync();
        }
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_step >= LastStepIndex)
        {
            _ = FinishAsync();
            return;
        }

        _step++;
        UpdateStepUi();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_step <= 0)
        {
            return;
        }

        _step--;
        UpdateStepUi();
    }

    private void OnSkipClicked(object sender, RoutedEventArgs e) => _ = FinishAsync();

    private async void OnRegisterProjectClicked(object sender, RoutedEventArgs e)
    {
        var folder = _dialogService.PickFolder("登録するプロジェクトのフォルダを選択してください");
        if (folder is null)
        {
            return;
        }

        var result = await _projectStore.RegisterAsync(folder, null).ConfigureAwait(true);
        ProjectResultText.Text = result.IsSuccess
            ? $"「{result.Value.Name}」を登録しました（{result.Value.Root}）。"
            : "プロジェクトの登録に失敗しました。";
    }

    private void OnCopyTemplateClicked(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(TemplatePreviewText.Text);
        CopyResultText.Text = "クリップボードにコピーしました。";
    }

    private void UpdateStepUi()
    {
        Screen1.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Screen2.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Screen3.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

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
