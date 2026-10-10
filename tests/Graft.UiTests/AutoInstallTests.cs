using System.IO.Compression;
using System.Security.Cryptography;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using Graft.Core;
using Graft.Core.Update;
using Graft.Features;
using Graft.Infra;
using Graft.Platform;
using Graft.UiTests.TestSupport;
using Graft.ViewModels;
using Graft.Views;

namespace Graft.UiTests;

/// <summary>
/// 「確認せずに自動で更新する」（<see cref="SettingsViewModel.UpdateAutoInstall"/>、1.0.25）の
/// 振る舞いの固定。起動時の確認で新しい版が見つかったとき、
/// <list type="bullet">
/// <item>オンで、SHA256を照合できるときは、ダイアログを出さずにダウンロード・検証・入れ替えまで進み、
/// 再起動は要求しないこと（完了はステータスバーの通知で知らせる）。</item>
/// <item>SHA256を照合できない（回数上限の経路）、OSやフォルダの都合で入れ替えられない場合は、
/// 従来の確認ダイアログへ戻り、ダウンロードも入れ替えも始めないこと。</item>
/// <item>オフなら従来どおりであること。手動の「今すぐ更新を確認」は、オンでも必ずダイアログで確認すること。</item>
/// <item>失敗したらいまの版のまま続き、ログに理由が残り、通知は同じ版につき1回だけであること。</item>
/// </list>
/// 入れ替えの実処理は本物（<see cref="UpdateInstallPipeline"/>・<see cref="SelfUpdateInstaller"/>）を
/// 一時フォルダに対して通す。通信だけをフェイクに差し替え、入れ替え先は
/// <see cref="SettingsViewModel.InstallDirectoryResolver"/>で一時フォルダへ向ける
/// （テストホスト自身のフォルダを書き換えないため）。
/// </summary>
public class AutoInstallTests : IDisposable
{
    private const string NewTag = "v99.0.0";
    private const string DownloadHost = "https://github.com/Yu5rin/Graft/releases/download/";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-auto-install", Guid.NewGuid().ToString("N"));

    private readonly ShownWindowTracker _windows = new();

