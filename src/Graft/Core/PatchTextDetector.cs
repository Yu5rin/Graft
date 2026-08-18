using System.IO;

namespace Graft.Core;

/// <summary>
/// クリップボードの内容が「パッチらしいテキスト」かどうかを判定する（仕様書9章・10章）。
///
/// クリップボードが変わるたびに走る判定なので、まず安価な前判定（行頭のマーカー／
/// unified diff ヘッダの手がかり探し）で大半の無関係なテキストを弾き、手がかりが
/// 見つかった場合に限り、実際のパーサ（<see cref="PatchParser"/>）で構造として
/// 成立しているか（SEARCH に対応する REPLACE があるか等）まで確認する。判定用の
/// 別実装を作らず、本物の解析器をそのまま「成立するかどうか」の判定に転用する。
///
/// 解説文書（取扱説明書・README等）がパッチの書き方をコードブロック（```）で
/// 例示しているだけのケースを誤検知しないよう、閉じているコードブロックの中身は
/// 第一段階の判定対象から除外する。ただし閉じずに入力が終わっている場合（AIの出力が
/// コードフェンスの途中で途切れた場合）は、切断パッチの検知を妨げないよう
/// 除外せず通常どおり判定対象に含める。
///
/// 【段階2: 単一フェンスで丸ごと囲まれたパッチの救済（案件3対応）】
/// 案件3でプロンプトテンプレートへ「パッチ全体を1つの```で囲んで出力する」指示を
/// 追加した結果、本物のパッチが単一のコードフェンスで丸ごと囲まれるケースが従来の
/// レアケースから既定シナリオへ変わった。第一段階（コードブロックの外だけを見る）
/// だけでは常にこれを見逃してしまうため、第一段階で何も見つからなかった場合に限り、
/// 「テキスト全体を通して閉じたコードフェンスがちょうど1個だけ」というケースに絞って
/// フェンスの中身も含めて再判定する（<see cref="LooksLikePatch"/>参照）。
///
/// フェンスの個数で絞る理由: 解説文書（取扱説明書・READMEなど）は、無関係な地の文の
/// 中に複数のコードブロック例示が独立して散らばっているのが通例で、そのうち1つが
/// たまたま構造的に成立していても、それは「実例の1つ」であって「クリップボードの
/// 中身がその1個のパッチそのもの」ではない。これに対し、AIが指示どおりパッチ全体を
/// 1つの```で囲んで出力した場合、クリップボードの中身は基本的に「前後にわずかな
/// 説明文＋パッチ全体を囲む1個のフェンス」という形（<c>markdown_fence</c>フィクスチャの
/// 実例と同じ形）になる。閉じたフェンスの個数がちょうど1個かどうかは、この2つの状況を
/// 実務上よく分離できる、単純で説明可能な条件である。
/// フェンスが0個の文章はそもそも第一段階で検知済みのはずで第二段階に来ない。
/// 2個以上ある場合（解説文書に複数の例示がある、AIが指示に反してファイルごとに
/// 別々のフェンスで出力した等）は救済しない＝自動検知の対象にしない。ファイルごとに
/// 別々のフェンスで出力された本物のパッチを見逃す（検知漏れになる）ことは許容する
/// トレードオフとした。貼り付け→解析（<see cref="PatchScanner"/>経由）自体は
/// フェンスの個数に関係なく従来どおり成功するため、実害は自動検知の通知が出ない
/// ことだけに留まる。
///
/// 【パスの実在らしさ（案件3対応）】 段階1・段階2のどちらでも、Graft形式の
/// パス付きヘッダ（<<<< FILE: 等）が1つ以上見つかった場合は、そのうち少なくとも1つの
/// パス欄が「拡張子（.を含む）または区切り（/を含む）を持つ、実在するファイルパスら
/// しい見た目」であることを追加で要求する。これはGraftの既定プロンプトテンプレート
/// （<c>PromptTemplateStore</c>）自身が仮のパスとして文字通り「相対パス」という
/// プレースホルダを使っているため、利用者がテンプレートをコピーしただけで
/// （AIに何も聞く前から）誤検知していた既存の不具合を修正するために追加した
/// （案件3対応前から存在した不具合。<c>PatchTextDetectorTests</c>の
/// 「既定プロンプトテンプレート」関連のテスト参照）。<<<< PATCH や <<<<<<< SEARCH
/// のようにパスを伴わないマーカーだけの場合はこの条件を課さない（判定材料が無いため）。
/// 拡張子も区切りも持たない実在のファイル名（例: Dockerfile・Makefile）を唯一の
/// 対象とするパッチは検知漏れになりうるが、稀なケースであり、貼り付け→解析には
/// 影響しないため許容する。
///
/// 内容はどこにも保持しない。OSごとのクリップボード監視実装
/// （<c>Platform/Windows</c>・<c>Platform/Linux</c>）が共通して使えるよう、
/// UI・OSのいずれにも依存しないCore層に置く。
/// </summary>
public static class PatchTextDetector
{
    private static readonly string[] HeaderPrefixes =
    {
        "<<<< FILE:",
        "<<<< PATCH",
        "<<<< DELETE:",
        "<<<< RENAME:",
        "<<<< MKDIR:",
        "<<<< APPEND:",
        "<<<< PREPEND:",
        "<<<<<<< SEARCH",
    };

