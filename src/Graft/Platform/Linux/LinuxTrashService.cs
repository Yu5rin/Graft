using System.Globalization;
using System.Text;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="ITrashService"/> のLinux実装（仕様書v2.1 19章 L4）。
/// freedesktop.org の Trash specification に従い、<c>$XDG_DATA_HOME/Trash</c>
/// （既定は <c>~/.local/share/Trash</c>）配下の <c>files/</c> へ実体を移し、
/// 同名の <c>.trashinfo</c> を <c>info/</c> へ書き出す。
///
/// ごみ箱と対象が別のファイルシステムにある場合、ディレクトリ間の移動は失敗する。
/// 仕様上は各ボリューム直下の <c>.Trash-$uid</c> を使う定めだが、書き込み可否や
/// マウント構成に強く依存し失敗も多いため、ここでは移動できない場合は素直に false を
/// 返して呼び出し側の判断（通常削除へのフォールバック等）に委ねる。
/// </summary>
public sealed class LinuxTrashService : ITrashService
{
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool Send(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) && !Directory.Exists(full)) return false;

            var trashRoot = ResolveTrashRoot();
            var filesDirectory = Path.Combine(trashRoot, "files");
            var infoDirectory = Path.Combine(trashRoot, "info");
            Directory.CreateDirectory(filesDirectory);
            Directory.CreateDirectory(infoDirectory);

            var name = ReserveUniqueName(filesDirectory, infoDirectory, Path.GetFileName(full));

            // .trashinfo を先に書く。実体の移動に失敗した場合は情報ファイルを取り消す。
            var infoPath = Path.Combine(infoDirectory, name + ".trashinfo");
            File.WriteAllText(infoPath, BuildTrashInfo(full), new UTF8Encoding(false));

            try
            {
                MoveInto(full, Path.Combine(filesDirectory, name));
            }
            catch (Exception)
            {
                TryDelete(infoPath);
                throw;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // ごみ箱へ送れない場合（別ファイルシステム・権限不足等）は失敗として返し、
            // 呼び出し側の判断に委ねる。
            return false;
        }
    }

    /// <summary>ごみ箱の起点（<c>$XDG_DATA_HOME/Trash</c>、未設定なら <c>~/.local/share/Trash</c>）。</summary>
    private static string ResolveTrashRoot()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            dataHome = Path.Combine(home, ".local", "share");
        }
        return Path.Combine(dataHome, "Trash");
    }

    /// <summary>
    /// ごみ箱内で衝突しない名前を決める。実体と情報ファイルの双方が未使用の名前を選ぶ
    /// （仕様上、両者は同じ基底名で対になる必要がある）。
    /// </summary>
    private static string ReserveUniqueName(string filesDirectory, string infoDirectory, string original)
    {
        var candidate = original;
        var stem = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);

        for (var i = 1; IsTaken(filesDirectory, infoDirectory, candidate); i++)
        {
            candidate = $"{stem}_{i}{extension}";
        }
        return candidate;
    }

    private static bool IsTaken(string filesDirectory, string infoDirectory, string name)
        => File.Exists(Path.Combine(filesDirectory, name))
           || Directory.Exists(Path.Combine(filesDirectory, name))
           || File.Exists(Path.Combine(infoDirectory, name + ".trashinfo"));

    /// <summary>
    /// Trash specification の情報ファイル本文を作る。Path は絶対パスを
    /// パーセントエンコードしたもの、DeletionDate はローカル時刻のISO 8601形式。
    /// </summary>
    private static string BuildTrashInfo(string originalPath)
    {
        var builder = new StringBuilder();
        builder.Append("[Trash Info]\n");
        builder.Append("Path=").Append(EncodePath(originalPath)).Append('\n');
        builder.Append("DeletionDate=")
            .Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture))
            .Append('\n');
        return builder.ToString();
    }

    /// <summary>パス区切りはそのまま残し、それ以外の非予約外文字をパーセントエンコードする。</summary>
    private static string EncodePath(string path)
    {
        var builder = new StringBuilder(path.Length);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (c == '/' || char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    private static void MoveInto(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // 取り消しに失敗しても、呼び出し側へは元の失敗を伝える。
        }
    }
}
