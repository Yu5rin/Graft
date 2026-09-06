namespace Graft.Features;

/// <summary>
/// 仕様書10.4のトークン概算。日本語込みの経験係数として「文字数 / ratio」で近似する。
/// 実際のトークナイザは使わず、コード本文であっても言語混在であっても一定の粗い目安を出す
/// ことを目的とする（トークン節約効果を体感できれば十分という仕様意図に基づく）。
/// </summary>
public static class TokenEstimator
{
    /// <summary>既定の比率（設定 <c>context.tokenRatio</c> の既定値と一致させる）。</summary>
    public const double DefaultRatio = 2.5;

    /// <summary>
    /// テキストの推定トークン数を返す。ratio が0以下など不正な場合は既定値へフォールバックする。
    /// </summary>
    public static int Estimate(string text, double ratio = DefaultRatio)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return EstimateLength(text.Length, ratio);
    }

    /// <summary>
    /// 文字数（の近似）から概算トークン数を計算する。コンテキスト収集画面で、ファイルを
    /// 実際に読まずに<see cref="Graft.Features.ContextFileNode.SizeBytes"/>から素早く概算を
    /// 出したい場合（3状態を切り替えるたびに全ファイルを読み直すと大規模プロジェクトで
    /// 重くなるため）に使う。バイト数をそのまま文字数の近似として渡しても、ASCII主体の
    /// コードであれば十分近い値になる。
    /// </summary>
    public static int EstimateLength(long length, double ratio = DefaultRatio)
    {
        if (length <= 0)
        {
            return 0;
        }

        var effectiveRatio = ratio > 0 ? ratio : DefaultRatio;

        // 異常系点検「中」3件目の対応: length が非常に大きい（例: 数GB相当のコンテキスト選択）・
        // effectiveRatio が極端に小さい（設定 context.tokenRatio に小さい値を入れられる不具合2と
        // 組み合わさると容易に起こる）場合、length / effectiveRatio は int.MaxValue
        // （約21億）を軽く超える。以前は Math.Ceiling(...) の結果（double）をそのまま
        // (int) へキャストしており、C# の既定動作（unchecked）では例外にならず静かに
        // 負数へラップしていた（実測: EstimateLength(long.MaxValue) や
        // EstimateLength(6_000_000_000) が -2147483648 を返すことを確認済み）。
        // この負数が ContextCollectViewModel.ExceedsWarnThreshold の比較
        // （推定トークン数 > 閾値）へそのまま渡ると常に false になり、10章の安全機構
        // （上限超過の警告）が無言で無効化されてしまう。
        //
        // 呼び出し元（ContextResult.EstimatedTokens・RevisionStats.EstimatedTokens 等）が
        // いずれも int 型で、それらすべてを long へ広げるのは影響範囲が大きい（複数の
        // record・ViewModelプロパティ・表示の桁区切り書式に波及する）ため、ここでは
        // 戻り値の型は int のまま、doubleの時点で int.MaxValue と比較してからキャストする
        // （キャスト後の値で比較すると、その時点で既にオーバーフローして意味を失っている
        // ため手遅れ）。表示用の概算値という性質上、桁あふれで無意味な値（まして負数）を
        // 返すより、int.MaxValueへ丸めて「非常に多い」ことを正しく伝える方を優先する。
        var estimated = Math.Ceiling(length / effectiveRatio);
        return estimated >= int.MaxValue ? int.MaxValue : (int)estimated;
    }
}
