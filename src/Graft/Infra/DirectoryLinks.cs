using System.IO;

namespace Graft.Infra;

/// <summary>
/// フォルダのシンボリックリンク・ジャンクションを「再帰でたどらない」ための共通の補助。
///
/// 【なぜ必要か】 プロジェクト内を再帰で列挙する箇所（コンテキスト収集・ファイル横断検索・
/// クイックオープン・エクスプローラの絞り込み・.gitignore の探索）は、フォルダのリンクも
/// 普通のフォルダとして中へ入っていた。リンクが親フォルダを指して輪になっていると、同じファイルを
/// 何度も数え（輪の深さはOSのリンク追跡の上限、Linuxで40段まで続く）、検索結果は重複し、
/// 一覧は膨れ、最悪は終わらない。リンクの先が別のフォルダ（プロジェクト外）の場合も、
/// 外のファイルを「プロジェクトの一部」として読んでしまう。
/// そこで、リンクそのものは一覧に出してよいが、その中へは入らない、で統一する。
///
/// 判定には <see cref="FileAttributes.ReparsePoint"/> を使う。Windows ではシンボリックリンクも
/// ジャンクションもこの属性を持ち、.NET は Linux/macOS でもシンボリックリンクにこの属性を付ける。
/// リンクの先を解決せず属性だけを見るため、輪になっていても判定は1回の問い合わせで済み、
/// 10万行ファイルの編集や、エクスプローラの応答性に影響しない（フォルダ1つにつき1回の属性取得のみ）。
/// </summary>
public static class DirectoryLinks
{
    /// <summary>
    /// パスがシンボリックリンクまたはジャンクション（再解析ポイント）か。
    /// 存在しない・属性を読めない場合は false（通常のフォルダとして従来どおり扱う）。
    /// </summary>
    public static bool IsLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="root"/> 配下のファイルを再帰で列挙する。フォルダのリンクは中へ入らない。
    /// <see cref="SearchOption.AllDirectories"/> の代わりに使う。読めないフォルダ
    /// （権限・消失）は黙って飛ばして続ける（従来の例外で途中打ち切りよりも、列挙できる分を返す）。
    /// ルート自身がリンクでも、ルートの中は列挙する（呼び出し側が指定した起点のため）。
    /// </summary>
    public static IEnumerable<string> EnumerateFilesRecursive(string root, string searchPattern = "*")
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            // 列挙の途中で例外が出ても、そこまでの結果は取りこぼさないよう先に配列へ取る。
            string[] files;
            string[] subDirs;
            try
            {
                files = Directory.EnumerateFiles(dir, searchPattern).ToArray();
                subDirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files) yield return file;
            foreach (var subDir in subDirs)
            {
                if (!IsLink(subDir)) pending.Push(subDir);
            }
        }
    }

    /// <summary>
    /// <paramref name="root"/> 配下のフォルダを再帰で列挙する（<paramref name="root"/> 自身は含まない）。
    /// フォルダのリンクは列挙結果には含めず、中へも入らない。コピー前にフォルダ構造を作る用途向け。
    /// </summary>
    public static IEnumerable<string> EnumerateDirectoriesRecursive(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subDir in subDirs)
            {
                if (IsLink(subDir)) continue;
                yield return subDir;
                pending.Push(subDir);
            }
        }
    }

    /// <summary><paramref name="root"/> 配下のファイル数（リンクの中は数えない）。</summary>
    public static int CountFilesRecursive(string root) => EnumerateFilesRecursive(root).Count();
}
