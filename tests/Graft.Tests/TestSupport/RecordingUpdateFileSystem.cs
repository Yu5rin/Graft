using System.IO;
using Graft.Core.Update;

namespace Graft.Tests.TestSupport;

/// <summary>
/// <see cref="IUpdateFileSystem"/>のテスト用実装。実際のファイルI/O（<see cref="RealUpdateFileSystem"/>と
/// 同じ<see cref="File"/>呼び出し）はそのまま行いつつ、呼び出しを記録し、指定した回数目の
/// 呼び出しで意図的に例外を投げられるようにする。
///
/// 【なぜ「実際にファイル操作をしつつ失敗を注入する」形にしたか】
/// <see cref="SelfUpdateInstaller"/>のロールバックは「途中まで実際にリネーム・コピーされた
/// ファイルを、正しい状態へ戻せるか」を検証したい。ファイル操作そのものをすべてモック化して
/// しまうと「本当にファイルが元に戻ったか」を確認できなくなるため、実ファイル操作は
/// <see cref="RealUpdateFileSystem"/>へ委譲し、「Nコール目で失敗させる」という一点だけを
/// 差し替える。
/// </summary>
public sealed class RecordingUpdateFileSystem : IUpdateFileSystem
{
    private readonly RealUpdateFileSystem _real = new();
    private int _callCount;

    /// <summary>これまでの呼び出しの記録（例: "move:Graft.exe" "copy:libSkiaSharp.dll"）。</summary>
    public List<string> Log { get; } = new();

    /// <summary>
    /// この回数目（1始まり）の操作呼び出し（MoveFile/CopyFileのみを数える。FileExists/DeleteFileは
    /// 数えない）で例外を投げる。nullなら失敗を注入しない（全操作成功）。
    /// </summary>
    public int? FailOnCallNumber { get; set; }

    public bool FileExists(string path) => _real.FileExists(path);

    public void MoveFile(string sourcePath, string destinationPath)
    {
        Log.Add($"move:{Path.GetFileName(sourcePath)}->{Path.GetFileName(destinationPath)}");
        MaybeFail();
        _real.MoveFile(sourcePath, destinationPath);
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        Log.Add($"copy:{Path.GetFileName(sourcePath)}->{Path.GetFileName(destinationPath)}");
        MaybeFail();
        _real.CopyFile(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => _real.DeleteFile(path);

    private void MaybeFail()
    {
        _callCount++;
        if (FailOnCallNumber == _callCount)
        {
            throw new IOException($"テスト用に注入した失敗（{_callCount}回目の操作）。");
        }
    }
}