    /// <summary>パス欄を伴うヘッダの接頭辞（パスの実在らしさ判定の対象）。</summary>
    private static readonly string[] PathBearingHeaderPrefixes =
    {
        "<<<< FILE:",
        "<<<< DELETE:",
        "<<<< RENAME:",
        "<<<< MKDIR:",
        "<<<< APPEND:",
        "<<<< PREPEND:",
    };

    /// <summary>
    /// テキストがパッチとして構造的に成立しているかどうかを判定する。
    ///
    /// 段階1（従来どおり）: 「マーカーがコードブロックの外にあり、かつ対応関係
    /// （SEARCH〜REPLACE、FILEヘッダの後のパス、等）が成立している」場合に検知する。
    /// 段階2（案件3で追加）: 段階1で見つからなかった場合に限り、「閉じたコードフェンスが
    /// ちょうど1個だけ」のテキストについて、フェンスの中身も含めて同じ基準で再判定する
    /// （単一フェンスで丸ごと囲まれた本物のパッチを救済する。クラスコメント参照）。
    /// どちらの段階でも、パスを伴うヘッダがあるなら少なくとも1つは実在するパスらしい
    /// 見た目であることを追加で要求する（クラスコメント参照）。
    /// </summary>
    public static bool LooksLikePatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var visible = StripClosedFencedBlocks(text);
        if (!string.IsNullOrEmpty(visible) && LooksStructurallyValid(visible))
            return true;

        // 段階2: 段階1で何も見つからなかった場合のみ、「閉じたフェンスがちょうど1個」の
        // ケースに絞ってフェンスの中身も含めて再判定する。
        if (CountClosedFencedBlocks(text) == 1 && LooksStructurallyValid(text))
            return true;

