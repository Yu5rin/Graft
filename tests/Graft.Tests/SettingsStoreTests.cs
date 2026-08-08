using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 仕様書14〜15章（設定ファイル settings.json）・13.1（データ破損時の復旧）の単体テスト。
/// 既定値が仕様書のJSONと一致すること、不正な値が既定へフォールバックし警告が返ること、
/// 破損時に .corrupt.&lt;日時&gt; へ退避して再生成されることを実ファイルで検証する。
/// </summary>
public class SettingsStoreTests
{
    private static AppPaths MakePaths(TempWorkspace ws) => new(ws.CreateDirectory("app"));

    // ------------------------------------------------------------------
    // 既定値が仕様書のJSONと一致すること
    // ------------------------------------------------------------------

    [Fact(DisplayName = "settings.jsonが存在しない場合、仕様書どおりの既定値が返る")]
    public async Task 既定値は仕様書のJSONと一致する()
    {
        using var ws = new TempWorkspace();
        var store = new SettingsStore(MakePaths(ws));

        var result = await store.LoadAsync();

        result.IsSuccess.Should().BeTrue();
        var s = result.Value;
        s.Theme.Should().Be("system");
        s.ApplyMode.Should().Be("allOrNothing");
        s.ShowPreview.Should().BeTrue();
        s.RequireSummary.Should().BeTrue();
        s.ClipboardWatch.Enabled.Should().BeFalse();
        s.ClipboardWatch.Action.Should().Be("notify");
        s.Hotkey.Should().Be("Ctrl+Alt+V");
        s.Backup.MaxRevisions.Should().Be(100);
        s.Backup.MaxTotalMB.Should().Be(500);
        s.Backup.UseRecycleBin.Should().BeTrue();
        s.Matching.SimilarityThreshold.Should().Be(0.85);
        s.Matching.AllowSimilarityMatch.Should().BeTrue();
        s.Matching.RangeWarningLines.Should().Be(300);
        s.Encoding.NewFileEncoding.Should().Be("utf-8");
        s.Encoding.NewFileBom.Should().BeFalse();
        s.Syntax.Enabled.Should().BeTrue();
        s.Syntax.ShowLineNumbers.Should().BeTrue();
        s.Diff.ContextLines.Should().Be(3);
        s.Diff.WordWrap.Should().BeFalse();
        s.Diff.ShowWhitespace.Should().BeFalse();
        s.Safety.MaxFileSizeMB.Should().Be(10);
        s.Safety.MaxFilesPerRevision.Should().Be(200);
        s.Safety.AllowedExtensions.Should().BeEquivalentTo(new[]
        {
            ".py", ".js", ".ts", ".tsx", ".cs", ".java", ".go",
            ".rs", ".html", ".css", ".json", ".yaml", ".yml",
            ".md", ".sql", ".xml", ".txt",
        });
        s.Context.RespectGitignore.Should().BeTrue();
        s.Context.TokenRatio.Should().Be(2.5);
        s.Context.TokenWarnThreshold.Should().Be(50000);
        s.Hooks.TimeoutSec.Should().Be(120);
        s.Git.AutoCommit.Should().BeFalse();
        s.LogLevel.Should().Be("info");
    }

    [Fact(DisplayName = "editorセクションの既定値が仕様書のJSONと一致する")]
    public async Task editorセクションの既定値が一致する()
    {
        using var ws = new TempWorkspace();
        var store = new SettingsStore(MakePaths(ws));

        var s = (await store.LoadAsync()).Value.Editor;

        s.FontSize.Should().Be(13);
        s.WordWrap.Should().BeFalse();
        s.ShowWhitespace.Should().BeFalse();
        s.ShowLineNumbers.Should().BeTrue();
        s.HighlightCurrentLine.Should().BeTrue();
        s.TabSize.Should().Be(4);
        s.InsertSpaces.Should().BeTrue();
        s.DetectIndent.Should().BeTrue();
        s.AutoClosingBrackets.Should().BeTrue();
        s.Folding.Should().BeTrue();
        s.Completion.Should().BeTrue();
        s.GitGutter.Should().BeTrue();
    }

    // ------------------------------------------------------------------
    // 不正な値のフォールバック
    // ------------------------------------------------------------------

