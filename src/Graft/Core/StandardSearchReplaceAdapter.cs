using System.Linq;

namespace Graft.Core;

/// <summary>
/// 標準的な SEARCH/REPLACE ブロック形式（以下「標準SR形式」）のパッチ本文を、既存の
/// SEARCH/REPLACE ペア・FULL形式ブロック・DELETEブロックへ変換するアダプタ（仕様書5.2）。
///
/// 【なぜこの形式を受け付けるか】
/// 5.1（unified diff 入力対応）と同じ理由。形式指示の無い会話で、AIが Graft 独自形式に
/// 従わないことが実際に起きている。標準SR形式は Aider が広め Cline・Roo Code 等も採用した
/// 事実上の標準で、マーカーが git のマージコンフリクトマーカーそのもの（<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; </c>／
/// <c>=======</c>／<c>&gt;&gt;&gt;&gt;&gt;&gt;&gt; </c>）であるため、学習データに大量に含まれておりLLMが自然に出せる。
///
/// 【「標準」の裏取り（2026-08 時点。Aider公式ドキュメント・Aider本体のプロンプト実装を確認）】
/// - ファイルパスは「フェンス付きコードブロックの直前に、パスだけを単独の行として」置く
///   （Aider: "The *FULL* file path alone on a line, verbatim. No bold asterisks, no quotes
///   around it, no escaping of characters, etc."）。Graft独自形式の <c>&lt;&lt;&lt;&lt; FILE: パス</c> とは
///   ここが決定的に違い、<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH</c> の行自体は両形式で完全に共通である。
/// - 新規ファイルの作成は「SEARCH部を空にする」で表す（Aider: "an empty `SEARCH` section,
///   the new file's contents in the `REPLACE` section"）。**素の標準SR形式に
///   <c>NEW_FILE</c> / <c>WHOLE_FILE</c> / <c>DELETE_FILE</c> というマーカーは存在しない**。
/// - ファイル削除・改名は素の標準SR形式の守備範囲外（Aiderは応答末尾のシェルコマンドで表す）。
///
/// 【それでも NEW_FILE / WHOLE_FILE / DELETE_FILE を受け付ける理由】
/// 利用者の会社で運用されている「SRルール」がこの3つのマーカーを独自拡張として定めており、
/// 実際にその形でAIへ指示が出ている。素の標準（空SEARCH方式）と会社ルール（マーカー方式）の
/// **どちらで来ても正しく解釈できる**ことが利用者にとって最善のため、両方を受け付ける。
/// どちらの表現も内部表現は同じ（新規作成・全面書き換えはいずれもFULL形式ブロック）。
///
/// 【変換方針】
/// <see cref="UnifiedDiffAdapter"/> と同じく「既存の内部表現へ変換するだけ」のアダプタとして
/// 実装し、マッチング（6章）・プレビュー・適用（7章）・リビジョン（8章）の既存パイプラインを
/// そのまま再利用する。Graft独自形式の解析（4章・<see cref="PatchParser"/>）には一切手を入れない
/// ため、履歴やパッチキューに残っている既存データの解釈は変わらない。
/// </summary>
public static class StandardSearchReplaceAdapter
{
    // パッチメタ（4.2）に相当する情報が標準SR形式には無いため、UnifiedDiffAdapter の前例に
    // 倣って固定文言を補い、requireSummary 設定（15章）の必須チェックに引っかからないようにする。
    private const string ImportSummary = "標準SEARCH/REPLACE形式からの取り込み";
    private const string ImportType = "chore";

    /// <summary>SEARCHブロックの開始マーカー（標準・Graft共通）。</summary>
    private const string SearchMarker = "<<<<<<< SEARCH";
    /// <summary>アンカー省略記法（Graft拡張。4.4）の開始マーカー。</summary>
    private const string SearchRangeMarker = "<<<<<<< SEARCH-RANGE";
    /// <summary>SEARCH部とREPLACE部の区切り。</summary>
    private const string Divider = "=======";
    /// <summary>REPLACE部の終了マーカー。</summary>
    private const string ReplaceEndMarker = ">>>>>>> REPLACE";

