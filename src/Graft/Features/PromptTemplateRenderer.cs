using Graft.Core;

namespace Graft.Features;

/// <summary>
/// 仕様書4.8.2の変数展開を行う。<c>{{files}}</c>・<c>{{tree}}</c> の展開は
/// <see cref="ContextCollector"/> の <c>BuildFilesTextAsync</c>・<c>BuildTreeTextAsync</c> を
/// そのまま呼び出すことで、4.8.4「コンテキスト収集とは同一の出力パイプラインを共有する」を満たす。
/// </summary>
public sealed class PromptTemplateRenderer
{
    private readonly ContextCollector _collector;

    public PromptTemplateRenderer(ContextCollector collector)
    {
        _collector = collector;
    }

    /// <summary>
    /// <see cref="RenderAsync"/>が{{files}}を持たないテンプレートの末尾に足すファイル部分の見出し。
    /// 組み込みの「修正依頼」「調査依頼」テンプレートが{{files}}の直前に置いている見出しと同じ表記にし、
    /// 付けた場合と本文に{{files}}がある場合で、AIが受け取る形が変わらないようにしてある。
    /// </summary>
    internal const string AppendedFilesHeading = "# 対象ファイル";

    /// <summary>依頼の観点を差し込む変数（仕様書_ブックの情報と依頼の観点 6.3）。</summary>
    internal const string FocusVariable = "{{focus}}";

    /// <summary>
    /// テンプレート本文中の {{standingContext}} {{tree}} {{files}} {{projectName}} {{lastRevision}}
    /// を実際の値へ展開する。テンプレートが使わない変数の収集処理は呼び出さない
    /// （調査依頼テンプレートで {{tree}} を使わない場合にツリー走査をしない等）。
    ///
    /// 【appendFilesIfAbsent: 指示文とファイルを1回のコピーで渡す】既定の「初回用（完全版）」のように
    /// {{files}} を含まないテンプレートでは、指示文のコピーとファイルのコピーを別々に行って
    /// 2回貼る必要があった。true のときは、本文に {{files}} が**無い**場合に限り、展開後の末尾へ
    /// 「# 対象ファイル」の見出しとコンテキスト収集の選択（{{files}} と同じ内容）を足す。
    /// 本文に {{files}} がある場合は何もしない（二重に付かない）。**テンプレートの本文そのもの
    /// （組み込み・利用者が編集したもの）は書き換えず**、展開結果にだけ足す。
    /// 選択ファイルが1つも無い・収集モードがファイルを出さない設定（ツリーのみ）のように
    /// 足す中身が空のときは、見出しだけが残らないよう何も足さない。
    ///
    /// 【依頼の観点（<see cref="ContextRequest.FocusText"/>）の差し込み】仕様書6.3。
    /// <list type="bullet">
    /// <item>観点が空（何も選んでいない）のとき、<c>{{focus}}</c> を含まないテンプレートの出力は従来と
    /// <b>1バイトも変わらない</b>。<c>{{focus}}</c> を含むテンプレートでは、その変数が前後の空行ごと消える。</item>
    /// <item>観点があり <c>{{focus}}</c> を含むなら、そこへ展開する。</item>
    /// <item>含まないなら次の順で位置を決める: ①「# 対象ファイル」だけの行の直前 ②{{files}} を含む行の直前
    /// ③自動付加する「# 対象ファイル」の直前 ④末尾。テンプレートの本文そのものは書き換えない。</item>
    /// </list>
    /// 【再展開を防ぐ約束】観点の文は、ほかの変数の展開が<b>済んだあと</b>に差し込む。<c>{{focus}}</c>（と①②で
    /// 決めた位置）を先に、本文に出てこない目印の文字列へ置き換えておき、最後に目印を観点の文へ戻す。理由は2つ:
    /// ファイルの中身に <c>{{focus}}</c> という文字列があっても置換されないこと、自作の観点の文に
    /// <c>{{files}}</c> などと書かれていても再展開されないこと。
    /// </summary>
    public async Task<GraftResult<string>> RenderAsync(
        PromptTemplate template, ContextRequest request, string? lastRevisionSummary, CancellationToken ct = default,
        bool appendFilesIfAbsent = false)
    {
        var body = template.Body;
        var issues = new List<GraftIssue>();
        var hasFilesVariable = body.Contains("{{files}}", StringComparison.Ordinal);

        // ---- 依頼の観点: 他の変数を展開する前に、差し込み位置だけ目印にしておく ----
        var focusText = request.FocusText;
        var hasFocus = !string.IsNullOrEmpty(focusText);
        var focusBlock = hasFocus ? focusText.TrimEnd('\r', '\n') : string.Empty;
        var deferred = new List<(string Marker, string Text)>();
        var focusPlacedInBody = false;
        if (body.Contains(FocusVariable, StringComparison.Ordinal))
        {
            if (hasFocus)
            {
                var marker = NewMarker();
                body = body.Replace(FocusVariable, marker, StringComparison.Ordinal);
                deferred.Add((marker, focusBlock));
            }
            else
            {
                body = RemoveEmptyFocusVariable(body);
            }
            focusPlacedInBody = true;
        }
        else if (hasFocus && FindFocusInsertIndex(body) is var at and >= 0)
        {
            var marker = NewMarker();
            body = body.Insert(at, marker);
            // 差し込み位置は行頭。次の見出しと詰まらないよう、観点の後ろに空行を1つ入れる。
            deferred.Add((marker, focusBlock + "\n\n"));
            focusPlacedInBody = true;
        }

        if (body.Contains("{{tree}}", StringComparison.Ordinal))
        {
            var tree = await _collector.BuildTreeTextAsync(request.Project, request.Settings, ct).ConfigureAwait(false);
            if (!tree.IsSuccess) return GraftResult<string>.Fail(tree.Issues);
            body = body.Replace("{{tree}}", tree.Value, StringComparison.Ordinal);
            issues.AddRange(tree.Issues);
        }

        if (body.Contains("{{files}}", StringComparison.Ordinal))
        {
            var files = await _collector.BuildFilesTextAsync(request, ct).ConfigureAwait(false);
            if (!files.IsSuccess) return GraftResult<string>.Fail(files.Issues);
            body = body.Replace("{{files}}", files.Value, StringComparison.Ordinal);
            issues.AddRange(files.Issues);
        }

        string? appendedFiles = null;
        if (appendFilesIfAbsent && !hasFilesVariable)
        {
            var files = await _collector.BuildFilesTextAsync(request, ct).ConfigureAwait(false);
            if (!files.IsSuccess) return GraftResult<string>.Fail(files.Issues);
            issues.AddRange(files.Issues);
            if (!string.IsNullOrWhiteSpace(files.Value)) appendedFiles = files.Value;
        }

        body = body
            .Replace("{{standingContext}}", request.Project.StandingContext ?? string.Empty, StringComparison.Ordinal)
            .Replace("{{projectName}}", request.Project.Name, StringComparison.Ordinal)
            .Replace("{{lastRevision}}", lastRevisionSummary ?? string.Empty, StringComparison.Ordinal);

        // 足すのは変数の展開が済んだあと。ファイルの中身に "{{standingContext}}" のような文字列が
        // 含まれていても、置換の対象にならないようにするため。
        // 観点を本文の中に置けなかったとき（③自動付加する見出しの直前・④末尾）。
        var focusBeforeAppended = hasFocus && !focusPlacedInBody;
        if (appendedFiles is not null)
        {
            var head = body.TrimEnd('\r', '\n') + "\n\n";
            if (focusBeforeAppended) head += focusBlock + "\n\n";
            body = head + AppendedFilesHeading + "\n" + appendedFiles;
        }
        else if (focusBeforeAppended)
        {
            var head = body.TrimEnd('\r', '\n');
            body = (head.Length == 0 ? string.Empty : head + "\n\n") + focusBlock + "\n";
        }

        // 最後に目印を観点の文へ戻す。ここより後ろで置換をしないので、観点の文は再展開されない。
        foreach (var (marker, text) in deferred)
        {
            body = body.Replace(marker, text, StringComparison.Ordinal);
        }

        return GraftResult<string>.Ok(body, issues);
    }

