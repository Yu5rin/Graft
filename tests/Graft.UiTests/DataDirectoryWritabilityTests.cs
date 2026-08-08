using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.Platform.Null;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 課題1（バグ）: 書き込み権限の無いフォルダから起動しても何の警告も出ないまま
/// 起動してしまい、設定・履歴・バックアップの保存失敗が利用者に一切伝わらなかった
/// 不具合の回帰テスト。
///
/// 実際の書き込み可否判定は<see cref="AppPaths.CanWriteToBaseDirectory"/>で行う
/// （Graft.Tests側のAppPathsWritabilityTests参照）。ここでは、その結果を
/// StartupCoordinatorが<see cref="MainViewModel"/>へどう伝え、ステータスバーへ
/// 常時警告として出し続けられる状態になっているかを検証する
/// （起動時ダイアログは1回きりのため、以後の継続的な通知はこのフラグに懸かっている）。
/// </summary>
public class DataDirectoryWritabilityTests : IDisposable
{
    private readonly string _baseDirectory =
        Path.Combine(Path.GetTempPath(), "graft-ui-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_baseDirectory)) Directory.Delete(_baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    [AvaloniaFact(DisplayName = "書き込める場所で組み立てた既定状態では、書き込み不可の警告を出さない")]
    public void 既定では書き込み不可の警告を出さない()
    {
        var shell = BuildShell();

        shell.Graft.IsDataDirectoryReadOnly.Should().BeFalse(
            "書き込める場所で起動した場合、余計な警告をステータスバーへ出してはならない");
    }

    [AvaloniaFact(DisplayName = "MarkDataDirectoryReadOnlyを呼ぶと、ステータスバー用のフラグが立ち続ける")]
    public void 書き込み不可を伝えるとフラグが立つ()
    {
        var shell = BuildShell();

        // StartupCoordinator.StartAsyncが、起動時のCanWriteToBaseDirectory()確認結果に
        // 応じてこのメソッドを呼ぶ（StartupCoordinator.cs参照）。ダイアログは起動時に
        // 1回しか出ないため、この状態フラグがステータスバー（StatusBarView.axaml）に
        // 常時反映され続けることで「黙って保存に失敗し続ける」ことを防ぐ。
        shell.Graft.MarkDataDirectoryReadOnly();

        shell.Graft.IsDataDirectoryReadOnly.Should().BeTrue();

        // ShellWindowを実際に描画してもバインディング先の取り違えで落ちないことを確認する
        // （StatusBarView.axamlの新規追加分のバインディング検証を兼ねる）。
        var window = new ShellWindow(shell) { Width = 1280, Height = 800 };
        window.Show();
        using var frame = window.CaptureRenderedFrame();
        frame.Should().NotBeNull();
    }

    private ShellViewModel BuildShell()
    {
        var appPaths = new AppPaths(_baseDirectory);
        appPaths.EnsureCoreDirectoriesExist();

        IDialogService dialogs = new NullDialogService();
        IUiServices ui = new AvaloniaUiServices();

        return StartupCoordinator.BuildShellViewModel(
            appPaths,
            new Settings(),
            new SettingsStore(appPaths),
            new PatchQueue(appPaths),
            new ProjectStore(appPaths),
            new RevisionStore(appPaths),
            new RevisionRestorer(appPaths),
            dialogs,
            ui,
            openSettings: () => { });
    }
}