    /// <summary>会社ルール【2】新規ファイルの作成。</summary>
    private const string NewFileMarker = "<<<<<<< NEW_FILE";
    /// <summary>会社ルール【3】既存ファイルの全面書き換え。</summary>
    private const string WholeFileMarker = "<<<<<<< WHOLE_FILE";
    /// <summary>会社ルール【4】ファイルの削除。</summary>
    private const string DeleteFileMarker = "<<<<<<< DELETE_FILE";
    /// <summary>会社ルール【2】〜【4】に共通の終了マーカー。</summary>
    private const string EndFileMarker = ">>>>>>> END_FILE";

    /// <summary>
    /// SEARCHを正確に作れないときにAIが1行だけ返す合図（会社ルールの運用規定）。
    /// これを黙って「ブロックが存在しない」（E001）として扱うと、利用者には
    /// 「AIが変な出力をした」としか見えず、実際には「AIが情報不足を訴えている」という
    /// 全く違う状況であることが伝わらないため、専用のエラーコード（E710）で区別する。
    /// </summary>
    public const string NeedMoreContextToken = "NEED_MORE_CONTEXT";

    // ------------------------------------------------------------------
    // 判定
    // ------------------------------------------------------------------

    /// <summary>
    /// テキストが「NEED_MORE_CONTEXT の1行だけ」かどうか。コードフェンス行・空行は無視する
    /// （会社ルールでは出力全体を1つの text フェンスで囲むため、フェンス付きで届くのが通常）。
    /// 誤検知を避けるため、その1行以外の内容が1つでもあれば false とする。
    /// </summary>
    public static bool IsNeedMoreContext(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var meaningful = PatchTextUtil.SplitRawLines(text)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("```", StringComparison.Ordinal))
            .ToList();

