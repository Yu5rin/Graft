using System.IO;

namespace Graft.Core;

/// <summary>
/// 仕様書6.8 多重起動の防止。名前付き Mutex の取得・解放のみを担当する
/// （既存ウィンドウを前面へ出す処理はUI側の責務）。
/// 非Windowsでも動作するよう、Mutex名に "Global\" プレフィックスは付与しない
/// （Unix版ランタイムのMutexはこの構文をサポートしないため）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// 指定名の名前付きMutexを取得する。既に他プロセスが起動中で取得できない場合は null を返す。
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("名前を指定してください", nameof(name));

        try
        {
            // Windows / 非Windows で構文上の差はないが、将来プラットフォーム固有の命名規則が
            // 必要になった場合に備えて分岐点を明示しておく（現状はどちらも同じ名前を使う）。
            var mutexName = OperatingSystem.IsWindows() ? name : name;
            var mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            if (createdNew) return new SingleInstanceGuard(mutex);

            mutex.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // 権限不足やOS側の名前空間の制約などで判定不能な場合は、安全側に倒して
            // 「取得できなかった」として扱い、多重起動を許してしまわないようにする。
            return null;
        }
    }

    /// <summary>Mutexを解放する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
