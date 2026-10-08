using System.Text;

namespace Graft.Core;

/// <summary>
/// 二重適用検知（仕様書6.2）に使う「パッチの同一性」を求める。通常のパッチは本文（<see cref="Patch.RawText"/>）の
/// ハッシュで判定するが、一部だけ適用できた後に利用者が残りのSEARCHを直して適用し直す「残りのパッチ」は、
/// 本文が元のAI出力のままでも中身は別物なので、別の同一性を与える。
/// </summary>
public static class PatchIdentity
{
    /// <summary>
    /// 残りのパッチ用の二重適用判定の基準テキストを作る（<see cref="Patch.PatchHashSource"/> に入れる）。
    ///
    /// 【なぜ必要か】一部適用の後に残った失敗ブロックのSEARCHを直して適用し直すとき、そのパッチの
    /// <c>RawText</c> は元のAI出力のままである（貼り付けた元のテキストは書き換えない方針）。
    /// そのままハッシュすると、直前に記録したリビジョンと<b>同じパッチ</b>と判定されてE302になり、
    /// 「適用」が押せなくなる。実際は「rNで適用に失敗したブロックだけを、直して出し直したもの」で、
    /// rNと同じパッチではない。
    ///
    /// 【なぜ「残りの中身」を混ぜるか】元の本文に目印を足すだけだと、残りの中身が違っても同じ
    /// ハッシュになり、別の修正を適用した後に「適用済み」と誤判定しうる。残りのブロック（パス・SEARCH・REPLACE）を
    /// 決まった順に連結して混ぜるので、同じ修正を同じ元の出力から二度適用しようとしたときだけ
    /// E302になり、修正が違えば別のパッチとして扱われる。
    ///
    /// 【元の出力を貼り直したときのE302は変わらない】元の出力のハッシュは<c>RawText</c>のままなので、
    /// rNと一致して従来どおりE302になる（残りのパッチのハッシュとは一致しない）。
    /// </summary>
    public static string ForRemainder(Patch source, IReadOnlyList<PatchBlock> remainingBlocks)
    {
        var sb = new StringBuilder(source.RawText);
        // 本文と区別がつくよう、通常の本文には現れない制御文字の区切りを使う。
        sb.Append("\n\u0001graft-remainder\u0001\n");
        foreach (var block in remainingBlocks)
        {
            sb.Append((int)block.Kind).Append('\u0002').Append(block.Path).Append('\u0002');
            switch (block)
            {
                case SearchReplaceBlock sr:
                    foreach (var pair in sr.Pairs)
                    {
                        sb.Append(pair.SearchText).Append('\u0003').Append(pair.ReplaceText).Append('\u0004');
                    }
                    break;
                case FullContentBlock full: sb.Append(full.Content); break;
                case AppendBlock append: sb.Append(append.Content); break;
                case PrependBlock prepend: sb.Append(prepend.Content); break;
                case RenameBlock rename: sb.Append(rename.ToPath); break;
            }
            sb.Append('\u0005');
        }
        return sb.ToString();
    }
}
