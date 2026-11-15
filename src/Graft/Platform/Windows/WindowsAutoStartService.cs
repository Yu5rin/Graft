using System.Runtime.InteropServices;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IAutoStartService"/> のWindows実装（課題3）。スタートアップフォルダ
/// （<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</c>）へ、Graft本体を指す
/// ショートカット（<c>Graft.lnk</c>）を置く。
///
/// なぜ .cmd をやめたか: 以前は <c>start "" "&lt;exeのパス&gt;"</c> を書いた <c>Graft.cmd</c> を
/// UTF-8（BOMなし）で置いていた。しかし cmd.exe はバッチファイルを OEM コードページ
/// （日本語版Windowsでは CP932）で読むため、exe を日本語のフォルダ（例: <c>C:\追加\Graft\Graft.exe</c>）に
/// 置くと、UTF-8 のバイト列の「追加」が化けてパスが見つからず、Windows の起動時に Graft が
/// 起動しない不具合が実機で起きた。他のアプリで起きないのは、ショートカットやレジストリの
/// Run キーがパスを Unicode で持つため。
///
/// なぜ .lnk か: 他のアプリと同じやり方で、パスが Unicode のまま保存される。
/// なぜレジストリではないか: 仕様書2.1「レジストリ書き込みは行わない（読み取りのみ許可）」に従う。
/// なぜ COM か: .lnk の生成にはシェルの <c>IShellLinkW</c>（COM）を使うのが標準で、外部ライブラリを
/// 足さずに済む（実装は <see cref="WindowsShortcutWriter"/>）。以前のこのコメントは「COM相互運用を
/// 増やしたくない」として .cmd を選んでいたが、上の不具合が実機で出たため、その方針を改めて COM を使う。
/// スクリプト用の COM（WScript.Shell）は使わない。
///
/// 古い版が置いた <c>Graft.cmd</c> の引っ越し（利用者の操作なし）: <see cref="Enable"/> は .lnk を
/// 書いたあとで古い .cmd を消し、<see cref="Disable"/> は両方を消し、<see cref="IsRegistered"/> は
/// どちらかがあれば true にする。自動起動がオンなら起動のたびに <see cref="Enable"/> が呼び直される
/// （<c>StartupCoordinator</c>）ので、更新後の最初の起動で .cmd が .lnk に置き換わる。
/// ショートカットの作成に失敗したときは古い .cmd を消さない（それまで動いていた人の自動起動を
/// 失わないため）。.cmd へ戻す代替もしない（日本語のパスでは結局動かないため）。
///
/// このクラス自体はWin32 P/Invokeやレジストリを使わず（COMはwriter側）、ショートカットの書き込みを
/// <see cref="IShortcutWriter"/> の裏に置いてあるため、他のPlatform/Windows配下のクラスと異なり
/// <c>[SupportedOSPlatform("windows")]</c> は付けていない（差し替えれば Linux 上でもテストできる）。
/// </summary>
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string ShortcutFileName = "Graft.lnk";

    // 古い版が置いていたバッチファイル。移行のため、見つけたら消す。
    private const string LegacyScriptFileName = "Graft.cmd";

    private readonly string _startupDirectory;
    private readonly Func<string> _resolveExecutablePath;
    private readonly IShortcutWriter _shortcutWriter;

    /// <param name="startupDirectory">
    /// スタートアップフォルダの絶対パス。省略時は <see cref="Environment.SpecialFolder.Startup"/>。
    /// テストから一時ディレクトリを渡せるようにするための注入口。
    /// </param>
    /// <param name="resolveExecutablePath">
    /// 現在の実行ファイルの絶対パスを返す関数。省略時は <see cref="Environment.ProcessPath"/>。
    /// テストで任意のパスを検証できるようにするための注入口。
    /// </param>
    /// <param name="shortcutWriter">
    /// ショートカットを書く窓口。省略時は Windows 用の <see cref="WindowsShortcutWriter"/>。
    /// テストで COM を使わずに呼び出しを確かめる・失敗させるための注入口。
    /// </param>
    public WindowsAutoStartService(
        string? startupDirectory = null,
        Func<string>? resolveExecutablePath = null,
        IShortcutWriter? shortcutWriter = null)
    {
        _startupDirectory = startupDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _resolveExecutablePath = resolveExecutablePath ?? (() => Environment.ProcessPath ?? string.Empty);
        _shortcutWriter = shortcutWriter ?? CreateDefaultWriter();
    }

    private static IShortcutWriter CreateDefaultWriter()
        => OperatingSystem.IsWindows() ? new WindowsShortcutWriter() : new UnsupportedShortcutWriter();

    // スタートアップフォルダは常に存在する（Windowsであれば）ため、常にtrue。
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    private string ShortcutPath => Path.Combine(_startupDirectory, ShortcutFileName);

    private string LegacyScriptPath => Path.Combine(_startupDirectory, LegacyScriptFileName);

    public bool IsRegistered => File.Exists(ShortcutPath) || File.Exists(LegacyScriptPath);

    public AutoStartResult Enable()
    {
        var exePath = _resolveExecutablePath();
        if (string.IsNullOrEmpty(exePath))
        {
            return AutoStartResult.Fail("実行ファイルのパスを取得できなかったため、自動起動を登録できませんでした。");
        }

        try
        {
            Directory.CreateDirectory(_startupDirectory);
            var workingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;
            _shortcutWriter.Write(ShortcutPath, exePath, workingDirectory, "Graft");

            // 書いたつもりでも実物が無ければ、登録できていないのに成功と返してしまう。
            if (!File.Exists(ShortcutPath))
            {
                return AutoStartResult.Fail("自動起動のショートカットを作成できませんでした（ファイルが見つかりません）。");
            }

            // ここまで来て初めて古い .cmd を消す（失敗時に自動起動を失わないため）。
            if (File.Exists(LegacyScriptPath)) File.Delete(LegacyScriptPath);
            return AutoStartResult.Ok();
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                       or InvalidCastException or PlatformNotSupportedException)
        {
            return AutoStartResult.Fail($"自動起動のショートカットを作成できませんでした: {ex.Message}");
        }
    }

    public AutoStartResult Disable()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            if (File.Exists(LegacyScriptPath)) File.Delete(LegacyScriptPath);
            return AutoStartResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AutoStartResult.Fail($"自動起動の解除に失敗しました: {ex.Message}");
        }
    }

    /// <summary>Windows 以外で既定の窓口が使われたとき用（通常は到達しない）。</summary>
    private sealed class UnsupportedShortcutWriter : IShortcutWriter
    {
        public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
            => throw new PlatformNotSupportedException("ショートカットの作成は Windows でのみ使えます。");
    }
}
