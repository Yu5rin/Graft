using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Graft.Core;
using Graft.Features;
using Graft.Infra;
using Graft.Tests.TestSupport;
using Xunit;

namespace Graft.Tests;

/// <summary>
/// 実画面での点検で見つかった「誤解を招く表示・詰む文言」の回帰テスト。
///
/// いずれも機能そのものは動いていて、<b>画面に出る文字だけが利用者を誤った判断へ導く</b>種類の
/// 不具合であり、通常の機能テストでは素通りしてしまう。修正前の挙動を「なぜ困るのか」と一緒に
/// ここへ書き残し、二度と戻らないよう固定する。
/// </summary>
public class MisleadingMessageRegressionTests
{
    // ==================================================================
    // B-4: 対処文どおり OCCURRENCE=1 を書いても、同じ E102 で止まる
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 同じファイル・同じSEARCHに対し
    /// <list type="bullet">
    /// <item>OCCURRENCE 未指定 → E102「複数箇所にマッチ」＋「OCCURRENCE を指定してください」</item>
    /// <item>OCCURRENCE=2 → 適用できる</item>
    /// <item><b>OCCURRENCE=1 → まったく同じ E102 が同じ対処文つきで返る</b></item>
    /// </list>
    /// 原因は<c>OccurrenceSpec.IsDefault</c>が<c>Index == 1</c>で既定判定していたことで、
    /// 「未指定」と「明示的に1番目」が区別できていなかった。対処文に従って最も自然な1番目を
    /// 指定した利用者が、画面からは絶対に分からない理由で詰む。
    /// </summary>
    [Fact(DisplayName = "B-4: 明示的なOCCURRENCE=1は「未指定」と区別され、1番目のマッチが選ばれる")]
    public void OCCURRENCE1を明示すると1番目が選ばれる()
    {
        var engine = new MatchEngine();
        var original = "x\nx\nx\n";
        var pair = new SearchReplacePair { SearchText = "x", ReplaceText = "y" };

        var result = engine.Match(original, pair, PatchTextUtil.ParseOccurrence("1"));

        result.IsSuccess.Should().BeTrue(
            "対処文どおりにOCCURRENCE=1と書いた利用者へ、同じE102を返してはならない（B-4）");
        result.Value.Should().HaveCount(1);
        result.Value[0].StartLine.Should().Be(0, "1番目（0始まりで0行目）のマッチが選ばれるはず");
    }

    [Fact(DisplayName = "B-4: OCCURRENCE未指定で複数マッチした場合は従来どおりE102（挙動は変えない）")]
    public void OCCURRENCE未指定は従来どおりE102()
    {
        var engine = new MatchEngine();
        var original = "x\nx\nx\n";
        var pair = new SearchReplacePair { SearchText = "x", ReplaceText = "y" };

        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        result.IsSuccess.Should().BeFalse();
        result.Errors.Should().Contain(i => i.Code == ErrorCode.E102);
    }

    [Fact(DisplayName = "B-4: E102のdetailは「1〜N を指定できます」と指定できる範囲まで示す")]
    public void E102は指定できる範囲を示す()
    {
        var engine = new MatchEngine();
        var original = "x\nx\nx\n";
        var pair = new SearchReplacePair { SearchText = "x", ReplaceText = "y" };

        var result = engine.Match(original, pair, OccurrenceSpec.Single);

        var issue = result.Errors.Single(i => i.Code == ErrorCode.E102);
        issue.Detail.Should().Contain("OCCURRENCE=1")
            .And.Contain("OCCURRENCE=3", "何番目まで指定できるのかが画面から分かる必要がある（B-4）");
    }

    [Fact(DisplayName = "B-4: E102の対処文は、どこに何を書けばよいかまで示す")]
    public void E102の対処文が具体的である()
    {
        var remedy = ErrorCatalog.RemedyOf(ErrorCode.E102);

        remedy.Should().Contain("SEARCH", "書く場所（SEARCHマーカー行）を示すこと");
        remedy.Should().Contain("OCCURRENCE=1", "最も自然な「1番目」の指定がそのまま効くと分かること");
        remedy.Should().Contain("OCCURRENCE=ALL", "全箇所を対象にする代替手段も示すこと");
    }

