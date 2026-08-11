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
/// 判定の対象から除外する。ただし閉じずに入力が終わっている場合（AIの出力が
/// コードフェンスの途中で途切れた場合）は、切断パッチの検知を妨げないよう
/// 除外せず通常どおり判定対象に含める。
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

    /// <summary>
    /// テキストがパッチとして構造的に成立しているかどうかを判定する。
    /// 「マーカーがコードブロックの外にあり、かつ対応関係（SEARCH〜REPLACE、
    /// FILEヘッダの後のパス、等）が成立している」場合にのみ検知する。
    /// コードブロックの中だけにマーカーがある場合（解説文書の例示など）は検知しない。
    /// </summary>
    public static bool LooksLikePatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        var visible = StripClosedFencedBlocks(text);
        if (string.IsNullOrEmpty(visible)) return false;

        // 安価な前判定: マーカーもunified diffの手がかりも無ければ、
        // 以降の実解析（PatchParser.Parse）は呼ばずに打ち切る。
        if (!HasGraftMarker(visible) && !UnifiedDiffAdapter.IsUnifiedDiff(visible))
            return false;

        // 手がかりがコードブロックの外に見つかった場合のみ、実際のパーサで
        // 対応関係まで成立しているかを確認する（成立していれば切断パッチも検知に含む）。
        //
        // ここでの合否は「何も認識できなかったか（E001）」だけで判定する。パスが不正
        // （E201）・SEARCHが空（E003）・エスケープ崩れ（E006）などパーサが特定のブロックを
        // 認識した上で内容の誤りとして弾いたケースまで非検知にしてしまうと、AIの出力に
        // 小さな不備が1つあっただけの本物のパッチを丸ごと見逃すことになる。そうした内容の
        // 誤り自体は、検知後に実際に「解析」した際に接ぎ木パネル側で利用者へ提示される。
        var result = new PatchParser().Parse(visible);
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
}
