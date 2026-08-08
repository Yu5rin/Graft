using System.Text;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="IAutoStartService"/> のWindows実装（課題3）。仕様書2.1「レジストリ書き込みは
/// 行わない（読み取りのみ許可）」に従い、レジストリの Run キーは使わない。代わりに
/// スタートアップフォルダ（<c>%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup</c>）へ
/// 登録する。
///
/// 実装方式について: 本来この用途では .lnk（ショートカット）を置くのが一般的だが、
/// .lnkの生成には通常COM（WScript.Shell や IShellLink）か外部ライブラリが必要になる。
/// この配布物は自己完結（附録A.5）でCOM相互運用や追加ライブラリを増やしたくないため、
/// 代わりにGraft本体を起動するだけの小さな .cmd スクリプトを置く方式を採る。
/// スクリプト内で <c>start ""</c> を使うのは、.cmd自身のコンソールウィンドウを一瞬でも
/// 表示させず、かつ.cmdの終了を待たずに本体だけ起動させるため。
///
/// このクラス自体はWin32 P/Invokeや registryを一切使わない（BCLのファイルI/Oのみ）ため、
/// 他のPlatform/Windows配下のクラスと異なり <c>[SupportedOSPlatform("windows")]</c> は
/// 付けていない（実際どのOSでも動作でき、テストでもLinux上から直接検証できる）。
/// Windowsでの利用は <c>WindowsPlatformServices</c> からの配線のみで足りる。
/// </summary>
public sealed class WindowsAutoStartService : IAutoStartService
{
    private const string FileName = "Graft.cmd";

    private readonly string _startupDirectory;
    private readonly Func<string> _resolveExecutablePath;

    /// <param name="startupDirectory">
    /// スタートアップフォルダの絶対パス。省略時は <see cref="Environment.SpecialFolder.Startup"/>。
    /// テストから一時ディレクトリを渡せるようにするための注入口。
    /// </param>
    /// <param name="resolveExecutablePath">
    /// 現在の実行ファイルの絶対パスを返す関数。省略時は <see cref="Environment.ProcessPath"/>。
    /// テストで任意のパスを検証できるようにするための注入口。
    /// </param>
    public WindowsAutoStartService(string? startupDirectory = null, Func<string>? resolveExecutablePath = null)
    {
        _startupDirectory = startupDirectory
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _resolveExecutablePath = resolveExecutablePath ?? (() => Environment.ProcessPath ?? string.Empty);
    }

    // スタートアップフォルダは常に存在する（Windowsであれば）ため、常にtrue。
    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    private string ScriptPath => Path.Combine(_startupDirectory, FileName);

    public bool IsRegistered => File.Exists(ScriptPath);

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
            // "start """ の最初の引数は（省略可能な）ウィンドウタイトル。exePathにスペースを
            // 含む場合に引数として誤解釈されないよう空のタイトルを明示する定番の書き方。
            var script = "@echo off" + Environment.NewLine
                + $"start \"\" \"{exePath}\"" + Environment.NewLine;
            File.WriteAllText(ScriptPath, script, new UTF8Encoding(false));
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
            if (File.Exists(ScriptPath)) File.Delete(ScriptPath);
            return AutoStartResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return AutoStartResult.Fail($"自動起動の解除に失敗しました: {ex.Message}");
        }
    }
}
