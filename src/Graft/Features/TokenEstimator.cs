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
        return (int)Math.Ceiling(length / effectiveRatio);
    }
}
