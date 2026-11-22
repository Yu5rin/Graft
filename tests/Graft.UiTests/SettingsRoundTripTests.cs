using Avalonia.Headless.XUnit;
using FluentAssertions;
using Graft.Infra;
using Graft.Platform;
using Graft.ViewModels;

namespace Graft.UiTests;

/// <summary>
/// 設定画面のタブを組み替えても、settings.json の中身（キーの名前・値の形・既定値）が
/// 変わらないことの確認。「全項目に既定以外の値を入れた settings.json」を設定画面の
/// ViewModelで読み込み、何も変えずに（画面の操作で保存を走らせて）保存し直したとき、
/// ファイルの中身が1文字も変わらないことを見る。
///
/// 画面（SettingsViewModel）は、項目を1つ変えるたびに「画面の全項目から Settings を組み立て直して」
/// 保存する（SettingsViewModel.CommitAndSaveAsync）。そのため、画面から読み書きされない項目や、
/// 読み込みと書き出しで値の形が変わる項目があれば、ここで差として表れる。
/// 保存を走らせるために使う ShowPreview は、値を反転して元に戻すだけで、最終値は変わらない。
///
/// 自動更新の確認先URL（update.checkUrl）だけは、配布元として許可された URL でないと
/// 読み込み時に既定へ戻される（UpdateHostPolicy）ため、既定のままにしている。
/// </summary>
public class SettingsRoundTripTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "graft-settings-roundtrip", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
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

    [AvaloniaFact(DisplayName = "全項目に既定以外の値を入れたsettings.jsonを画面で読み込み、何も変えずに保存し直しても、中身が変わらない")]
    public async Task 全項目が既定以外のsettings_jsonは画面を通しても変わらない()
    {
        var appPaths = new AppPaths(_root);
        appPaths.EnsureCoreDirectoriesExist();
        var store = new SettingsStore(appPaths);

        var original = BuildAllNonDefault();
        await store.SaveAsync(original);
        var before = await File.ReadAllTextAsync(appPaths.SettingsFilePath);

        // 前提: 作った値が本当に全項目で既定と違うこと（既定のままの項目があると、取りこぼしを見逃す）。
        // 読み込み時の検証で既定へ戻されていないことも、同じ比較で確かめられる。
        var loaded = await store.LoadAsync();
        loaded.Issues.Should().BeEmpty("全項目が有効な値なので、読み込みで警告が出てはならない");
        loaded.Value.Should().BeEquivalentTo(original);
        AssertNoPropertyEqualsDefault(loaded.Value, new Settings());

        var vm = new SettingsViewModel(appPaths, new NullDialogService(), new AvaloniaUiServices());
        await vm.InitializeAsync();

        // 画面の操作で保存を走らせる（反転して元に戻す。最終値は変わらない）。
        vm.ShowPreview = !vm.ShowPreview;
        vm.ShowPreview = !vm.ShowPreview;
        await vm.FlushPendingSaveAsync();

        var after = await File.ReadAllTextAsync(appPaths.SettingsFilePath);
        after.Should().Be(before, "画面を通しても、settings.jsonのキー・値・並びは変わってはならない");

        var reloaded = await store.LoadAsync();
        reloaded.Value.Should().BeEquivalentTo(original);
    }

    /// <summary>settings.json の全項目に、既定と異なる（かつ有効な）値を入れた設定を作る。</summary>
    private static Settings BuildAllNonDefault() => new()
    {
        Theme = "nord",
        TooltipDetail = "detailed",
        ApplyMode = "allOrNothing",
        ShowPreview = false,
        RequireSummary = false,
        ClipboardWatch = new ClipboardWatchSettings
        {
            Enabled = true, Action = "passive", AutoParse = false, ActivateOnDetect = false,
        },
        Hotkey = "Ctrl+Alt+B",
        PromptHotkey = "Ctrl+Shift+J",
        Backup = new BackupSettings { MaxRevisions = 37, MaxTotalMB = 123, UseRecycleBin = false },
        Matching = new MatchingSettings { SimilarityThreshold = 0.7, AllowSimilarityMatch = false, RangeWarningLines = 111 },
        Encoding = new EncodingSettings { NewFileEncoding = "utf-16", NewFileBom = true, NewFileNewLine = "crlf" },
        Syntax = new SyntaxSettings { Enabled = false, ShowLineNumbers = false },
        Diff = new DiffSettings { ContextLines = 5, WordWrap = true, ShowWhitespace = true, SideBySide = false },
        Safety = new SafetySettings
        {
            AllowedExtensions = new List<string> { ".py", ".rs", ".md" }, MaxFileSizeMB = 7, MaxFilesPerRevision = 55,
        },
        Context = new ContextSettings
        {
            RespectGitignore = false, TokenRatio = 3.5, TokenWarnThreshold = 12345, AppendFilesToPrompt = true,
        },
        Hooks = new HookSettings { TimeoutSec = 45 },
        Git = new GitSettings { AutoCommit = true },
        Editor = new EditorSettings
        {
            FontSize = 17, FontFamily = "Noto Sans", BodyTextFontFamily = "Noto Serif", MonospaceFontFamily = "Noto Sans Mono",
            WordWrap = false, ShowWhitespace = true, ShowLineNumbers = false, HighlightCurrentLine = false,
            TabSize = 2, InsertSpaces = false, DetectIndent = false, AutoClosingBrackets = false, Folding = false,
            Completion = false, GitGutter = false, ColorPreviewInCode = false, IndentGuideMode = "all",
        },
        LogLevel = "debug",
        CloseBehavior = "tray",
        LaunchAtStartup = true,
        MinimizeToTray = true,
        Update = new UpdateSettings { CheckOnStartup = false, AutoInstall = true },
        // Excel VBA 連携（docs/仕様書_VBA連携.md 8章）。設定画面にはまだ編集欄が無いが、
        // 別の項目の保存で値が既定へ戻らないこと（BuildSettingsFromFields の持ち回り）を、
        // 全項目の往復の中でも固定する。CallTimeoutMs は ComTimeoutMs 以上でなければ読み込みで戻される。
        Vba = new VbaSettings
        {
            Enabled = false, PullOnContextOpen = false, PullBeforeDryRun = false, ComTimeoutMs = 7000, CallTimeoutMs = 40000,
            // ブックの情報（仕様書_ブックの情報と依頼の観点 9章。段階 Q2）。すべて既定と異なる値にする。
            CollectWorkbookInfo = false, WorkbookInfoSampleRows = 8, WorkbookInfoMaxColumns = 40,
            WorkbookInfoMaxCellChars = 80, WorkbookInfoMaxChars = 30000,
            // シートごとの見せ方の「全部」「範囲を指定」の上限（v0.4）。
            WorkbookInfoAllMaxRows = 700, WorkbookInfoAllMaxColumns = 60,
            // 反映前の自動控え（同 7.3。段階 Q3）。
            RescueMaxGenerations = 35, RescueMaxTotalMB = 120,
        },
    };

    /// <summary>
    /// 直下と入れ子の全プロパティについて、既定値と同じものが無いことを確かめる。
    /// ただし update.checkUrl は上のとおり既定のままにするため対象から外す。
    /// </summary>
    private static void AssertNoPropertyEqualsDefault(object actual, object defaults, string path = "")
    {
        foreach (var prop in actual.GetType().GetProperties())
        {
            var name = path + prop.Name;
            var a = prop.GetValue(actual);
            var d = prop.GetValue(defaults);
            if (name == "Update.CheckUrl") continue;

            if (a is not null && a.GetType().Namespace == typeof(Settings).Namespace && a.GetType().IsClass && a is not string
                && !a.GetType().IsGenericType)
            {
                AssertNoPropertyEqualsDefault(a, d!, name + ".");
                continue;
            }

            if (a is System.Collections.IEnumerable list && a is not string)
            {
                list.Cast<object>().Should().NotBeEquivalentTo(((System.Collections.IEnumerable)d!).Cast<object>(),
                    $"{name} は既定と異なる値でなければならない");
                continue;
            }

            Equals(a, d).Should().BeFalse($"{name} は既定と異なる値でなければならない（既定のままだと取りこぼしを見逃す）");
        }
    }

    /// <summary>ダイアログは出さない（この画面の保存では確認が要らない）。</summary>
    private sealed class NullDialogService : IDialogService
    {
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);

        public Task<bool?> ConfirmThreeWayAsync(string title, string message, string yesLabel, string noLabel)
            => Task.FromResult<bool?>(false);

        public Task<string?> PromptAsync(string title, string message, string? initial = null)
            => Task.FromResult<string?>(initial);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<string?> PickFileAsync(string title, IReadOnlyList<string>? extensions = null) => Task.FromResult<string?>(null);

        public Task<string?> SaveFileAsync(string title, string suggestedFileName, IReadOnlyList<string>? extensions = null)
            => Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
