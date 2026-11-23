using System.IO;
using Graft.Core.Vba;

namespace Graft.Features;

/// <summary>
/// リボンの書き換え（仕様書_ブックの情報と依頼の観点 16.5）が使う、ファイルの操作の差し替え口。本番は既定の実装のまま使い、
/// テストは失敗の注入（控えを書けない・ディスクがいっぱい・置き換えの失敗・途中で落ちる）に差し替える。
/// <para>
/// 【なぜ差し替え口を設けるか】 段階 F3 の完了条件は「失敗の注入を含めて、ブックのファイルが元のまま（または控えと同じ）であること」。
/// 本物のディスクを本当にいっぱいにしたり、<c>File.Replace</c> を本当に失敗させたりするのは CI では作れない。判断と後始末の流れは
/// 本番と同じコードを通し、外界とのやり取りの 1 点だけを差し替える。
/// </para>
/// </summary>
internal sealed class VbaRibbonRewriteIo
{
    /// <summary>
    /// フォルダのある保存先の空き容量（バイト）。取れない保存先（UNC など）は null（そのときは事前の検査を省く。16.5 の予行）。
    /// </summary>
    public Func<string, long?> FreeSpace { get; set; } = DefaultFreeSpace;

    /// <summary>
    /// ブックを排他で開く（16.5 の段階 7。<see cref="FileShare.None"/>。ほかの誰も使っていない証拠）。開いたストリームで控えも写す。
    /// </summary>
    public Func<string, FileStream> OpenExclusive { get; set; } =
        path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

    /// <summary>控えのファイルを新しく作る（同名があれば失敗させ、既存の控えを上書きしない）。</summary>
    public Func<string, FileStream> CreateNew { get; set; } =
        path => new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

    /// <summary>一時ファイルでブックを置き換える（<see cref="VbaRibbonPackageWriter.ReplaceFile"/>）。控えから戻すときも使う。</summary>
    public Action<string, string> Replace { get; set; } = VbaRibbonPackageWriter.ReplaceFile;

    /// <summary>
    /// 排他で開けるまで待つ上限（16.5 の段階 7。Excel で開いていなかったブック）。既定値は <see cref="VbaRibbonRewriteTimings"/>
    /// （実機検証 12 で見直す値の置き場）。
    /// </summary>
    public TimeSpan ExclusiveTimeout { get; set; } = VbaRibbonRewriteTimings.ExclusiveTimeout;

    /// <summary>Graft が Excel でブックを閉じた直後の、排他で開けるまで待つ上限（16.6。<b>実機で要確認</b>）。</summary>
    public TimeSpan ExclusiveTimeoutAfterExcelClose { get; set; } = VbaRibbonRewriteTimings.ExclusiveTimeoutAfterExcelClose;

    /// <summary>排他で開き直す間隔。</summary>
    public TimeSpan ExclusiveInterval { get; set; } = VbaRibbonRewriteTimings.ExclusiveInterval;

    private static long? DefaultFreeSpace(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            // UNC（\\server\share）は DriveInfo で取れないことがある。取れないものは「分からない」として検査を省く。
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
