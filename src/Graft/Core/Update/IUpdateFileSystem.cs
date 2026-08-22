namespace Graft.Core.Update;

/// <summary>
/// <see cref="SelfUpdateInstaller"/>が使う、最小限のファイル操作の抽象。
///
/// 【なぜ抽象化するか】自己置き換え処理の核心は「途中で失敗しても必ずロールバックできる」こと
/// （指示書の最重要事項）。これを実際のWindows環境（実行中のexeのリネーム）でしか起きない
/// 状況を再現せずに単体テストするため、ファイル操作をこのインターフェース越しに行い、
/// テストでは失敗を狙った箇所だけ例外を投げるフェイク実装に差し替える。
/// 本番は<see cref="RealUpdateFileSystem"/>（<see cref="File"/>への薄いラッパー）を使う。
/// </summary>
public interface IUpdateFileSystem
{
    /// <summary>指定パスにファイルが存在するか。</summary>
    bool FileExists(string path);

    /// <summary>
    /// ファイルをリネーム（移動）する。実行中のexe・ロード済みDLLでも、内容の上書きはできないが
    /// リネームはできるという性質（指示書参照）を使うための操作。移動先が既に存在する場合は
    /// 失敗する（上書きしない。呼び出し側が事前に必要なら<see cref="DeleteFile"/>で消しておく）。
    /// </summary>
    void MoveFile(string sourcePath, string destinationPath);

    /// <summary>ファイルをコピーする。</summary>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>ファイルを削除する。存在しない場合は何もしない（例外を投げない）。</summary>
    void DeleteFile(string path);
}

/// <summary><see cref="IUpdateFileSystem"/>の本番実装。<see cref="File"/>への薄いラッパー。</summary>
public sealed class RealUpdateFileSystem : IUpdateFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public void MoveFile(string sourcePath, string destinationPath) => File.Move(sourcePath, destinationPath, overwrite: false);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) => File.Copy(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