    /// <summary>
    /// 観点を差し込む位置（本文の文字位置）を求める。①「# 対象ファイル」だけの行の行頭、
    /// 無ければ ②{{files}} を含む最初の行の行頭。どちらも無ければ -1（③④は呼び出し側が末尾側で処理する）。
    /// </summary>
    private static int FindFocusInsertIndex(string body)
    {
        var filesLineStart = -1;
        var lineStart = 0;
        while (lineStart <= body.Length)
        {
            var lineEnd = body.IndexOf('\n', lineStart);
            if (lineEnd < 0) lineEnd = body.Length;
            var line = body.AsSpan(lineStart, lineEnd - lineStart).Trim();

            if (line.SequenceEqual(AppendedFilesHeading)) return lineStart;
            if (filesLineStart < 0 && line.Contains("{{files}}", StringComparison.Ordinal)) filesLineStart = lineStart;

            if (lineEnd >= body.Length) break;
            lineStart = lineEnd + 1;
        }
        return filesLineStart;
    }

    /// <summary>
    /// 観点が空のとき、<c>{{focus}}</c> を痕跡なしで消す。変数だけの行は行ごと消し、その前後が
    /// どちらも空行（または端）なら空行が2つ続かないよう1つ詰める。行の途中にあるものは文字だけ消す。
    /// </summary>
    private static string RemoveEmptyFocusVariable(string body)
    {
        var lines = body.Split('\n').ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() != FocusVariable) continue;

            lines.RemoveAt(i);
            var previousBlank = i == 0 || lines[i - 1].Trim().Length == 0;
            if (previousBlank && i < lines.Count && lines[i].Trim().Length == 0) lines.RemoveAt(i);
            i--;
        }
        return string.Join('\n', lines).Replace(FocusVariable, string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// 本文・ファイルの中身・利用者の指示文のどこにも現れない目印。私用領域の文字で挟み、
    /// 呼び出しごとに別の値にする（ファイルの中身に偶然同じ文字列があっても取り違えない）。
    /// </summary>
    private static string NewMarker() => "\uE000graft-focus-" + Guid.NewGuid().ToString("N") + "\uE001";
}
