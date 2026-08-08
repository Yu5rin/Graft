using System.Text;

namespace Graft.Platform.Linux;

/// <summary>
/// <see cref="IAutoStartService"/> のLinux実装（課題3）。XDG Desktop Entry Specificationの
/// autostartの仕組みに従い、<c>$XDG_CONFIG_HOME/autostart/graft.desktop</c>
/// （未設定時は <c>~/.config/autostart/graft.desktop</c>）へ .desktop ファイルを置く。
/// 主要なLinuxデスクトップ環境（GNOME・KDE・XFCE等）はいずれもこの仕様に従って
/// ログイン時にアプリを起動する。
/// </summary>
public sealed class LinuxAutoStartService : IAutoStartService
{
    private const string FileName = "graft.desktop";

    private readonly string _autostartDirectory;
    private readonly Func<string> _resolveExecutablePath;

    /// <param name="autostartDirectory">
    /// autostartディレクトリの絶対パス。省略時は <c>$XDG_CONFIG_HOME/autostart</c>
    /// （<c>LinuxTrashService</c>のXDG_DATA_HOME判定と同様、環境変数を都度読み直す）。
    /// テストから一時ディレクトリを渡せるようにするための注入口。
    /// </param>
    /// <param name="resolveExecutablePath">
    /// 現在の実行ファイルの絶対パスを返す関数。省略時は <see cref="Environment.ProcessPath"/>。
    /// </param>
    public LinuxAutoStartService(string? autostartDirectory = null, Func<string>? resolveExecutablePath = null)
    {
        _autostartDirectory = autostartDirectory ?? ResolveDefaultAutostartDirectory();
        _resolveExecutablePath = resolveExecutablePath ?? (() => Environment.ProcessPath ?? string.Empty);
    }

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    private string DesktopFilePath => Path.Combine(_autostartDirectory, FileName);

    public bool IsRegistered => File.Exists(DesktopFilePath);

    public AutoStartResult Enable()
    {
        var exePath = _resolveExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            return AutoStartResult.Fail("実行ファイルのパスを取得できなかったため、自動起動を登録できませんでした。");
        }

        try
        {
            Directory.CreateDirectory(_autostartDirectory);

            // Exec= はダブルクォートで囲み、実行ファイルのパスに空白を含む場合に備える
            // （Desktop Entry Specificationはパス中のダブルクォートを許すが、そのような
            // パスは想定しないため簡易なクォートに留める）。
            var content = "[Desktop Entry]" + "\n"
                + "Type=Application" + "\n"
                + "Name=Graft" + "\n"
                + $"Exec=\"{exePath}\"" + "\n"
                + "X-GNOME-Autostart-enabled=true" + "\n";
            File.WriteAllText(DesktopFilePath, content, new UTF8Encoding(false));
            return AutoStartResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AutoStartResult.Fail($"自動起動の登録に失敗しました: {ex.Message}");
        }
    }

    public AutoStartResult Disable()
    {
        try
        {
            if (File.Exists(DesktopFilePath)) File.Delete(DesktopFilePath);
            return AutoStartResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AutoStartResult.Fail($"自動起動の解除に失敗しました: {ex.Message}");
        }
    }

    /// <summary>XDG Base Directory Specificationに従う（LinuxTrashServiceのXDG_DATA_HOME判定と同じ考え方）。</summary>
    private static string ResolveDefaultAutostartDirectory()
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configHome = Path.Combine(home, ".config");
        }
        return Path.Combine(configHome, "autostart");
    }
}
