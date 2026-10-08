using System.Text;
using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書11章の失敗時リカバリ支援と、4.10章の継続依頼プロンプトを生成する。
/// いずれもAIへ再投入するための日本語プレーンテキストを返す（クリップボードへのコピー自体は
/// 呼び出し側のUIが行う）。
///
/// 【修正依頼文に入れるもの】AIは会話の途中で自分が何を書いたかを正確には覚えていない
/// （長い会話では特に）。以前の文面は「パス — 理由」と現在のコードの抜粋だけで、
/// 失敗したSEARCH本文・置換後の内容・出力形式の指示がいずれも無く、AIは
/// 「自分が何を書いて失敗したのか」が分からないまま、形式もあいまいに再出力することになっていた。
/// そこで各失敗ブロックに次の4点を必ず添える。
/// <list type="number">
/// <item>失敗の理由（従来どおり）。</item>
/// <item>失敗したSEARCH部の本文（AIが出したものをそのまま。長ければ <see cref="MaxQuotedLines"/> 行に中略）。</item>
/// <item>置換後（REPLACE）の本文。直したいのはSEARCHだけで、変更の意図は保ちたいので併記する。</item>
/// <item>現在のコード（SEARCHに最も似ている箇所の前後 <see cref="ContextLines"/> 行。従来どおり）。</item>
/// </list>
/// 加えて、受け取ったパッチの形式（<see cref="PatchFormat"/>）に合わせて「同じ形式で、失敗した
/// ブロックだけを出し直す」ことと、その形式の骨組みを明記する。形式を指定しないと、AIが
/// 別の形式（例: 標準SR形式で渡したのにGraft独自形式）で答え直し、貼り付け直したときに
/// 意図しない解析経路に乗ってしまう。
/// </summary>
public static class RecoveryPrompt
{
    /// <summary>現在のコードとして、SEARCHに最も似ている箇所の前後に付ける行数（従来から変更なし）。</summary>
    private const int ContextLines = 20;

    /// <summary>引用するSEARCH/REPLACE本文の、先頭側に残す行数。</summary>
    public const int QuotedHeadLines = 20;

    /// <summary>引用するSEARCH/REPLACE本文の、末尾側に残す行数。</summary>
    public const int QuotedTailLines = 20;

    /// <summary>
    /// 引用する本文の最大行数（<see cref="QuotedHeadLines"/> + <see cref="QuotedTailLines"/>）。これを超えると
    /// 先頭と末尾を残して中間を中略する。
    ///
    /// 【上限40行の根拠】(1) Graftのプロンプトは「SEARCHは一意に特定できる最小限の行数」と
    /// 指示しており（PromptTemplateStore）、正しく書かれたSEARCHは数行〜数十行に収まる。それを超える
    /// ものは、巨大な関数をまるごと貼ったような失敗例で、中間を見せなくてもAIは先頭と末尾から
    /// 「どこを狙ったSEARCHか」を特定できる。(2) 現在のコードの抜粋が既に「最も似ている箇所の
    /// 前後20行」＝最大で SEARCH行数＋40行 になるため、引用側も同じ桁（先頭20＋末尾20）に揃えると
    /// 1ブロックあたりの量の釣り合いが取れる。(3) 1行40文字前後の概算で40行は約1,600文字であり、
    /// SEARCH・REPLACE・現在のコードの3つを合わせても1ブロック数千文字に収まるため、失敗ブロックが
    /// 十数件あっても1回のチャットに貼れる範囲に収まる。厳密な実測値ではなく、この3点から決めた設計値。
    /// </summary>
    public const int MaxQuotedLines = QuotedHeadLines + QuotedTailLines;

    /// <summary>
    /// 引用する1行の最大文字数。ミニファイされたJS・巨大なJSON・1行のログなど、1行が数十万文字に
    /// なりうるファイルでも、修正依頼文がクリップボードやチャットに貼れない大きさにならないようにする。
    /// 通常のソースコードの1行（長くても100〜200文字）を切らない余裕を持たせた値。
    /// </summary>
    public const int MaxQuotedLineChars = 300;

