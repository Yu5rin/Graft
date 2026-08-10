using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 細かいユーザビリティ改善6: 初回起動ガイドの「サンプルで試す」ボタン。
/// 実データを一切触らずに、登録→貼り付け→適用→履歴確認の流れを体験できるようにする要件のうち、
/// 「登録」（プロジェクト一覧への反映）と「貼り付け」（クリップボードへのコピー）まで、
/// および「サンプルを削除」による後片付け導線を検証する。「適用」「履歴確認」自体は
/// 既存のGraftの画面操作そのもの（あえて自動化していない設計。OnboardingWindow.axaml.cs
/// OnTryOnboardingSampleClicked参照）であり、それらは他のシナリオテスト
/// （ScenarioTests・RestoreThroughTests等）で既に広く検証されている。
/// </summary>
public class OnboardingSampleButtonTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-onboarding-sample-tests", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();

        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果と無関係のため無視する。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "「サンプルで試す」で一時フォルダにサンプルが生成され、プロジェクト一覧へ反映され、パッチがクリップボードへコピーされる")]
    public async Task サンプルで試すとプロジェクト登録とクリップボードコピーが行われる()
    {
        var shell = BuildShell();
        var shellWindow = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        shellWindow.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(shellWindow);

        var onboarding = _windows.Track(new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane));
        onboarding.Show();

        RaiseClick(onboarding, "次へ"); // 画面1 → 画面2（プロジェクト登録）
        await SettleAsync().ConfigureAwait(true);

        RaiseClick(onboarding, "サンプルで試す");
        await SettleAsync().ConfigureAwait(true);

        // 要件: 実データを一切触らずに登録できること（一時フォルダ配下に生成されること）。
        shell.Graft.ProjectPane.Items.Should().ContainSingle();
        var registered = shell.Graft.ProjectPane.Items.Single();
        Path.GetFullPath(registered.Project.Root).Should()
            .StartWith(Path.GetFullPath(Path.GetTempPath()), "生成先は一時フォルダである必要がある（利用者のドキュメント等を汚さない）");
        Directory.Exists(registered.Project.Root).Should().BeTrue();
        File.Exists(Path.Combine(registered.Project.Root, "greeting.py")).Should().BeTrue();

        // 要件: 「貼り付け」まで体験できるよう、サンプルパッチがクリップボードにコピーされていること。
        var clipboardText = await onboarding.Clipboard!.GetTextAsync().ConfigureAwait(true);
        clipboardText.Should().NotBeNullOrEmpty();
        clipboardText.Should().Contain("<<<< PATCH");
        clipboardText.Should().Contain("<<<< FILE: greeting.py");

        // 「サンプルを削除」ボタンが使えるようになっていること。
        var deleteButton = onboarding.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "サンプルを削除"));
        deleteButton.IsVisible.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「サンプルを削除」でプロジェクト一覧・一時フォルダの両方から片付けられる")]
    public async Task サンプルを削除で一覧とディスクの両方から消える()
    {
        var shell = BuildShell();
        var shellWindow = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        shellWindow.Show();
        ShellWindowLoadWaiter.WaitForLayoutApplied(shellWindow);

        var onboarding = _windows.Track(new OnboardingWindow(new AppPaths(_baseDirectory), shell.Graft.ProjectPane));
        onboarding.Show();

        RaiseClick(onboarding, "次へ");
        await SettleAsync().ConfigureAwait(true);
        RaiseClick(onboarding, "サンプルで試す");
        await SettleAsync().ConfigureAwait(true);

        var sampleRoot = shell.Graft.ProjectPane.Items.Single().Project.Root;
        Directory.Exists(sampleRoot).Should().BeTrue();

        RaiseClick(onboarding, "サンプルを削除");
        await SettleAsync().ConfigureAwait(true);

        shell.Graft.ProjectPane.Items.Should().BeEmpty("削除後はプロジェクト一覧からも取り除かれる必要がある");
        Directory.Exists(sampleRoot).Should().BeFalse("削除後は一時フォルダも消えている必要がある");

        var deleteButton = onboarding.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, "サンプルを削除"));
        deleteButton.IsVisible.Should().BeFalse("削除済みなら再度押せないよう隠れる必要がある");
    }

    private static void RaiseClick(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, content));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(10);
        }
    }

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        IDialogService dialogs = new Graft.Platform.Null.NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Graft.Infra.Settings(),
            new SettingsStore(appPaths),
            new Graft.Features.PatchQueue(appPaths),
            new Graft.Features.ProjectStore(appPaths),
            new Graft.Core.RevisionStore(appPaths),
            new Graft.Core.RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }
}