    [Theory(DisplayName = "範囲外のsimilarityThresholdは既定値0.85へフォールバックし警告が返る")]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public async Task 範囲外のsimilarityThresholdはフォールバックする(double invalid)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, $$"""{ "matching": { "similarityThreshold": {{invalid}} } }""");
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Matching.SimilarityThreshold.Should().Be(0.85);
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Severity == Severity.Warning
            && i.Detail != null && i.Detail.Contains("similarityThreshold"));
    }

    [Theory(DisplayName = "範囲外のeditor.fontSizeは既定値13へフォールバックし警告が返る")]
    [InlineData(1000)]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task 範囲外のfontSizeはフォールバックする(double invalid)
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, $$"""{ "editor": { "fontSize": {{invalid}} } }""");
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Editor.FontSize.Should().Be(13);
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail != null && i.Detail.Contains("fontSize"));
    }

    [Fact(DisplayName = "0以下のeditor.tabSizeは既定値4へフォールバックし警告が返る")]
    public async Task ゼロ以下のtabSizeはフォールバックする()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """{ "editor": { "tabSize": 0 } }""");
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Editor.TabSize.Should().Be(4);
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404 && i.Detail != null && i.Detail.Contains("tabSize"));
    }

    [Fact(DisplayName = "不正なthemeの値は既定値systemへフォールバックする")]
    public async Task 不正なthemeはフォールバックする()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """{ "theme": "purple" } """);
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Theme.Should().Be("system");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404);
    }

    [Fact(DisplayName = "空のallowedExtensionsは既定の拡張子一覧へフォールバックする")]
    public async Task 空の許可拡張子一覧はフォールバックする()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """{ "safety": { "allowedExtensions": [] } }""");
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Safety.AllowedExtensions.Should().NotBeEmpty();
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404);
    }

    [Fact(DisplayName = "複数の不正な値がある場合はそれぞれについて個別に警告が返る")]
    public async Task 複数の不正値でそれぞれ警告が返る()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """
            {
              "applyMode": "invalid-mode",
              "backup": { "maxRevisions": -1 },
              "hooks": { "timeoutSec": -10 }
            }
            """);
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.ApplyMode.Should().Be("allOrNothing");
        result.Value.Backup.MaxRevisions.Should().Be(100);
        result.Value.Hooks.TimeoutSec.Should().Be(120);
        result.Issues.Count(i => i.Code == ErrorCode.E404).Should().BeGreaterOrEqualTo(3);
    }

    // ------------------------------------------------------------------
    // ValidateOnly（14章 即時反映方式の保存前検証）
    //
    // SettingsViewModelは変更のたびに自動保存するが、LoadAsyncと同じ「不正値を既定値へ
    // 差し替えて延命する」ロジックをそのまま保存前にも使うと、画面に見えている値と
    // 実際にディスクへ保存された値が黙って食い違う事故になる。ValidateOnlyは同じ検証規則を
    // 使いつつディスクへは一切書き込まず、「この値を保存してよいか」の判定材料
    // （Issuesの有無）だけを返す。ただしErrorCodeはLoadAsyncのE404（設定・履歴データの破損→
    // 退避のうえ再生成）ではなく、保存前検証向けのE406（値を修正すると自動的に保存される）へ
    // 差し替えて返す。「データが壊れて再生成された」という誤った印象を与えないため。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ValidateOnlyは正常な値ならIssuesが空で、ディスクへは書き込まない")]
    public void ValidateOnlyは正常な値ならIssuesが空でディスクへ書き込まない()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);

        var result = SettingsStore.ValidateOnly(new Settings { Backup = new BackupSettings { MaxRevisions = 50 } });

        result.IsSuccess.Should().BeTrue();
        result.Issues.Should().BeEmpty();
        result.Value.Backup.MaxRevisions.Should().Be(50, "正常な値はそのまま素通りする（既定値へ差し替えない）");
        File.Exists(paths.SettingsFilePath).Should().BeFalse("ValidateOnlyはディスクへ一切書き込んではならない");
    }

    [Fact(DisplayName = "ValidateOnlyは範囲外のbackup.maxRevisionsに対してIssuesを返す（保存を保留する判断材料）")]
    public void ValidateOnlyは範囲外の値でIssuesを返す()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);

        var result = SettingsStore.ValidateOnly(new Settings { Backup = new BackupSettings { MaxRevisions = -1 } });

        result.Issues.Should().Contain(i => i.Code == ErrorCode.E406 && i.Severity == Severity.Warning
            && i.Detail != null && i.Detail.Contains("maxRevisions"));
        File.Exists(paths.SettingsFilePath).Should().BeFalse(
            "ValidateOnly自体は判定するだけで、Issuesがあっても保存しない判断は呼び出し側（SettingsViewModel）が行う");
    }

    [Fact(DisplayName = "ValidateOnlyは複数の不正な値がある場合、それぞれについて個別にIssuesを返す")]
    public void ValidateOnlyは複数の不正値でそれぞれIssuesを返す()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);

        var result = SettingsStore.ValidateOnly(new Settings
        {
            ApplyMode = "invalid-mode",
            Backup = new BackupSettings { MaxRevisions = -1 },
            Hooks = new HookSettings { TimeoutSec = -10 },
        });

        result.Issues.Count(i => i.Code == ErrorCode.E406).Should().BeGreaterOrEqualTo(3);
        File.Exists(paths.SettingsFilePath).Should().BeFalse();
    }

    // ------------------------------------------------------------------
    // バグ2: E406（保存前検証）の文言が実態と矛盾していた不具合の回帰テスト。
    //
    // 以前はValidateOnlyのDetailがLoadAsyncと同じ「既定値Xを使用します」という文言のまま
    // コードだけE406へ差し替わっていたため、「入力値が保存条件を満たしていない: …は0以上
    // である必要があるため既定値100を使用します。（対処: 値を修正すると自動的に保存されます）」
    // という、Detail（既定値100を使う）とRemedy（保存されず修正を待つ）が矛盾した表示になって
    // いた。実際には何も保存されず直前の値のままなので、Detailも「保存されない」という
    // 事実に合わせる必要がある。一方でLoadAsync（起動時に不正値を実際に既定値へ差し替える
    // 文脈）では「既定値を使用します」のままが正確であるため、文脈で文言を分けたことも
    // あわせて検証する（両方が同じ検証ロジック=Validate()を共有しているため、
    // 一律に変えると片方が不正確になる）。
    // ------------------------------------------------------------------

    [Fact(DisplayName = "ValidateOnly（保存前検証）のDetailは『既定値を使用します』と言わず、保存されない旨を伝える")]
    public void ValidateOnlyのDetailは保存されない旨を伝える()
    {
        var result = SettingsStore.ValidateOnly(new Settings { Backup = new BackupSettings { MaxRevisions = -5 } });

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E406
            && i.Detail != null && i.Detail.Contains("maxRevisions")).Subject;

        issue.Detail.Should().NotContain("既定値", "保存前検証では既定値へ差し替わらないため、既定値を使うという表現は誤り");
        issue.Detail.Should().NotContain("100を使用", "実際には直前の正しい値のまま保存されず、100になるわけではない");
        issue.Detail.Should().Contain("保存されて", "保存されなかった（保留された）ことが伝わる文言である必要がある");
    }

    [Fact(DisplayName = "LoadAsync（起動時の読み込み）のDetailは、実際に既定値へ差し替わるため『既定値を使用します』のままでよい")]
    public async Task LoadAsyncのDetailは既定値を使用する旨のままでよい()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        WriteRawSettings(paths, """{ "backup": { "maxRevisions": -5 } }""");
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.Value.Backup.MaxRevisions.Should().Be(100, "起動時の読み込みでは実際に既定値へ差し替わる");
        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E404
            && i.Detail != null && i.Detail.Contains("maxRevisions")).Subject;
        issue.Detail.Should().Contain("既定値", "この文脈では実際に既定値が使われるため、その旨を伝える文言のままでよい");
    }

    [Fact(DisplayName = "E406の対処方法（Remedy）は『値を修正すると自動的に保存されます』のまま変わらない")]
    public void E406のRemedyは保存される旨のままである()
    {
        var result = SettingsStore.ValidateOnly(new Settings { Backup = new BackupSettings { MaxRevisions = -5 } });

        var issue = result.Issues.Should().ContainSingle(i => i.Code == ErrorCode.E406).Subject;
        issue.Remedy.Should().Be("値を修正すると自動的に保存されます");
        issue.Summary.Should().Be("入力値が保存条件を満たしていない");

        // Detail・Remedyを組み合わせた表示（ToDisplayText + Remedy）全体で見ても、
        // 「既定値を使用する」という矛盾した記述が残っていないことを確認する。
        var displayText = $"{issue.ToDisplayText()}（対処: {issue.Remedy}）";
        displayText.Should().NotContain("既定値100を使用");
    }

    // ------------------------------------------------------------------
    // 13.1 破損時の復旧
    // ------------------------------------------------------------------

    [Fact(DisplayName = "settings.jsonがJSONとして解析できない場合は.corrupt.<日時>へ退避し既定値で再生成する")]
    public async Task 破損時は退避して既定値で再生成する()
    {
        using var ws = new TempWorkspace();
        var paths = MakePaths(ws);
        var brokenContent = "{ この行は正しいJSONではありません ";
        File.WriteAllText(paths.SettingsFilePath, brokenContent);
        var store = new SettingsStore(paths);

        var result = await store.LoadAsync();

        result.IsSuccess.Should().BeTrue("破損していても既定値で再生成され成功として返るはず");
        result.Value.Theme.Should().Be("system");
        result.Issues.Should().Contain(i => i.Code == ErrorCode.E404);

        var directory = Path.GetDirectoryName(paths.SettingsFilePath)!;
        var quarantined = Directory.GetFiles(directory, "settings.json.corrupt.*");
        quarantined.Should().ContainSingle("破損したファイルが.corrupt.<日時>へ退避されているはず");
        File.ReadAllText(quarantined[0]).Should().Be(brokenContent, "退避先には元の破損内容がそのまま残るはず");

        File.Exists(paths.SettingsFilePath).Should().BeTrue("再生成されたsettings.jsonが存在するはず");
        var regenerated = await File.ReadAllTextAsync(paths.SettingsFilePath);
        regenerated.Should().Contain("\"system\"", "再生成されたファイルは既定値のJSONであるはず");
    }

    private static void WriteRawSettings(AppPaths paths, string json)
    {
        var directory = Path.GetDirectoryName(paths.SettingsFilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(paths.SettingsFilePath, json);
    }
}