        return meaningful.Count == 1 && meaningful[0] == NeedMoreContextToken;
    }

    /// <summary>
    /// 標準SR形式の開始マーカーが行頭に1つでもあるかどうかの安価な判定。
    ///
    /// 【注意】<c>&lt;&lt;&lt;&lt;&lt;&lt;&lt; SEARCH</c> は Graft独自形式と**共通**のマーカーであるため、
    /// これだけで「標準SR形式である」と決めてはならない。<see cref="PatchParser.Parse"/> は
    /// Graft独自形式にしか無いヘッダ（<c>&lt;&lt;&lt;&lt; FILE:</c> 等。
    /// <see cref="PatchTextDetector.HasGraftOwnMarker"/>）が1つも無いことを先に確かめてから
    /// この判定を使う。
    /// </summary>
    public static bool HasStandardMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var raw in PatchTextUtil.SplitRawLines(text))
        {
            if (IsBlockOpenMarker(raw.TrimEnd())) return true;
        }
        return false;
    }

    /// <summary>
    /// 標準SR形式のブロックが「開始マーカーと終了マーカーの対」として最後まで揃っているものが
    /// 1つでもあるかどうか。
    ///
    /// 【なぜ <see cref="HasStandardMarker"/> と別に用意するか】クリップボード監視（10章）で
    /// 「パス指定行が実在しそうか」を要求する対象を、閉じたブロックを持つテキストだけに
    /// 絞り込むため。開始マーカーが1行あるだけのテキスト（切れた出力の断片など）は、
    /// パス指定行が無くて当然であり、これに実在らしさを要求すると従来検知できていた
    /// 断片を取りこぼす（<c>PatchTextDetectorTests</c> の「ブロックヘッダを含むテキストは
    /// パッチとみなす」参照）。一方、既定プロンプトテンプレートのように
    /// 「ブロックが最後まで揃っているのにパスだけが仮の文字列（"相対パス"）」という
    /// テキストこそが誤検知の実害であり、そこだけを狙って弾ける。
    /// </summary>
    public static bool HasCompleteStandardBlock(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var lines = PatchTextUtil.SplitRawLines(text);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd();
            if (!IsBlockOpenMarker(trimmed)) continue;

            var isSearch = trimmed.StartsWith(SearchMarker, StringComparison.Ordinal)
                || trimmed.StartsWith(SearchRangeMarker, StringComparison.Ordinal);
            var terminator = isSearch ? ReplaceEndMarker : EndFileMarker;

            for (var j = i + 1; j < lines.Length; j++)
            {
                if (lines[j].TrimEnd() == terminator) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 「パスだけの行」として通用しそうな行が1つでもあるかどうか。クリップボード監視の
    /// 誤検知防止（10章・<see cref="PatchTextDetector"/>）で使う。判定基準は
    /// <see cref="TryParsePathLine"/> と同じ。
    /// </summary>
    public static bool HasPlausiblePathLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var raw in PatchTextUtil.SplitRawLines(text))
        {
            if (TryParsePathLine(raw, out _)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // 解析
    // ------------------------------------------------------------------

    /// <summary>
    /// 標準SR形式の本文を解析し <see cref="Patch"/> を組み立てる。
    /// </summary>
    public static GraftResult<Patch> Parse(string patchText)
    {
        var lines = PatchTextUtil.SplitRawLines(patchText);
        var state = new ParseState();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd();

            // ブロックの外側にあるコードフェンス行だけを読み飛ばす。
            // 【なぜ UnifiedDiffAdapter のように一律で剥がさないか】標準SR形式では
            // REPLACE本文・NEW_FILE本文が「ファイルの中身そのもの」であり、Markdownファイルを
            // 編集する場合は本文が正当に ``` を含む。unified diff は全ての本文行が
            // ' ' '-' '+' のいずれかで始まるため一律除去でも実害が出にくいが、
            // こちらは本文が丸裸のため、外側だけを取り除く必要がある。
            if (trimmed.StartsWith("```", StringComparison.Ordinal)) continue;

            if (!IsBlockOpenMarker(trimmed))
            {
                // ブロックの外側。パス指定行の候補として覚えておく（直近のものが有効）。
                if (TryParsePathLine(line, out var candidate)) state.PendingPath = candidate;
                continue;
            }

            i = ParseBlock(lines, i, trimmed, state);
        }

        return state.Build(patchText);
    }

    /// <summary>
    /// 開始マーカー行（<paramref name="markerLine"/>、行番号 <paramref name="index"/>）から
    /// 1ブロックを読み取り、次に走査すべき行の直前の添字を返す。
    /// </summary>
    private static int ParseBlock(string[] lines, int index, string markerLine, ParseState state)
    {
        var headerLine = index + 1; // 表示用の行番号は1始まり

        // 直前に見つけたパス指定行を使う。見つかっていない場合は、直前のブロックと同じ
        // ファイルへの連続した指定とみなして最後に確定したパスを引き継ぐ。
        // 【根拠】Aiderの作法では毎回パスを書くのが正だが、実際のAIは同一ファイルへ
        // 連続してブロックを出すときにパス行を省くことが多い。省かれた場合に丸ごと
        // 取りこぼすより、直前のファイルの続きとして扱う方が実務上の取り込み率が高い。
        var rawPath = state.PendingPath ?? state.LastPath;
        state.PendingPath = null;

        if (rawPath is null)
        {
            state.Issues.Add(GraftIssue.Of(ErrorCode.E002,
                detail: "SEARCH/REPLACEブロックの直前に、対象ファイルのパスだけを書いた行がありません。" +
                        "標準SR形式ではブロックの直前の行にプロジェクト相対パスを単独で書きます",
                line: headerLine));
            return SkipBlock(lines, index, markerLine, state);
        }

        if (!PatchTextUtil.TryNormalizePath(rawPath, out var path))
        {
            // 絶対パス・".." を含むパスはここで確実に弾く。プロジェクト外への書き込みは
            // 取り返しがつかないため、Graft独自形式（PatchParser.NormalizePathOrThrow）と
            // 同一の検査（PatchTextUtil.TryNormalizePath）を必ず通す。
            state.Issues.Add(GraftIssue.Of(ErrorCode.E201, line: headerLine, path: rawPath));
            return SkipBlock(lines, index, markerLine, state);
        }

        state.LastPath = rawPath;

        return markerLine.StartsWith(SearchMarker, StringComparison.Ordinal)
                || markerLine.StartsWith(SearchRangeMarker, StringComparison.Ordinal)
            ? ParseSearchReplaceBlock(lines, index, markerLine, path, headerLine, state)
            : ParseWholeFileBlock(lines, index, markerLine, path, headerLine, state);
    }

    /// <summary>【1】既存ファイルの部分修正（SEARCH / ======= / REPLACE）。</summary>
    private static int ParseSearchReplaceBlock(
        string[] lines, int index, string markerLine, string path, int headerLine, ParseState state)
    {
        var isRange = markerLine.StartsWith(SearchRangeMarker, StringComparison.Ordinal);
        var attrPrefix = isRange ? SearchRangeMarker : SearchMarker;
        var (occurrence, description) = ParseMarkerAttributes(markerLine[attrPrefix.Length..]);

        var search = Collect(lines, index + 1, t => t == Divider);
        if (!search.Completed)
        {
            state.MarkTruncated();
            return lines.Length;
        }

        var replace = Collect(lines, search.Next, t => t == ReplaceEndMarker);
        if (!replace.Completed)
        {
            state.MarkTruncated();
            return lines.Length;
        }

        var searchText = string.Join('\n', search.Lines);
        var replaceText = string.Join('\n', replace.Lines);

        if (search.Lines.Count == 0 || search.Lines.All(l => l.Length == 0))
        {
            // 【空SEARCH = 新規ファイル作成】素の標準SR形式（Aider）における新規ファイルの
            // 正規の書き方。Graft の FULL形式ブロック（MODE=FULL 相当）は存在しないファイルにも
            // 書けるため、そのまま対応づけられる。会社ルールの NEW_FILE と同じ内部表現になる。
            state.AddFullContent(path, headerLine, replaceText, description);
            return replace.Next - 1;
        }

        state.AddPair(path, headerLine, occurrence, new SearchReplacePair
        {
            SearchText = searchText,
            ReplaceText = replaceText,
            Description = description,
            IsRange = isRange,
            SourceLine = headerLine,
        });
        return replace.Next - 1;
    }

    /// <summary>【2】NEW_FILE ／【3】WHOLE_FILE ／【4】DELETE_FILE。</summary>
    private static int ParseWholeFileBlock(
        string[] lines, int index, string markerLine, string path, int headerLine, ParseState state)
    {
        var body = Collect(lines, index + 1, t => t == EndFileMarker);
        if (!body.Completed)
        {
            state.MarkTruncated();
            return lines.Length;
        }

        if (markerLine.StartsWith(DeleteFileMarker, StringComparison.Ordinal))
        {
            // DELETE_FILE の本文は空であるべきだが、AIが余計な行を挟んでも削除の意図は
            // 明白なため、内容は捨てて DeleteBlock にする（黙って失敗させない）。
            state.AddDelete(path, headerLine);
            return body.Next - 1;
        }

        // NEW_FILE と WHOLE_FILE は、Graft の内部表現ではどちらも FULL形式ブロックになる。
        // Graft の FULL形式は「存在しなければ作る／存在すれば全面的に置き換える」という
        // 一つの動作であり、AI側が新規作成のつもりだったか全面書き換えのつもりだったかで
        // 適用結果が変わることは無いため、区別して持つ必要が無い。
        state.AddFullContent(path, headerLine, string.Join('\n', body.Lines), description: null);
        return body.Next - 1;
    }

    /// <summary>
    /// パス不正などでブロックを取り込めなかった場合に、そのブロックの終了マーカーまで
    /// 読み飛ばす。本文がそのまま次のブロックのパス候補やマーカーとして誤解釈されるのを防ぐ。
    /// </summary>
    private static int SkipBlock(string[] lines, int index, string markerLine, ParseState state)
    {
        var isSearch = markerLine.StartsWith(SearchMarker, StringComparison.Ordinal)
            || markerLine.StartsWith(SearchRangeMarker, StringComparison.Ordinal);

        var body = isSearch
            ? Collect(lines, index + 1, t => t == ReplaceEndMarker)
            : Collect(lines, index + 1, t => t == EndFileMarker);

        if (!body.Completed)
        {
            state.MarkTruncated();
            return lines.Length;
        }
        return body.Next - 1;
    }

    // ------------------------------------------------------------------
    // 行の収集
    // ------------------------------------------------------------------

    /// <summary>本文収集の結果。<paramref name="Next"/> は終了マーカーの次の行の添字。</summary>
    private readonly record struct Collected(bool Completed, IReadOnlyList<string> Lines, int Next);

    /// <summary>
    /// <paramref name="start"/> から終了マーカーまでの行を集める。終了マーカーが見つからないまま
    /// 入力が尽きた場合は Completed=false（切断・4.10相当）とする。
    /// 本文中の行は加工しない（``` も含めてそのまま残す。ファイルの中身そのものであるため）。
    /// </summary>
    private static Collected Collect(string[] lines, int start, Func<string, bool> isTerminator)
    {
        var body = new List<string>();
        for (var i = start; i < lines.Length; i++)
        {
            if (isTerminator(lines[i].TrimEnd())) return new Collected(true, body, i + 1);
            body.Add(lines[i]);
        }
        return new Collected(false, body, lines.Length);
    }

    // ------------------------------------------------------------------
    // 行の判定
    // ------------------------------------------------------------------

    private static bool IsBlockOpenMarker(string trimmed)
        => trimmed.StartsWith(SearchMarker, StringComparison.Ordinal)
        || trimmed.StartsWith(SearchRangeMarker, StringComparison.Ordinal)
        || trimmed.StartsWith(NewFileMarker, StringComparison.Ordinal)
        || trimmed.StartsWith(WholeFileMarker, StringComparison.Ordinal)
        || trimmed.StartsWith(DeleteFileMarker, StringComparison.Ordinal);

    /// <summary>
    /// 行が「パスだけの行」かどうかを判定し、装飾を取り除いたパスを返す。
    ///
    /// 【判定を厳しめにしている理由】標準SR形式ではパスが裸の1行として現れるため、
    /// AIの説明文（地の文）を誤ってパスとして拾う危険が Graft独自形式（<c>&lt;&lt;&lt;&lt; FILE:</c> という
    /// 明示ヘッダがある）より格段に高い。そこで
    ///   (1) 空白を1つも含まないこと（英文の地の文を弾く）
    ///   (2) "." または "/" を含むこと（日本語の地の文「以下のとおり修正します。」等を弾く。
    ///       全角の「。」は "." ではないため条件を満たさない）
    ///   (3) パスに使えない文字を含まないこと
    /// の3つを満たす場合に限りパス指定行とみなす。この (2) の基準は、既に
    /// <see cref="PatchTextDetector"/> がGraft形式のパス欄に対して使っている
    /// 「実在するファイルパスらしい見た目（. または / を持つ）」と意図的に揃えてある。
    ///
    /// 【承知の上の取りこぼし】拡張子も区切りも持たない実在のファイル名（Dockerfile・Makefile 等）は
    /// パス指定行と認められない。地の文との区別が原理的に付かないためで、
    /// <see cref="PatchTextDetector"/> が同じトレードオフを既に採っている。この場合は
    /// Graft独自形式（<c>&lt;&lt;&lt;&lt; FILE: Dockerfile</c>）で出力すれば従来どおり扱える。
    ///
    /// 【装飾の除去】Aiderは「太字にするな・引用符で囲むな」と明示的に指示しているが、実際の
    /// AIは <c>**path**</c> や <c>`path`</c>、末尾に <c>:</c> を付けた形をよく出す。パスの安全性検査
    /// （<see cref="PatchTextUtil.TryNormalizePath"/>）は装飾を外した後に必ず通すため、
    /// ここで寛容にしても危険は増えない。
    /// </summary>
    private static bool TryParsePathLine(string line, out string path)
    {
        path = string.Empty;

        var candidate = line.Trim();
        if (candidate.Length == 0) return false;

        // 末尾のコロン（"src/app.py:" のような書き方）を先に落とす。
        candidate = candidate.TrimEnd(':', '：');
        // 太字（**）・インラインコード（`）・引用符の装飾を外す。
        candidate = candidate.Trim('*', '`', '"', '\'');
        candidate = candidate.Trim();
        if (candidate.Length == 0) return false;

        if (candidate.Any(char.IsWhiteSpace)) return false;
        if (!candidate.Contains('.') && !candidate.Contains('/') && !candidate.Contains('\\')) return false;
        if (!candidate.All(IsPathChar)) return false;

        path = candidate;
        return true;
    }

    /// <summary>
    /// パス指定行に使える文字かどうか。禁止文字を並べる「拒否リスト」ではなく
    /// **許可リスト**にしてある。
    ///
    /// 【なぜ許可リストか】日本語の地の文は空白を含まないため「空白が無い」だけでは弾けず、
    /// 実際に <c>【原則】既存ファイルの修正はSEARCH/REPLACE形式を使い、</c> のような1行が
    /// "/" を含むためにパス指定行として誤認される事故が起きた（既定プロンプトテンプレートの
    /// 本文をクリップボード監視が誤検知する）。全角の約物（【】、。「」（）等）は無数にあり
    /// 拒否リストでは漏れる一方、実在のファイル名に使われる記号はごく限られるため、
    /// 「英数字・各国語の文字」＋「パスで実際に使う記号だけ」に絞る方が確実で説明もしやすい。
    /// <see cref="char.IsLetterOrDigit(char)"/> は日本語の文字にも true を返すため、
    /// <c>docs/取扱説明書.md</c> のような日本語ファイル名は従来どおり扱える。
    /// </summary>
    private static bool IsPathChar(char c)
        => char.IsLetterOrDigit(c)
        || c is '.' or '/' or '\\' or '_' or '-' or '+' or '~' or '@' or '#'
        // ':' を許すのは、Windowsのドライブレター付き絶対パス（C:/... ）を「パス指定行として
        // 認識したうえで安全性検査（TryNormalizePath）に落とし、E201として明確に拒否する」ため。
        // 許さないとパス指定行として認識されず、危険なパスがE002（ヘッダの構文エラー）という
        // 実態と食い違う理由で弾かれ、何が起きたのか利用者に伝わらない。
        // 行末のコロン（"src/app.py:"）は判定前に落としているため、ここでは影響しない。
        || c is ':';

    /// <summary>
    /// 開始マーカー行の残り部分から、Graft拡張の属性（<c>OCCURRENCE=</c>）と
    /// <c>#</c> 以降の説明文を取り出す。標準SR形式の素の書き方ではどちらも現れないが、
    /// 「基本は標準SR形式、必要なときだけGraft拡張」という今回の方針上、標準SR形式の
    /// ブロックでもこれらを使えるようにしておく（4章の SEARCH マーカーと同じ書き方）。
    /// </summary>
    private static (OccurrenceSpec? Occurrence, string? Description) ParseMarkerAttributes(string afterMarker)
    {
        var hashIdx = afterMarker.IndexOf('#');
        var mainPart = hashIdx >= 0 ? afterMarker[..hashIdx] : afterMarker;
        var description = hashIdx >= 0 ? afterMarker[(hashIdx + 1)..].Trim() : null;
        if (string.IsNullOrEmpty(description)) description = null;

        OccurrenceSpec? occurrence = null;
        foreach (var token in mainPart.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("OCCURRENCE=", StringComparison.Ordinal))
                occurrence = PatchTextUtil.ParseOccurrence(token["OCCURRENCE=".Length..]);
        }
        return (occurrence, description);
    }

    // ------------------------------------------------------------------
    // 組み立て
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析途中の状態。同一ファイルに対する複数のSEARCH/REPLACEブロックは、
    /// <see cref="UnifiedDiffAdapter"/> と同じく1つの <see cref="SearchReplaceBlock"/> へ
    /// ペアとしてまとめる（同一パスのブロックが複数並ぶとキュー側で重複扱い（E007）に
    /// なってしまうため）。
    /// </summary>
    private sealed class ParseState
    {
        private readonly List<PatchBlock> _blocks = new();
        private readonly Dictionary<string, List<SearchReplacePair>> _pairsByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _blockIndexByPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OccurrenceSpec> _occurrenceByPath = new(StringComparer.Ordinal);
        private bool _truncated;

        /// <summary>直前に見つけたパス指定行（このブロックで消費する）。</summary>
        public string? PendingPath { get; set; }

        /// <summary>最後に確定したパス。パス行が省略された連続ブロックで引き継ぐ。</summary>
        public string? LastPath { get; set; }

        /// <summary>解析中に見つかった問題。</summary>
        public List<GraftIssue> Issues { get; } = new();

        public void MarkTruncated() => _truncated = true;

        public void AddPair(string path, int headerLine, OccurrenceSpec? occurrence, SearchReplacePair pair)
        {
            if (!_pairsByPath.TryGetValue(path, out var pairs))
            {
                pairs = new List<SearchReplacePair>();
                _pairsByPath[path] = pairs;
                _blockIndexByPath[path] = _blocks.Count;
                // Pairs・Occurrence は後から差し替えるため、いったん現時点の内容で入れておく。
                _blocks.Add(new SearchReplaceBlock { Path = path, HeaderLine = headerLine, Pairs = pairs });
            }

            pairs.Add(pair);

            // OCCURRENCE はペアではなくブロックに載る（4章のSEARCHマーカーと同じ扱い。
            // SearchReplacePair は Occurrence を持たない）。同一ファイルの複数ブロックを
            // 1つへまとめる都合上、最後に指定されたものが有効になる。
            if (occurrence is not null) _occurrenceByPath[path] = occurrence;
        }

        public void AddFullContent(string path, int headerLine, string content, string? description)
            => _blocks.Add(new FullContentBlock
            {
                Path = path,
                HeaderLine = headerLine,
                Content = content,
                Description = description,
            });

        public void AddDelete(string path, int headerLine)
            => _blocks.Add(new DeleteBlock { Path = path, HeaderLine = headerLine });

        public GraftResult<Patch> Build(string patchText)
        {
            // まとめて溜めたペアを最終的な件数で確定させる。
            foreach (var (path, index) in _blockIndexByPath)
            {
                var block = (SearchReplaceBlock)_blocks[index];
                _blocks[index] = block with
                {
                    Pairs = _pairsByPath[path].ToArray(),
                    Occurrence = _occurrenceByPath.TryGetValue(path, out var occ) ? occ : OccurrenceSpec.Single,
                };
            }

            var issues = new List<GraftIssue>(Issues);
            var tailLines = Array.Empty<string>() as IReadOnlyList<string>;
            if (_truncated)
            {
                // 4.10 の切断検出と同じ扱い。継続依頼プロンプトの導線へ載せる。
                tailLines = PatchTextUtil.GetTailLines(patchText, 3);
                issues.Add(GraftIssue.Of(ErrorCode.E005, severity: Severity.Warning));
            }

            if (_blocks.Count == 0 && !_truncated)
            {
                // 何も取り込めなかった場合は失敗。原因が分かっている（パス不正等）ならそれを、
                // 何も分からなければ E001 を返す。UnifiedDiffAdapter と同じ考え方。
                var failIssues = issues.Count > 0
                    ? issues
                    : new List<GraftIssue> { GraftIssue.Of(ErrorCode.E001, line: 1) };
                return GraftResult<Patch>.Fail(failIssues);
            }

            var patch = new Patch
            {
                Meta = new PatchMeta { Summary = ImportSummary, Type = ImportType },
                Blocks = _blocks,
                RawText = patchText,
                IsTruncated = _truncated,
                TailLines = tailLines,
            };
            return GraftResult<Patch>.Ok(patch, issues);
        }
    }
}