        return false;
    }

    /// <summary>
    /// 与えられたテキスト（既にフェンスの扱いを終えたもの）が、パッチとして
    /// 構造的に成立しているかを判定する共通ロジック。安価な前判定→実解析→
    /// パスの実在らしさ、の順に確認する。
    /// </summary>
    private static bool LooksStructurallyValid(string text)
    {
        // 安価な前判定: マーカーもunified diffの手がかりも無ければ、
        // 以降の実解析（PatchParser.Parse）は呼ばずに打ち切る。
        if (!HasGraftMarker(text) && !UnifiedDiffAdapter.IsUnifiedDiff(text))
            return false;

        // パスを伴うヘッダが1つ以上あるなら、少なくとも1つは実在するパスらしい
        // 見た目であることを要求する（クラスコメント「パスの実在らしさ」参照）。
        // unified diff側のヘッダ（--- a/...・+++ b/...）はこの対象に含めない
        // （Graft形式の"相対パス"プレースホルダ対策のためのチェックであり、
        // unified diffは常にファイルパスを伴う形式のため対象にする必要が無い）。
        if (HasAnyPathBearingHeader(text) && !HasPlausiblePathBearingHeader(text))
            return false;

        // 手がかりが見つかった場合のみ、実際のパーサで対応関係まで成立しているかを
        // 確認する（成立していれば切断パッチも検知に含む）。
        //
        // ここでの合否は「何も認識できなかったか（E001）」だけで判定する。パスが不正
        // （E201）・SEARCHが空（E003）・エスケープ崩れ（E006）などパーサが特定のブロックを
        // 認識した上で内容の誤りとして弾いたケースまで非検知にしてしまうと、AIの出力に
        // 小さな不備が1つあっただけの本物のパッチを丸ごと見逃すことになる。そうした内容の
        // 誤り自体は、検知後に実際に「解析」した際に接ぎ木パネル側で利用者へ提示される。
        var result = new PatchParser().Parse(text);
        return result.IsSuccess || !result.HasIssue(ErrorCode.E001);
    }

    /// <summary>
    /// 閉じているMarkdownコードフェンス（```〜```の対）の中身をテキストから取り除く。
    /// フェンスが閉じずに入力が尽きた場合（切断されたAI出力）は、その区間は
    /// 除外せずそのまま残す。フェンス行自体（```で始まる行）はどちらの場合も出力に含めない。
    /// </summary>
    private static string StripClosedFencedBlocks(string text)
    {
        var lines = PatchTextUtil.SplitRawLines(text);
        var visible = new List<string>(lines.Length);
        List<string>? pending = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                pending = pending is null
                    ? new List<string>() // フェンス開始。閉じるまで保留する。
                    : null;               // 対になる終端フェンスが見つかった → 保留分ごと除外して確定。
                continue;
            }

            (pending ?? visible).Add(line);
        }

        // 閉じずに入力が尽きた場合は、保留していた中身をそのまま可視部分へ戻す。
        if (pending is not null) visible.AddRange(pending);

        return string.Join('\n', visible);
    }

    /// <summary>
    /// テキスト全体を通して「閉じている」コードフェンスの個数を数える（開いたまま
    /// 入力が尽きたフェンスは数えない）。段階2（単一フェンス救済）の絞り込みに使う。
    /// </summary>
    private static int CountClosedFencedBlocks(string text)
    {
        var lines = PatchTextUtil.SplitRawLines(text);
        var count = 0;
        var isOpen = false;

        foreach (var line in lines)
        {
            if (!line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (isOpen) count++; // 対になる終端フェンス。
            isOpen = !isOpen;
        }

        return count;
    }

    /// <summary>
    /// テキストがGraft形式（<c>&lt;&lt;&lt;&lt; ...</c>）のブロックヘッダを行頭に含むかどうかを判定する。
    /// <see cref="PatchParser"/> が unified diff アダプタへ委譲すべきか判断する際にも使う
    /// （Graft形式のマーカーが1つも無い場合に限りアダプタへ回す）。
    /// </summary>
    public static bool HasGraftMarker(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            foreach (var prefix in HeaderPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    /// <summary>行頭にパス欄を伴うヘッダ（<<<< FILE: 等）が1つでもあるかどうか。</summary>
    private static bool HasAnyPathBearingHeader(string text)
        => EnumeratePathBearingHeaderRemainders(text).Any();

    /// <summary>
    /// パス欄を伴うヘッダのうち、少なくとも1つのパス欄が拡張子（.）または
    /// 区切り（/）を持つ「実在するファイルパスらしい見た目」かどうか。
    /// </summary>
    private static bool HasPlausiblePathBearingHeader(string text)
        => EnumeratePathBearingHeaderRemainders(text).Any(remainder =>
            remainder.Contains('.') || remainder.Contains('/'));

    /// <summary>
    /// パス欄を伴うヘッダ行から、接頭辞を除いた残り（パス欄＋MODE等の追加トークン）を
    /// 順に取り出す。正確なパス抽出（<see cref="PatchParser"/>相当の厳密さ）は目的では
    /// なく、あくまで「実在するパスらしい見た目かどうか」の軽い手がかり探しのため、
    /// 接頭辞より後ろの行全体を対象にする（MODE=FULLやOCCURRENCE指定・#以降の説明が
    /// 混ざっていても、通常はそれ自体に.や/が含まれないため実用上問題ない）。
    /// </summary>
    private static IEnumerable<string> EnumeratePathBearingHeaderRemainders(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            foreach (var prefix in PathBearingHeaderPrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    yield return line[prefix.Length..];
                    break;
                }
            }
        }
    }
}
