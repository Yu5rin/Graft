using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// <see cref="ITrashService"/> のWindows実装。移設元は <c>Core/RevisionIndex.cs</c> の
/// <c>RecycleBin</c>。<c>Microsoft.VisualBasic</c> への参照を追加しないため、shell32.dll の
/// <c>SHFileOperationW</c> を直接呼び出す（附録A.3の依存関係制約に準拠）。ロジックは
/// 移設元から変更していない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrashService : ITrashService
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofSilent = 0x0004;

    public bool IsSupported => true;

    public string? UnsupportedReason => null;

    public bool Send(string path)
    {
        // pFrom はNULL文字2つで終端された複数文字列形式が必要。マーシャラが末尾にもう1つ
        // NULLを付与するため、ここで明示的に付けておくことで確実に二重終端となる。
        var op = new ShFileOpStruct
        {
            wFunc = FoDelete,
            pFrom = path + '\0' + '\0',
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofSilent),
        };
        var result = SHFileOperationW(ref op);
        return result == 0 && !op.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);
}