    /// <summary>11章 失敗ブロックの再依頼文を生成する。</summary>
    public static string Build(IReadOnlyList<BlockPlan> failedPlans, Func<string, string?> readCurrentText)
    {
        var sb = new StringBuilder();
        AppendLine(sb, "以下のブロックの適用に失敗しました。それぞれについて、失敗した内容（あなたが出力したSEARCH部）と、");
        AppendLine(sb, "現在の実際のコードを示します。現在のコードに一致するSEARCH部に直して、");
        AppendLine(sb, "失敗したブロックだけを出し直してください。適用に成功したブロックは再出力しないでください。");

        AppendFormatInstructions(sb, failedPlans);

        var formats = DistinctFormats(failedPlans);
        foreach (var plan in failedPlans)
        {
            AppendLine(sb, string.Empty);
            AppendBlockSection(sb, plan, readCurrentText, showFormat: formats.Count > 1);
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 4.10 切断時の継続依頼プロンプトを生成する。末尾3行のみを含める。
    /// <paramref name="format"/> は受け取ったパッチの形式（<see cref="Patch.Format"/>）で、
    /// 「同じ形式で続けて」とAIに頼むために使う。以前は常に「同じGraft形式で」と書いており、
    /// 標準SR形式やunified diffで受け取った出力の続きまで Graft 形式で頼んでしまっていた。
    /// </summary>
    public static string BuildContinuation(IReadOnlyList<string> tailLines, PatchFormat format = PatchFormat.Graft)
    {
        var sb = new StringBuilder();
        AppendLine(sb, $"出力が途中で切れています。以下の続きから、同じ{FormatLabel(format)}で出力してください。");
        AppendLine(sb, ContinuationHint(format));
        AppendLine(sb, "最後に受け取った行:");

        var tail = tailLines.Count <= 3 ? tailLines : tailLines.Skip(tailLines.Count - 3).ToList();
        foreach (var line in tail)
        {
            AppendLine(sb, line);
        }

        return sb.ToString().TrimEnd('\n');
    }

    // ------------------------------------------------------------------
    // 形式ごとの指示
    // ------------------------------------------------------------------

    private static string FormatLabel(PatchFormat format) => format switch
    {
        PatchFormat.StandardSearchReplace => "標準SEARCH/REPLACE形式",
        PatchFormat.UnifiedDiff => "unified diff形式",
        _ => "Graft形式",
    };

    private static string ContinuationHint(PatchFormat format) => format switch
    {
        PatchFormat.StandardSearchReplace =>
            "（ファイルパスだけの行、SEARCH〜REPLACEのマーカー、の並びを崩さないでください）",
        PatchFormat.UnifiedDiff =>
            "（--- / +++ のファイルヘッダと @@ のハンク見出しの並びを崩さないでください）",
        _ =>
            "（<<<< で始まるブロックのヘッダと、対応する終了マーカーの並びを崩さないでください）",
    };

    private static IReadOnlyList<PatchFormat> DistinctFormats(IReadOnlyList<BlockPlan> plans)
        => plans.Select(p => p.Block.SourceFormat).Distinct().ToList();

    /// <summary>
    /// 出力形式の指示。失敗ブロックの形式が1種類ならその1つだけ、混在（パッチキューの結合など）なら
    /// 形式ごとに、対象のファイルを添えて書く。骨組みはコードブロックに入れる：クリップボード監視
    /// （PatchTextDetector）が、この修正依頼文そのものを「パッチ」と誤検知しないよう、
    /// マーカー行はすべて閉じたコードフェンスの内側に置く。
    /// </summary>
    private static void AppendFormatInstructions(StringBuilder sb, IReadOnlyList<BlockPlan> plans)
    {
        var formats = DistinctFormats(plans);
        foreach (var format in formats)
        {
            AppendLine(sb, string.Empty);
            if (formats.Count > 1)
            {
                var paths = plans.Where(p => p.Block.SourceFormat == format).Select(p => p.Path).Distinct();
                AppendLine(sb, $"【出力形式: {FormatLabel(format)}】（対象: {string.Join("、", paths)}）");
            }
            else
            {
                AppendLine(sb, "【出力形式】");
            }

            AppendLine(sb, FormatInstruction(format));
            AppendFenced(sb, FormatSkeleton(format));
        }
    }

    private static string FormatInstruction(PatchFormat format) => format switch
    {
        PatchFormat.StandardSearchReplace =>
            "受け取ったパッチと同じ標準SEARCH/REPLACE形式で出力してください。ファイルパスだけの行に続けて、" +
            "SEARCH〜REPLACEのブロックを置きます。失敗したブロックだけを出し直し、別の形式は使わないでください。",
        PatchFormat.UnifiedDiff =>
            "受け取ったパッチと同じunified diff形式で出力してください。失敗したハンクだけを出し直し、" +
            "ハンクの変更前の行（空白で始まる文脈行と - の行）が現在のコードと完全に一致するようにしてください。" +
            "別の形式は使わないでください。",
        _ =>
            "受け取ったパッチと同じGraft形式で出力してください。失敗したブロックだけを出し直し、" +
            "別の形式は使わないでください。",
    };

    private static string FormatSkeleton(PatchFormat format) => format switch
    {
        PatchFormat.StandardSearchReplace =>
            "相対パス\n" +
            "<<<<<<< SEARCH\n" +
            "（現在のコードに一致する修正前のコード）\n" +
            "=======\n" +
            "（修正後のコード）\n" +
            ">>>>>>> REPLACE",
        PatchFormat.UnifiedDiff =>
            "--- a/相対パス\n" +
            "+++ b/相対パス\n" +
            "@@ -開始行,行数 +開始行,行数 @@\n" +
            " （変更しない文脈行）\n" +
            "-（削除する行）\n" +
            "+（追加する行）",
        _ =>
            "<<<< FILE: 相対パス\n" +
            "<<<<<<< SEARCH  # このペアの変更内容を1行で\n" +
            "（現在のコードに一致する修正前のコード）\n" +
            "=======\n" +
            "（修正後のコード）\n" +
            ">>>>>>> REPLACE",
    };

    // ------------------------------------------------------------------
    // 各失敗ブロック
    // ------------------------------------------------------------------

    private static void AppendBlockSection(
        StringBuilder sb, BlockPlan plan, Func<string, string?> readCurrentText, bool showFormat)
    {
        var issue = plan.Issues.FirstOrDefault(i => i.Severity == Severity.Error) ?? plan.Issues.FirstOrDefault();
        var formatNote = showFormat ? $"（{FormatLabel(plan.Block.SourceFormat)}）" : string.Empty;
        AppendLine(sb, $"■ {plan.Path} — {ReasonText(issue)}{formatNote}");
        if (!string.IsNullOrWhiteSpace(plan.Description))
        {
            AppendLine(sb, $"変更の意図: {plan.Description}");
        }

        if (plan.Block is not SearchReplaceBlock srBlock || srBlock.Pairs.Count == 0)
        {
            // SEARCH部を持たない種類のブロック（全文・削除・改名など）。引用できる本文が無いので、
            // その旨を明記する（以前は「現在のコードを取得できませんでした」と、取得に失敗した
            // かのような誤解を招く文言になっていた）。
            AppendLine(sb, $"このブロックは{DescribeKind(plan.Block.Kind)}のため、SEARCH部の引用と現在のコードの抜粋はありません。");
            return;
        }

        var pairs = ResolvePairs(plan, srBlock, issue);
        for (var i = 0; i < pairs.Count; i++)
        {
            if (pairs.Count > 1) AppendLine(sb, $"（{i + 1}/{pairs.Count}個目のSEARCH部）");
            AppendPair(sb, plan, pairs[i], srBlock, readCurrentText);
        }
    }

    private static void AppendPair(
        StringBuilder sb, BlockPlan plan, SearchReplacePair pair, PatchBlock block, Func<string, string?> readCurrentText)
    {
        var isDiff = block.SourceFormat == PatchFormat.UnifiedDiff;
        var searchLines = TextNormalizer.SplitLines(pair.SearchText);

        AppendLine(sb, isDiff
            ? $"失敗したハンクの変更前のコード（SEARCH相当・{searchLines.Count}行）:"
            : $"失敗したSEARCH部（{searchLines.Count}行）:");
        AppendFenced(sb, Excerpt(searchLines));

        var replaceLines = TextNormalizer.SplitLines(pair.ReplaceText);
        AppendLine(sb, replaceLines.Count == 0
            ? "置換後（REPLACE）: 空（この部分を削除する変更です）"
            : $"置換後（REPLACE・{replaceLines.Count}行。この変更の意図は保ってください）:");
        if (replaceLines.Count > 0) AppendFenced(sb, Excerpt(replaceLines));

        var currentText = searchLines.Count > 0 ? readCurrentText(plan.Path) : null;
        if (string.IsNullOrEmpty(currentText))
        {
            AppendLine(sb, "現在のコードを取得できませんでした。");
            return;
        }

        var fileLines = TextNormalizer.SplitLines(currentText);
        var (startLine, endLine, snippet) = ExtractContext(fileLines, searchLines);
        AppendLine(sb, $"現在のコード（{startLine}〜{endLine}行目）:");
        AppendFenced(sb, snippet);
    }

    private static string DescribeKind(BlockKind kind) => kind switch
    {
        BlockKind.FullContent => "ファイル全文の書き込み",
        BlockKind.Delete => "ファイルの削除",
        BlockKind.Rename => "ファイルの移動・改名",
        BlockKind.Mkdir => "フォルダの作成",
        BlockKind.Append => "末尾への追記",
        BlockKind.Prepend => "先頭への挿入",
        _ => "SEARCH/REPLACE以外の操作",
    };

    private static string ReasonText(GraftIssue? issue)
    {
        if (issue is null) return "適用に失敗しました";
        // 仕様書11章の例文と一致させる（カタログの言い切り表現とは語尾のみ異なる）。
        return issue.Code == ErrorCode.E101 ? "SEARCH部が見つかりません" : issue.Summary;
    }

    /// <summary>
    /// このプランが指すSEARCH/REPLACEペアを求める。ペア単位の失敗なら <see cref="BlockPlan.Pair"/> が
    /// そのまま答え。ファイル単位の失敗（対象ファイルが無い等）で Pair が null のときは、
    /// エラーの行番号が指すペアを探し、それも決まらず複数ペアがあるなら全ペアを示す
    /// （以前は黙って先頭のペアだけを使っており、2個目以降のSEARCHが依頼文から消えていた）。
    /// </summary>
    private static IReadOnlyList<SearchReplacePair> ResolvePairs(BlockPlan plan, SearchReplaceBlock block, GraftIssue? issue)
    {
        if (plan.Pair is { } own) return new[] { own };

        if (issue?.LineNumber is int line
            && block.Pairs.FirstOrDefault(p => p.SourceLine == line) is { } matched)
        {
            return new[] { matched };
        }

        return block.Pairs;
    }

    // ------------------------------------------------------------------
    // 本文の引用（中略）と現在のコードの抜粋
    // ------------------------------------------------------------------

    /// <summary>
    /// 本文を引用用に整える。<see cref="MaxQuotedLines"/> 行を超えるときは先頭 <see cref="QuotedHeadLines"/> 行と
    /// 末尾 <see cref="QuotedTailLines"/> 行を残して中間を1行の注記に置き換え、省略した行数を明示する
    /// （AIが「元の本文はもっと長い」と分かり、勝手に中間を補って書き足さないようにするため）。
    /// 1行が <see cref="MaxQuotedLineChars"/> 文字を超えるときも同様に打ち切って注記する。
    /// </summary>
    public static string Excerpt(IReadOnlyList<string> lines)
    {
        IEnumerable<string> shown;
        if (lines.Count <= MaxQuotedLines)
        {
            shown = lines.Select(ClipLine);
        }
        else
        {
            var omitted = lines.Count - QuotedHeadLines - QuotedTailLines;
            shown = lines.Take(QuotedHeadLines).Select(ClipLine)
                .Append($"（中略: 中間の{omitted}行を省略。全体は{lines.Count}行）")
                .Concat(lines.Skip(lines.Count - QuotedTailLines).Select(ClipLine));
        }

        var text = string.Join("\n", shown);
        return text.Length == 0 ? "（空）" : text;
    }

    private static string ClipLine(string line)
        => line.Length <= MaxQuotedLineChars
            ? line
            : line[..MaxQuotedLineChars] + $"…（この行は長いため{MaxQuotedLineChars}文字目以降を省略。全体は{line.Length}文字）";

    private static (int StartLine, int EndLine, string Snippet) ExtractContext(
        IReadOnlyList<string> fileLines, IReadOnlyList<string> searchLines)
    {
        if (fileLines.Count == 0)
        {
            return (0, 0, string.Empty);
        }

        // 閾値0で常に「最も類似する箇所」を採用する（要確認扱いにはしない。あくまで文面生成用）。
        var best = SimilarityScorer.FindBestMatch(fileLines, searchLines, threshold: 0.0);
        var startLine = best?.StartLine ?? 0;
        var lineCount = best?.LineCount ?? Math.Min(searchLines.Count, fileLines.Count);

        var contextStart = Math.Max(0, startLine - ContextLines);
        var contextEndExclusive = Math.Min(fileLines.Count, startLine + lineCount + ContextLines);
        // 現在のコードも、1行が極端に長いファイル（ミニファイ済み等）で文面が膨れないよう同じ上限で切る。
        var snippet = string.Join("\n", fileLines.Skip(contextStart).Take(contextEndExclusive - contextStart).Select(ClipLine));
        return (contextStart + 1, contextEndExclusive, snippet);
    }

    // ------------------------------------------------------------------
    // 文字列組み立て
    // ------------------------------------------------------------------

    /// <summary>
    /// 本文をコードフェンスで囲む。本文中に ``` が含まれていても囲みが途中で閉じないよう、
    /// 本文中の最長のバッククォート連続より1つ長いフェンスを使う（Markdownの規則）。
    /// </summary>
    private static void AppendFenced(StringBuilder sb, string body)
    {
        var fence = new string('`', Math.Max(3, LongestBacktickRun(body) + 1));
        AppendLine(sb, fence);
        AppendLine(sb, body);
        AppendLine(sb, fence);
    }

    private static int LongestBacktickRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var c in text)
        {
            current = c == '`' ? current + 1 : 0;
            if (current > longest) longest = current;
        }
        return longest;
    }

    private static void AppendLine(StringBuilder sb, string text) => sb.Append(text).Append('\n');
}