    public void Dispose()
    {
        _windows.Dispose();
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は検証結果に影響しない。
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // 自動で進む（照合できる・書き込める・Windows）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "オンで照合できるときは、ダイアログを出さずに入れ替えまで進み、再起動は要求しない")]
    public async Task オンで照合できればダイアログなしで入れ替える()
    {
        var h = await CreateAsync();

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.Titles.Should().BeEmpty("確認なしで進めるので、ダイアログは一切出さない");
        h.Downloader.CallCount.Should().Be(1);
        // 入れ替えの実処理を本当に通った: 新しいファイルが置かれ、実行中のファイルは退避名へ移っている。
        File.ReadAllText(Path.Combine(h.InstallDir, "Graft.exe")).Should().Be("new-Graft.exe");
        File.ReadAllText(Path.Combine(h.InstallDir, "Graft.exe.old")).Should().Be("old-Graft.exe");
        h.Restarts.Should().BeEmpty("勝手に再起動してはならない");
        h.Vm.IsUpdateBusy.Should().BeFalse();

        var notice = h.Vm.CurrentAutoUpdateNotice;
        notice.Should().NotBeNull();
        notice!.Kind.Should().Be(AutoUpdateNoticeKind.Installed);
        notice.Text.Should().Be("Graft を 99.0.0 に更新しました。次回の起動から新しい版になります");
        h.Notices.Should().ContainSingle().Which.Should().BeSameAs(notice);

        var lines = await h.ReadLogLinesAsync();
        lines.Should().Contain(l => l.Contains("自動更新") && l.Contains("ダウンロードと入れ替えを始めます"));
        lines.Should().Contain(l => l.Contains("自動更新") && l.Contains("入れ替えが完了しました") && l.Contains(NewTag));
    }

    [AvaloniaFact(DisplayName = "オンの起動時の確認でも、新しい版が見つかったことを示す従来のログは残る")]
    public async Task 従来のログも残る()
    {
        var h = await CreateAsync();

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        var lines = await h.ReadLogLinesAsync();
        lines.Should().Contain(l => l.Contains("起動時の更新確認") && l.Contains("新しいバージョンが見つかりました"));
    }

    [AvaloniaFact(DisplayName = "完了通知の「今すぐ再起動」は、未保存の確認を通してから、専用の理由で再起動を要求する")]
    public async Task 完了通知から再起動できる()
    {
        var h = await CreateAsync();
        var confirmed = 0;
        h.Vm.ConfirmUnsavedDocumentsAsync = () =>
        {
            confirmed++;
            return Task.FromResult(true);
        };
        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        h.Restarts.Should().BeEmpty();

        await h.Vm.RestartForInstalledUpdateAsync();

        confirmed.Should().Be(1, "再起動の前に未保存の編集を確認する既存の流れを通る");
        h.Restarts.Should().Equal(RestartReason.AutoUpdateNotice);
        h.Dialogs.Titles.Should().BeEmpty("ボタンを押したこと自体が意思表示なので、再起動の確認ダイアログを重ねない");
    }

    [AvaloniaFact(DisplayName = "未保存の確認でキャンセルされたら再起動せず、通知は残る")]
    public async Task 未保存の確認でキャンセルすれば再起動しない()
    {
        var h = await CreateAsync();
        h.Vm.ConfirmUnsavedDocumentsAsync = () => Task.FromResult(false);
        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        await h.Vm.RestartForInstalledUpdateAsync();

        h.Restarts.Should().BeEmpty();
        h.Vm.CurrentAutoUpdateNotice.Should().NotBeNull("もう一度押せるよう通知は残す");
    }

    [AvaloniaFact(DisplayName = "通知を閉じると消える（消えたことも橋渡し用のイベントで伝わる）")]
    public async Task 通知を閉じられる()
    {
        var h = await CreateAsync();
        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Vm.DismissAutoUpdateNotice();

        h.Vm.CurrentAutoUpdateNotice.Should().BeNull();
        h.Notices.Should().HaveCount(2);
        h.Notices[^1].Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "入れ替え済みの版を手動で確認し直しても、もう一度ダウンロードせず再起動の案内へ進む")]
    public async Task 入れ替え済みの版は再ダウンロードしない()
    {
        var h = await CreateAsync();
        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        h.Downloader.CallCount.Should().Be(1);

        h.Vm.CheckForUpdateNowCommand.Execute(null);
        await WaitUntilAsync(() => !h.Vm.IsUpdateBusy);

        h.Downloader.CallCount.Should().Be(1, "ファイルはすでに新しい版。実行中の版が古いだけで「新しい」と判定され続ける");
        h.Dialogs.Titles.Should().Contain("更新の準備ができました");
        h.Dialogs.ActionLabels.Should().Equal("今すぐ再起動");
    }

