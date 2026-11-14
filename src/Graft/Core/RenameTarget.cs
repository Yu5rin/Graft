using System.IO;

namespace Graft.Core;

/// <summary>
/// RENAME の移動先に「既に何かがある」かどうかの判定。ドライラン（<see cref="DryRunPlanner"/>）と
/// 本適用（<see cref="ApplyEngine"/>）の両方が同じ基準で判断するために1か所へまとめる。
///
/// 【なぜ必要か】 以前の RENAME は移動先の存在を見ず、実行側も <c>File.Move(..., overwrite: true)</c>
/// だった。<c>a.txt</c> と <c>b.txt</c> がある状態で <c>RENAME: a.txt -&gt; b.txt</c> を適用すると、
/// 警告なしで <c>b.txt</c> の元の内容が消える。バックアップは移動元（<c>a.txt</c>）しか取らないので、
/// 取り消しても <c>a.txt</c> が戻り、<c>b.txt</c> は削除されるだけで、元の内容は二度と戻らなかった。
///
/// 【大文字小文字だけが違う改名を許す理由と判定方法】 <c>a.txt -&gt; A.txt</c> は、Windows や macOS の
/// 既定のファイルシステム（大文字小文字を区別しない）では同じファイルの名前の付け替えで、
/// 何も失われない。ところがパスだけ見ると「移動先が存在する」ので、単純な存在確認では拒否してしまう。
/// 一方 Linux のように区別するファイルシステムでは <c>a.txt</c> と <c>A.txt</c> は別のファイルで、
/// 両方あれば上書きになる。OSで分岐すると、Linux上のWindows共有や大文字小文字を区別する
/// ボリュームを取りこぼすため、実際のフォルダの一覧に「移動元の名前」と「移動先の名前」が
/// <b>別々の項目として</b>並んでいるかどうかで判定する（区別しない環境では実体が1つなので、
/// 一覧にはどちらか一方の綴りしか出ない）。
/// </summary>
internal static class RenameTarget
{
    /// <summary>
    /// 移動先に、移動元とは別の実体（ファイルまたはフォルダ）が既にあれば true。
    /// 移動元と同じパス、または大文字小文字だけが違うだけで同じ実体を指す場合は false。
    /// </summary>
    public static bool IsOccupied(string fromFullPath, string toFullPath)
    {
        var toIo = LongPath.Extended(toFullPath);
        if (!File.Exists(toIo) && !Directory.Exists(toIo)) return false;

        if (string.Equals(fromFullPath, toFullPath, StringComparison.Ordinal)) return false;
        if (!string.Equals(fromFullPath, toFullPath, StringComparison.OrdinalIgnoreCase)) return true;

        // 綴りだけが違う。フォルダ一覧に両方の綴りが別項目として載っていれば別々のファイル。
        var dir = Path.GetDirectoryName(toFullPath);
        if (string.IsNullOrEmpty(dir)) return true;
        try
        {
            var names = Directory.EnumerateFileSystemEntries(LongPath.Extended(dir))
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.Ordinal);
            return names.Contains(Path.GetFileName(fromFullPath)) && names.Contains(Path.GetFileName(toFullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一覧を取れないときは、データを失わない側（拒否）へ倒す。
            return true;
        }
    }
}
