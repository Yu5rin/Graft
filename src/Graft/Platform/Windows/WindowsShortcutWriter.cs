using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace Graft.Platform.Windows;

/// <summary>
/// ショートカット（.lnk）を書く窓口。<see cref="WindowsAutoStartService"/> が COM の呼び出しを
/// 直接持たずに済むようにするための境目で、テストでは Linux でも動く差し替えを渡せる。
/// 失敗は <see cref="COMException"/>・<see cref="IOException"/> などの例外で知らせる
/// （呼び出し側が利用者向けの日本語の理由へ変える）。
/// </summary>
public interface IShortcutWriter
{
    /// <summary>
    /// <paramref name="shortcutPath"/> に、<paramref name="targetPath"/> を指すショートカットを書く。
    /// 既にあれば上書きする。引数は無し。アイコンは対象自身（インデックス 0）。
    /// </summary>
    void Write(string shortcutPath, string targetPath, string workingDirectory, string description);
}

/// <summary>
/// Windows 標準の <c>IShellLinkW</c> ＋ <c>IPersistFile</c>（CLSID 00021401-0000-0000-C000-000000000046）で
/// .lnk を書く実装。.lnk はパスを Unicode で持つので、日本語のフォルダでも文字化けしない
/// （かつての .cmd は cmd.exe が OEM コードページで読むため化けた。理由は
/// <see cref="WindowsAutoStartService"/> のクラスコメント）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShortcutWriter : IShortcutWriter
{
    public void Write(string shortcutPath, string targetPath, string workingDirectory, string description)
    {
        object? instance = null;
        try
        {
            instance = new ShellLinkCoClass();
            var link = (IShellLinkW)instance;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(workingDirectory);
            link.SetDescription(description);
            // アイコンは exe 自身に埋め込んだもの（ApplicationIcon）。インデックス 0。
            link.SetIconLocation(targetPath, 0);
            // 第2引数 true は「保存先を以後の現在のファイルにする」。通常の保存の指定。
            ((IPersistFile)instance).Save(shortcutPath, true);
        }
        finally
        {
            // COM オブジェクトはファイナライザ任せにせず、ここで確実に解放する。
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }
}

/// <summary>シェルのショートカットオブジェクト（CLSID_ShellLink）。</summary>
[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
public class ShellLinkCoClass
{
}

/// <summary>
/// <c>IShellLinkW</c>。メソッドの並び（vtable 順）が1つでもずれると別のメソッドを呼んで壊れるため、
/// Windows SDK の <c>ShObjIdl_core.h</c> の <c>IShellLinkW</c> の定義順
/// （GetPath, GetIDList, SetIDList, GetDescription, SetDescription, GetWorkingDirectory,
/// SetWorkingDirectory, GetArguments, SetArguments, GetHotkey, SetHotkey, GetShowCmd, SetShowCmd,
/// GetIconLocation, SetIconLocation, SetRelativePath, Resolve, SetPath）どおりに全部宣言している。
/// 使わないメソッドも、並びを守るために省かない。テスト（読み戻し）からも使うため public。
/// </summary>
[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
public interface IShellLinkW
{
    // WIN32_FIND_DATAW* は使わないので IntPtr（呼び出し側は IntPtr.Zero を渡す）。
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out ushort pwHotkey);
    void SetHotkey(ushort wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
    void Resolve(IntPtr hwnd, uint fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}