    // ------------------------------------------------------------------
    // 従来のダイアログへ戻す
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "SHA256を照合できない経路（AllowMissingChecksum）では、従来のダイアログへ戻り、ダウンロードも入れ替えもしない")]
    public async Task 照合できなければダイアログへ戻る()
    {
        var h = await CreateAsync(allowMissingChecksum: true);

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Dialogs.Messages.Should().ContainSingle().Which.Should().Contain("SHA256", "従来どおり、照合を省くことをダイアログで開示する");
        h.Downloader.CallCount.Should().Be(0, "利用者の同意なしに、照合を省いたままダウンロードしてはならない");
        h.AssertInstallDirUntouched();
        h.Vm.CurrentAutoUpdateNotice.Should().BeNull();
        h.Restarts.Should().BeEmpty();

        var lines = await h.ReadLogLinesAsync();
        lines.Should().Contain(l => l.Contains("自動更新") && l.Contains("SHA256を照合できない") && l.Contains("従来どおり確認ダイアログ"));
    }

    [AvaloniaFact(DisplayName = "配布物のdigestが無いときも、ダウンロードする前に従来のダイアログへ戻る")]
    public async Task digestが無ければダイアログへ戻る()
    {
        var h = await CreateAsync(digestOverride: "");

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Downloader.CallCount.Should().Be(0);
        h.AssertInstallDirUntouched();
    }

    [AvaloniaFact(DisplayName = "自分で入れ替えられないOS（Linux）では、従来のリリースページの案内に戻る")]
    public async Task Linuxでは案内に戻る()
    {
        var h = await CreateAsync(platform: UpdatePlatform.Linux);

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("リリースページを開く");
        h.Downloader.CallCount.Should().Be(0);
        h.AssertInstallDirUntouched();
    }

    [AvaloniaFact(DisplayName = "実行ファイルの場所を特定できないときは、従来のダイアログへ戻る")]
    public async Task 場所を特定できなければダイアログへ戻る()
    {
        var h = await CreateAsync(installDirResolvesToNull: true);

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Downloader.CallCount.Should().Be(0);
        var lines = await h.ReadLogLinesAsync();
        lines.Should().Contain(l => l.Contains("自動更新") && l.Contains("実行ファイルの場所を特定できない") && l.Contains("従来どおり確認ダイアログ"));
    }

    [AvaloniaFact(DisplayName = "更新の確認先が既定から変更されているときは、確認ダイアログ（変更の警告つき）へ戻る")]
    public async Task 確認先が変更されていればダイアログへ戻る()
    {
        var h = await CreateAsync();
        h.Vm.UpdateCheckUrl = "https://example.com/releases/latest";

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Dialogs.Messages.Should().ContainSingle().Which.Should().Contain("既定から変更されています");
        h.Downloader.CallCount.Should().Be(0);
    }

    [AvaloniaFact(DisplayName = "オフ（既定）なら従来どおりダイアログで確認し、同意するまでダウンロードしない")]
    public async Task オフなら従来どおり()
    {
        var h = await CreateAsync(autoInstall: false);

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Downloader.CallCount.Should().Be(0);
        h.AssertInstallDirUntouched();
        h.Vm.CurrentAutoUpdateNotice.Should().BeNull();
        var lines = await h.ReadLogLinesAsync();
        lines.Should().NotContain(l => l.Contains("自動更新:"), "オフなら自動更新のログは出さない");
    }

    [AvaloniaFact(DisplayName = "手動の「今すぐ更新を確認」は、オンでも必ずダイアログで確認する")]
    public async Task 手動確認はオンでもダイアログ()
    {
        var h = await CreateAsync();

        h.Vm.CheckForUpdateNowCommand.Execute(null);
        await WaitUntilAsync(() => !h.Vm.IsUpdateBusy);

        h.Dialogs.ActionLabels.Should().Equal("今すぐ更新");
        h.Downloader.CallCount.Should().Be(0, "利用者が自分で押した操作。同意なしにダウンロードしない");
        h.AssertInstallDirUntouched();
        h.Vm.CurrentAutoUpdateNotice.Should().BeNull();
    }

    [AvaloniaFact(DisplayName = "「起動時に更新を確認する」がオフなら、AutoInstallがオンでも確認自体をしない")]
    public async Task 起動時の確認がオフなら何もしない()
    {
        var h = await CreateAsync();
        h.Vm.UpdateCheckOnStartup = false;

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Feed.CallCount.Should().Be(0);
        h.Dialogs.Titles.Should().BeEmpty();
        h.Downloader.CallCount.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // 失敗したとき
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ダウンロードに失敗したら、いまの版のまま続け、ログに理由を残し、控えめな通知だけを出す")]
    public async Task 失敗したらいまの版のまま続ける()
    {
        var h = await CreateAsync(downloadFails: true);

        await h.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        h.Dialogs.Titles.Should().BeEmpty("裏の失敗でダイアログを出して作業を止めない");
        h.AssertInstallDirUntouched();
        h.Restarts.Should().BeEmpty();
        h.Vm.IsUpdateBusy.Should().BeFalse();

        var notice = h.Vm.CurrentAutoUpdateNotice;
        notice.Should().NotBeNull();
        notice!.Kind.Should().Be(AutoUpdateNoticeKind.Failed);
        notice.Text.Should().Be("自動更新に失敗しました。設定の「今すぐ更新を確認」から手動で更新できます");

        var lines = await h.ReadLogLinesAsync();
        lines.Should().Contain(l => l.Contains("自動更新") && l.Contains("入れ替えに失敗しました") && l.Contains("いまの版のまま") && l.Contains(NewTag));
    }

    [AvaloniaFact(DisplayName = "同じ版の失敗は、次の起動でまた失敗しても通知を出さない（ログには残す）。次の版の失敗は改めて通知する")]
    public async Task 同じ失敗を毎回通知しない()
    {
        var appPaths = NewAppPaths();

        var first = await CreateAsync(downloadFails: true, appPaths: appPaths);
        await first.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        first.Notices.Should().ContainSingle("初回は通知する");
        await first.ReadLogLinesAsync();

        // 次の起動（同じデータ保存先・同じ版・また失敗）。
        var second = await CreateAsync(downloadFails: true, appPaths: appPaths);
        await second.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        second.Notices.Should().BeEmpty("同じ版の失敗は通知済み");
        second.Vm.CurrentAutoUpdateNotice.Should().BeNull();
        second.Downloader.CallCount.Should().Be(1, "通知は絞るが、更新自体は毎回試みる（一時的な失敗から回復できるように）");
        var secondLines = await second.ReadLogLinesAsync();
        secondLines.Should().Contain(l => l.Contains("入れ替えに失敗しました"), "ログには毎回残す");
        secondLines.Should().Contain(l => l.Contains("通知済み"));

        // さらに次の版が出て、それも失敗した場合は、改めて通知する。
        var third = await CreateAsync(downloadFails: true, appPaths: appPaths, tag: "v99.0.1");
        await third.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        third.Notices.Should().ContainSingle("別の版の失敗は新しい出来事");
    }

    [AvaloniaFact(DisplayName = "失敗のあと成功したら、通知済みの記録は消える")]
    public async Task 成功したら失敗の記録を消す()
    {
        var appPaths = NewAppPaths();
        var failed = await CreateAsync(downloadFails: true, appPaths: appPaths);
        await failed.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);
        (await new UpdateCheckStateStore(appPaths).LoadAsync()).AutoInstallFailedTag.Should().Be(NewTag);

        var succeeded = await CreateAsync(appPaths: appPaths);
        await succeeded.Vm.CheckForUpdateOnStartupAsync(isRestartLaunch: false);

        (await new UpdateCheckStateStore(appPaths).LoadAsync()).AutoInstallFailedTag.Should().BeNull();
        succeeded.Vm.CurrentAutoUpdateNotice!.Kind.Should().Be(AutoUpdateNoticeKind.Installed);
    }

    // ------------------------------------------------------------------
    // 設定（画面側の値）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "設定画面の値: 既定はオフで、オンにすると保存され、読み直しても保たれる")]
    public async Task 設定の値は保存される()
    {
        var appPaths = NewAppPaths();
        var vm = new SettingsViewModel(appPaths, new RecordingDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();
        vm.UpdateAutoInstall.Should().BeFalse("既定はオフ");

        vm.UpdateAutoInstall = true;
        await vm.FlushPendingSaveAsync();

        var saved = await new SettingsStore(appPaths).LoadAsync();
        saved.Value.Update.AutoInstall.Should().BeTrue();
        saved.Value.Update.CheckOnStartup.Should().BeTrue("他の更新設定は変わらない");

        var reloaded = new SettingsViewModel(appPaths, new RecordingDialogService(), new AvaloniaUiServices());
        await reloaded.InitializeAsync();
        reloaded.UpdateAutoInstall.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "設定画面の値: 「起動時に更新を確認する」がオフの間は無効表示になり、値そのものは保たれる")]
    public async Task 起動時の確認がオフなら無効表示になる()
    {
        var vm = new SettingsViewModel(NewAppPaths(), new RecordingDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();
        vm.UpdateAutoInstall = true;
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.UpdateCheckOnStartup = false;

        vm.IsUpdateAutoInstallAvailable.Should().BeFalse();
        changed.Should().Contain(nameof(SettingsViewModel.IsUpdateAutoInstallAvailable), "画面の無効表示を切り替えるため");
        vm.UpdateAutoInstall.Should().BeTrue("選んでいた状態は消さない。確認をオンに戻せばそのまま効く");

        vm.UpdateCheckOnStartup = true;
        vm.IsUpdateAutoInstallAvailable.Should().BeTrue();
    }

    [AvaloniaFact(DisplayName = "「バージョン情報」タブ: チェックボックスは起動時の確認がオフの間だけ無効になり、理由が出る")]
    public async Task バージョン情報タブのチェックボックス()
    {
        var vm = new SettingsViewModel(NewAppPaths(), new RecordingDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();
        var view = new AboutView { DataContext = vm };
        var window = _windows.Track(new Window { Content = view, Width = 900, Height = 700 });
        window.Show();

        var box = view.GetVisualDescendants().OfType<CheckBox>()
            .Single(c => AutomationProperties.GetName(c) == "確認せずに自動で更新する");
        box.IsEnabled.Should().BeTrue();
        box.IsChecked.Should().BeFalse();
        var hint = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "「起動時に更新を確認する」がオフのため、この設定は使われません。");
        hint.IsVisible.Should().BeFalse();

        vm.UpdateCheckOnStartup = false;
        box.IsEnabled.Should().BeFalse();
        hint.IsVisible.Should().BeTrue();

        box.IsEnabled = true; // 値の反映だけを見る（無効化の確認は上で済み）。
        vm.UpdateCheckOnStartup = true;
        box.IsChecked = true;
        vm.UpdateAutoInstall.Should().BeTrue("チェックボックスの操作が設定の値へ届く");
    }

    // ------------------------------------------------------------------
    // ステータスバーの通知（表示）
    // ------------------------------------------------------------------

    [AvaloniaFact(DisplayName = "ステータスバー: 完了の通知には文言と「今すぐ再起動」が出て、失敗の通知には再起動のボタンが出ない")]
    public async Task ステータスバーの通知の表示()
    {
        var (shell, window) = await OpenShellAsync();
        var text = FindText(window, "自動更新の通知");
        var restart = FindButton(window, "更新を反映するため今すぐ再起動");
        var close = FindButton(window, "自動更新の通知を閉じる");
        text.IsVisible.Should().BeFalse("通知が無いときは何も出さない");
        restart.IsVisible.Should().BeFalse();
        close.IsVisible.Should().BeFalse();

        shell.SetUpdateNotice(new AutoUpdateNotice(AutoUpdateNoticeKind.Installed, "Graft を 1.0.25 に更新しました。次回の起動から新しい版になります", "詳細"));
        text.IsVisible.Should().BeTrue();
        text.Text.Should().Be("Graft を 1.0.25 に更新しました。次回の起動から新しい版になります");
        restart.IsVisible.Should().BeTrue();
        close.IsVisible.Should().BeTrue();

        shell.SetUpdateNotice(new AutoUpdateNotice(AutoUpdateNoticeKind.Failed, "自動更新に失敗しました。設定の「今すぐ更新を確認」から手動で更新できます", "詳細"));
        text.IsVisible.Should().BeTrue();
        restart.IsVisible.Should().BeFalse("入れ替わっていないので再起動を促さない");
        close.IsVisible.Should().BeTrue();

        shell.SetUpdateNotice(null);
        text.IsVisible.Should().BeFalse();
        close.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact(DisplayName = "ステータスバー: 「今すぐ再起動」と「×」の操作は、橋渡し用のイベントとして届く")]
    public async Task ステータスバーの操作はイベントで届く()
    {
        var (shell, _) = await OpenShellAsync();
        var restartRequested = 0;
        var dismissRequested = 0;
        shell.UpdateRestartRequested += (_, _) => restartRequested++;
        shell.UpdateNoticeDismissRequested += (_, _) => dismissRequested++;
        shell.SetUpdateNotice(new AutoUpdateNotice(AutoUpdateNoticeKind.Installed, "x", "y"));

        shell.RestartForUpdateCommand.Execute(null);
        shell.DismissUpdateNoticeCommand.Execute(null);

        restartRequested.Should().Be(1);
        dismissRequested.Should().Be(1);
        shell.HasUpdateNotice.Should().BeTrue("閉じる側の状態はSettingsViewModelが持ち、消えたことはSetUpdateNotice(null)で伝わる");
    }

    // ------------------------------------------------------------------
    // ヘルパ
    // ------------------------------------------------------------------

    private AppPaths NewAppPaths()
    {
        var appPaths = new AppPaths(Path.Combine(_root, "app"));
        appPaths.EnsureCoreDirectoriesExist();
        return appPaths;
    }

    private async Task<Harness> CreateAsync(
        bool autoInstall = true,
        bool allowMissingChecksum = false,
        string? digestOverride = null,
        bool downloadFails = false,
        bool installDirResolvesToNull = false,
        UpdatePlatform platform = UpdatePlatform.Windows,
        AppPaths? appPaths = null,
        string tag = NewTag)
    {
        appPaths ??= NewAppPaths();

        var installDir = Path.Combine(_root, "install", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installDir);
        foreach (var name in UpdateFiles.RequiredFileNames)
        {
            File.WriteAllText(Path.Combine(installDir, name), $"old-{name}");
        }

        var zip = BuildValidZip();
        var version = tag.TrimStart('v');
        var asset = new GitHubReleaseAsset
        {
            Name = $"Graft-{version}-win-x64.zip",
            BrowserDownloadUrl = $"{DownloadHost}{tag}/Graft-{version}-win-x64.zip",
            Digest = allowMissingChecksum ? null : digestOverride ?? $"sha256:{Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()}",
        };
        var release = new GitHubReleaseInfo
        {
            TagName = tag,
            HtmlUrl = $"https://github.com/Yu5rin/Graft/releases/tag/{tag}",
            AllowMissingChecksum = allowMissingChecksum,
            Assets = new[] { asset },
        };

        var feed = new FixedReleaseFeed(release);
        var downloader = new FakeDownloader(zip, downloadFails);
        var dialogs = new RecordingDialogService();
        var logger = new Logger(appPaths, autoCleanupOnStart: false);
        var vm = new SettingsViewModel(appPaths, dialogs, new AvaloniaUiServices(), releaseFeed: feed, updateDownloader: downloader)
        {
            UpdatePlatform = platform,
            InstallDirectoryResolver = () => installDirResolvesToNull ? null : installDir,
        };
        await vm.InitializeAsync();
        vm.Logger = logger;
        vm.UpdateCheckOnStartup = true;
        vm.UpdateAutoInstall = autoInstall;

        var harness = new Harness(vm, dialogs, feed, downloader, installDir, appPaths, logger);
        vm.AutoUpdateNoticeChanged += (_, n) => harness.Notices.Add(n);
        vm.RestartRequested += (_, e) => harness.Restarts.Add(e.Reason);
        return harness;
    }

    private async Task<(ShellViewModel Shell, ShellWindow Window)> OpenShellAsync()
    {
        var appPaths = NewAppPaths();
        var settingsStore = new SettingsStore(appPaths);
        await settingsStore.SaveAsync(new Settings { ShowPreview = false }).ConfigureAwait(true);

        var shell = StartupCoordinator.BuildShellViewModel(
            appPaths, new Settings { ShowPreview = false }, settingsStore, new PatchQueue(appPaths),
            new ProjectStore(appPaths), new RevisionStore(appPaths), new RevisionRestorer(appPaths),
            new RecordingDialogService(), new AvaloniaUiServices(), openSettings: () => { });

        var window = _windows.Track(new ShellWindow(shell) { Width = 1280, Height = 800 });
        window.Show();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (shell.Graft.ProjectPane.State == ProjectPaneState.Loading)
        {
            await Task.Delay(10, cts.Token).ConfigureAwait(true);
        }

        return (shell, window);
    }

    private static TextBlock FindText(Window window, string automationName)
        => window.GetVisualDescendants().OfType<TextBlock>().Single(t => AutomationProperties.GetName(t) == automationName);

    private static Button FindButton(Window window, string automationName)
        => window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == automationName);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
    }

    private static byte[] BuildValidZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var fileName in UpdateFiles.RequiredFileNames)
            {
                var entry = archive.CreateEntry($"Graft/{fileName}");
                using var writer = new StreamWriter(entry.Open());
                writer.Write($"new-{fileName}");
            }
        }

        return stream.ToArray();
    }

    private sealed class Harness(
        SettingsViewModel vm, RecordingDialogService dialogs, FixedReleaseFeed feed, FakeDownloader downloader,
        string installDir, AppPaths appPaths, Logger logger)
    {
        public SettingsViewModel Vm { get; } = vm;
        public RecordingDialogService Dialogs { get; } = dialogs;
        public FixedReleaseFeed Feed { get; } = feed;
        public FakeDownloader Downloader { get; } = downloader;
        public string InstallDir { get; } = installDir;
        public List<AutoUpdateNotice?> Notices { get; } = new();
        public List<RestartReason> Restarts { get; } = new();

        /// <summary>入れ替えが起きていない（全ファイルが元のままで、退避ファイルも無い）こと。</summary>
        public void AssertInstallDirUntouched()
        {
            foreach (var name in UpdateFiles.RequiredFileNames)
            {
                File.ReadAllText(Path.Combine(InstallDir, name)).Should().Be($"old-{name}");
                File.Exists(Path.Combine(InstallDir, name + UpdateFiles.OldFileSuffix)).Should().BeFalse();
            }
        }

        public async Task<string[]> ReadLogLinesAsync()
        {
            // Loggerはチャネル経由で非同期に書くため、DisposeAsyncで書き込みの完了を待ってから読む。
            await logger.DisposeAsync();
            var logPath = appPaths.GetLogFilePath(DateOnly.FromDateTime(DateTime.Now));
            File.Exists(logPath).Should().BeTrue();
            return await File.ReadAllLinesAsync(logPath);
        }
    }

    private sealed class FixedReleaseFeed(GitHubReleaseInfo release) : IReleaseFeed
    {
        public int CallCount { get; private set; }

        public Task<AtomFeedTag?> TryGetLatestTagFromAtomAsync(string checkUrl, CancellationToken ct)
            => Task.FromResult<AtomFeedTag?>(null);

        public Task<ReleaseFetchResult> GetLatestReleaseAsync(string checkUrl, string userAgent, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(ReleaseFetchResult.Ok(release));
        }
    }

    private sealed class FakeDownloader(byte[] zip, bool fail) : IUpdateDownloader
    {
        public int CallCount { get; private set; }

        public async Task<UpdateDownloadOutcome> DownloadAsync(
            string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            CallCount++;
            if (fail) return new UpdateDownloadOutcome(UpdateDownloadStatus.Failed, "テスト用に通信を失敗させました。");
            await File.WriteAllBytesAsync(destinationPath, zip, ct);
            progress?.Report(1.0);
            return new UpdateDownloadOutcome(UpdateDownloadStatus.Success);
        }
    }

    /// <summary>出したダイアログの題名・本文・ボタンの文言を記録する。アクションはすべて「しない」を選ぶ。</summary>
    private sealed class RecordingDialogService : IDialogService
    {
        public List<string> Titles { get; } = new();
        public List<string> Messages { get; } = new();
        public List<string> ActionLabels { get; } = new();

        public Task<bool> ShowActionMessageAsync(string title, string message, string actionLabel)
        {
            Titles.Add(title);
            Messages.Add(message);
            ActionLabels.Add(actionLabel);
            return Task.FromResult(false);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            Titles.Add(title);
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);
    }
}