    [Fact(DisplayName = "B-4: ParseOccurrenceは「1」も明示指定として解釈する")]
    public void ParseOccurrenceは1も明示指定として扱う()
    {
        PatchTextUtil.ParseOccurrence("1").IsExplicit.Should().BeTrue();
        PatchTextUtil.ParseOccurrence("1").IsDefault.Should().BeFalse();
        PatchTextUtil.ParseOccurrence("ALL").IsExplicit.Should().BeTrue();

        // 未指定・解釈できない値は従来どおり「既定」。
        OccurrenceSpec.Single.IsDefault.Should().BeTrue();
        PatchTextUtil.ParseOccurrence("なにか").IsDefault.Should().BeTrue();
    }

    [Fact(DisplayName = "B-4: パッチ本文に OCCURRENCE=1 と書いた場合も1番目が適用される（解析〜照合の通し）")]
    public async Task パッチ本文のOCCURRENCE1が効く()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "x\nx\nx\n");

        var patch = "<<<< FILE: a.txt\n<<<<<<< SEARCH OCCURRENCE=1\nx\n=======\ny\n>>>>>>> REPLACE\n";
        var ctx = harness.MakeContext(1);
        var dryRun = await harness.DryRunAsync(patch, ctx);

        dryRun.Plans.Should().OnlyContain(p => p.CanApply,
            "OCCURRENCE=1 は「未指定」ではなく明示指定として扱われ、E102にならないはず（B-4）");
    }

    // ==================================================================
    // B-1: フォルダが無いだけで「E404 設定・履歴データの破損」と告げる
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 起動時レポートに
    /// 「・E404 設定・履歴データの破損（/tmp/…/missingproj）：プロジェクト「missingproj」の
    /// ルート（/tmp/…/missingproj）が見つからないため未接続にしました。」と出ていた。
    /// 何も壊れていないのに「データの破損」と告げ、E404の対処文「退避のうえ再生成しました」は
    /// この状況にまったく当てはまらない。しかも同じ長いパスが1行に2回出て読みづらく、
    /// 何をすればいいのか（フォルダを戻す／場所を変更する／削除する）がどこにも書かれていなかった。
    /// </summary>
    [Fact(DisplayName = "B-1: ルートが無いプロジェクトはE404（破損）ではなくE213として報告される")]
    public async Task ルート不明はE404ではなくE211になる()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var missingRoot = Path.Combine(ws.RootPath, "missingproj");

        var validated = await store.ValidateAsync(new[]
        {
            new Project { Id = "p_gone", Name = "missingproj", Root = missingRoot },
        });

        validated.Issues.Should().NotContain(i => i.Code == ErrorCode.E404,
            "フォルダが無いだけで「設定・履歴データの破損」と告げてはならない（B-1）");
        var issue = validated.Issues.Single(i => i.Code == ErrorCode.E213);
        issue.Severity.Should().Be(Severity.Warning);
    }

    [Fact(DisplayName = "B-1: 表示文言に同じパスが2回出ない")]
    public async Task ルート不明の文言でパスが重複しない()
    {
        using var ws = new TempWorkspace();
        var paths = new AppPaths(ws.CreateDirectory("app"));
        var store = new ProjectStore(paths);
        var missingRoot = Path.Combine(ws.RootPath, "missingproj");

        var validated = await store.ValidateAsync(new[]
        {
            new Project { Id = "p_gone", Name = "missingproj", Root = missingRoot },
        });

        var text = validated.Issues.Single(i => i.Code == ErrorCode.E213).ToDisplayText();
        var occurrences = text.Split(missingRoot).Length - 1;
        occurrences.Should().Be(1, "GraftIssue.ToDisplayTextがPathを前置するため、detailにも書くと同じ長いパスが2回出る（B-1）");
    }

    [Fact(DisplayName = "B-1: E213の対処文は「場所を変更」「ネットワークドライブの接続」を案内する")]
    public void E213の対処文が行動を示す()
    {
        ErrorCatalog.SummaryOf(ErrorCode.E213).Should().NotContain("破損",
            "何も壊れていないのに「破損」と告げてはならない（B-1）");

        var remedy = ErrorCatalog.RemedyOf(ErrorCode.E213);
        remedy.Should().Contain("場所を変更", "フォルダを移した場合に取れる行動を示すこと");
        remedy.Should().Contain("ネットワークドライブ", "未接続のネットワークドライブという典型例に触れること");
        remedy.Should().NotContain("再生成", "E404の「退避のうえ再生成しました」を引きずってはならない");
    }

    // ==================================================================
    // B-10: 履歴の「Nファイル」が実際の変更件数と食い違う
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 統計の files は<c>DryRunPlanner.ComputeStats</c>が
    /// 「パッチが対象にした全ブロックの相異なるパス数」として数えており、
    /// <b>適用できなかったブロック・チェックを外したブロックまで含めていた</b>。
    /// その結果、1ファイルだけ適用したリビジョンの manifest.json が
    /// <c>"stats": { "files": 2 }</c> なのに <c>"entries"</c> は1件、履歴ペインは
    /// 「2ファイル +1 -1」と表示していた。履歴を後から見た利用者は「2ファイル変えた」と誤解し、
    /// 復元の影響範囲の判断も誤る。
    /// </summary>
    [Fact(DisplayName = "B-10: 適用できなかったブロックは履歴の「Nファイル」に数えない")]
    public async Task 適用できなかったブロックはファイル数に数えない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("ok.txt", "before\n");
        harness.WriteProjectText("ng.txt", "まったく違う内容\n");

        // ok.txt は当たり、ng.txt はSEARCHが見つからず失敗する。部分適用モードで
        // 「当たった1件だけ」を書き込む。
        var patch =
            "<<<< FILE: ok.txt\n<<<<<<< SEARCH\nbefore\n=======\nafter\n>>>>>>> REPLACE\n" +
            "<<<< FILE: ng.txt\n<<<<<<< SEARCH\n存在しない行\n=======\nどうでもよい\n>>>>>>> REPLACE\n";
        var settings = new Settings { ApplyMode = "partial" };
        var ctx = harness.MakeContext(1, settings);

        var dryRun = await harness.DryRunAsync(patch, ctx);
        dryRun.Plans.Count(p => p.CanApply).Should().Be(1, "前提: 1件だけ適用できる状態を作っている");
        dryRun.Stats.Files.Should().Be(2, "ドライラン時点の見積もりはパッチ全体を対象にしたままでよい");

        var applied = await harness.ApplyAsync(dryRun, ctx);

        applied.IsSuccess.Should().BeTrue();
        applied.Value.Entries.Should().HaveCount(1);
        applied.Value.Stats.Files.Should().Be(1,
            "履歴に残る「Nファイル」は実際に適用したエントリから数えること（B-10）");
    }

    [Fact(DisplayName = "B-10: チェックを外したブロックも履歴の「Nファイル」に数えない")]
    public async Task チェックを外したブロックはファイル数に数えない()
    {
        using var ws = new TempWorkspace();
        var harness = new ApplyHarness(ws);
        harness.WriteProjectText("a.txt", "aaa\n");
        harness.WriteProjectText("b.txt", "bbb\n");

        var patch =
            "<<<< FILE: a.txt\n<<<<<<< SEARCH\naaa\n=======\nAAA\n>>>>>>> REPLACE\n" +
            "<<<< FILE: b.txt\n<<<<<<< SEARCH\nbbb\n=======\nBBB\n>>>>>>> REPLACE\n";
        var settings = new Settings { ApplyMode = "partial" };
        var ctx = harness.MakeContext(1, settings);

        var dryRun = await harness.DryRunAsync(patch, ctx);
        // b.txt のチェックを外す（利用者が接ぎ木パネルでチェックを外した状態の再現）。
        var deselected = dryRun with
        {
            Plans = dryRun.Plans
                .Select(p => p.Path == "b.txt" ? p with { IsSelected = false } : p)
                .ToList(),
        };

        var applied = await harness.ApplyAsync(deselected, ctx);

        applied.IsSuccess.Should().BeTrue();
        applied.Value.Entries.Should().ContainSingle().Which.Path.Should().Be("a.txt");
        applied.Value.Stats.Files.Should().Be(1, "チェックを外したブロックは数に入れない（B-10）");
        harness.ProjectFileExists("b.txt").Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(harness.ReadProjectBytes("b.txt")).Should().Be("bbb\n",
            "前提: チェックを外したファイルは書き換わっていない");
    }

    // ==================================================================
    // B-5: フック出力の永続化（manifest.jsonへ保存しない）
    // ==================================================================

    /// <summary>
    /// 点検での指摘: <c>HookResult.Output</c>のコメントは「manifest には保存しない」と書いて
    /// あったのに、実装は普通のプロパティのままで、ビルド出力の全文がmanifest.jsonへ
    /// 永続化されていた。ビルド出力には絶対パス・環境変数・トークンが混じりえて、
    /// manifest.jsonはリビジョンごとに残り続ける。
    /// </summary>
    [Fact(DisplayName = "B-5: フックの標準出力はmanifest.jsonへ保存されない（コメントどおりの挙動）")]
    public void フック出力はJSONへ直列化されない()
    {
        var hook = new HookResult
        {
            Name = "ビルド",
            ExitCode = 1,
            DurationMs = 10,
            Output = "C:\\Users\\秘密のパス\\token=abcdef ビルドに失敗しました",
        };

        var json = JsonSerializer.Serialize(hook, JsonFileStore.DefaultOptions);

        json.Should().NotContain("token=abcdef",
            "ビルド出力には絶対パス・環境変数・トークンが混じりえるため永続化しない（B-5）");
        json.Should().NotContain("output", "outputフィールド自体を書き出さない");
        json.Should().Contain("ビルド", "フック名・終了コードなど、履歴として意味のある項目は残す");
        json.Should().Contain("exitCode");
    }

    // ==================================================================
    // B-2: 設定JSONの解析エラーが英語のまま出て、行番号が見切れる
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 画面には
    /// <c>JSONを解析できませんでした: '@' is an invalid start of a value. Path: $ | LineNumb…</c>
    /// と出ていた。英語であるうえ、唯一役に立つ行番号が右端で切れて読めなかった。
    /// </summary>
    [Fact(DisplayName = "B-2: JSONの解析エラーは日本語＋行・文字位置＋その位置の文字で示される")]
    public void JSON解析エラーが日本語で位置を示す()
    {
        var source = "{\n  \"theme\": \"dark\",\n  \"editor\": @\n}\n";
        var ex = Record.Exception(() => JsonSerializer.Deserialize<Settings>(source, JsonFileStore.DefaultOptions))
            as JsonException;
        ex.Should().NotBeNull("前提: このJSONは解析に失敗する");

        var message = ExceptionMessages.DescribeJsonParseFailure(ex!, source);

        message.Should().StartWith("E406", "エラーコード体系に乗せる（B-2）");
        message.Should().Contain("3行目", "行番号は必ず読める位置に出すこと");
        message.Should().Contain("「@」", "その位置に実際にあった文字まで示すこと");
        message.Should().NotContain("invalid start of a value", ".NETの英語メッセージをそのまま出さない");
    }

    [Fact(DisplayName = "B-2: 元テキストが無くても行番号までは日本語で示す")]
    public void JSON解析エラーは元テキスト無しでも行番号を出す()
    {
        var source = "{\n  \"editor\": @\n}\n";
        var ex = Record.Exception(() => JsonSerializer.Deserialize<Settings>(source, JsonFileStore.DefaultOptions))
            as JsonException;

        var message = ExceptionMessages.DescribeJsonParseFailure(ex!, sourceText: null);

        message.Should().Contain("2行目");
        message.Should().NotContain("invalid start of a value");
    }

    [Fact(DisplayName = "B-2: 日本語を含む行でも文字位置がバイト数でずれない")]
    public void JSON解析エラーの文字位置が日本語行でずれない()
    {
        // "あいう" はUTF-8で9バイト。BytePositionInLineをそのまま文字位置として扱うと大きくずれる。
        var source = "{\n  \"summary\": \"あいう\", \"editor\": @\n}\n";
        var ex = Record.Exception(() => JsonSerializer.Deserialize<Settings>(source, JsonFileStore.DefaultOptions))
            as JsonException;

        var message = ExceptionMessages.DescribeJsonParseFailure(ex!, source);

        message.Should().Contain("「@」",
            "バイト位置を文字位置へ換算していないと、@ではない別の文字を指してしまう（B-2）");
    }

    // ==================================================================
    // B-3: .NETの英語例外がそのまま検索の状態表示に出る
    // ==================================================================

    /// <summary>
    /// 修正前の挙動（対照）: 「正規表現が不正です: Invalid pattern 'foo(b…」と英語が出たうえ、
    /// 表示先（SearchView.axamlのStatusText）にTextWrapping指定が無く右端で見切れていた。
    /// </summary>
    [Fact(DisplayName = "B-3: 不正な正規表現のエラーは日本語の一言になる（英語の原文を含まない）")]
    public void 不正な正規表現のエラーが日本語になる()
    {
        var (regex, error) = SearchPatternBuilder.TryBuild("foo(b", useRegex: true, caseSensitive: false, wholeWord: false);

        regex.Should().BeNull();
        error.Should().NotBeNull();
        error.Should().StartWith("正規表現が不正です");
        error.Should().NotContain("Invalid pattern", ".NETの英語メッセージをそのまま出さない（B-3）");
        error.Should().Contain("かっこ", "次に何を確認すればよいかを示すこと");
    }
}
