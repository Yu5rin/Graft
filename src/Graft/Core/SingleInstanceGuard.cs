using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Graft.Core;

/// <summary>
/// 仕様書v1.5 6.8 多重起動の防止。名前付き Mutex の取得・解放のみを担当する
/// （既存ウィンドウを前面へ出す処理はUI側の責務）。
///
/// 【Mutex名には必ず "Global\" プレフィックスを付与する】
/// このコメントは以前「非Windowsでも動作するようGlobal\は付けない（Unix版ランタイムは
/// この構文をサポートしないため）」と書いていたが、これは誤りだった（実機調査で判明）。
/// 実際には .NET の名前付きMutexは、Windows・Linux（Unix系ランタイム）のいずれでも
/// "Global\" を付けない名前は既定で「セッション」単位のスコープになる。Linux実装では
/// 実体として <c>/tmp/.dotnet/shm/session&lt;セッションID&gt;/</c> 配下にファイルが作られ、
/// セッションIDが異なるプロセス同士は同じ名前を渡しても別々の実体を見てしまい、
/// 互いの存在を検知できない。デスクトップ環境ではログインシェル・ターミナル・
/// ファイルマネージャからのダブルクリックなど、起動経路によってセッションが分かれることが
/// 普通にあり、「同じ発行フォルダのGraftを2つ起動したら両方立ち上がってしまう」という
/// 実害が実機で確認された（仕様書6.8「バックアップとリビジョン番号の整合が複数プロセスから
/// 壊されることを防ぐための必須要件」が満たされない）。
///
/// 実機検証（別セッション= <c>setsid</c> で起動した2プロセス間、最小再現プログラムで確認）:
/// <code>
///   "Graft.SingleInstance.Mutex"        → 1つ目ACQUIRED、2つ目もACQUIRED（防げない＝バグ）
///   "Global\Graft.SingleInstance.Mutex" → 1つ目ACQUIRED、2つ目はBLOCKED（正しく防げる）
/// </code>
/// つまり Unix でも "Global\" 構文は問題なくサポートされており、むしろ付けないと
/// セッションをまたいだ多重起動防止が機能しない。
///
/// Windowsでは "Global\" はターミナルサービス（RDP等のマルチセッション環境）の
/// グローバル名前空間を意味し、通常の名前付きMutexとして機能する（今回の修正による
/// Windows側の挙動変化はない）。ただし、権限が厳しい環境（Session 0分離下で動くサービスや、
/// 一部の制限付きアカウント等）では Global\ 名前空間へのオブジェクト作成そのものが
/// <see cref="UnauthorizedAccessException"/> で拒否されることがあるため、<see cref="TryAcquire"/>
/// はその場合 "Global\" 無しの名前で再試行する（セッション内だけは多重起動を検知できる、
/// 上記バグと同じ状態への縮退）。それも失敗するごく限られた環境では、「判定不能」を
/// 「多重起動とみなして起動を止める」ではなく「起動を許可する」側に倒す。多重起動防止は
/// あくまで安全機構であり、権限の問題だけで利用者が単独起動すらできなくなる方が実害が
/// 大きいと判断したため（以前の実装は逆に倒しており、これも本タスクで是正した。詳細は
/// <see cref="TryAcquire"/> 参照）。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string GlobalPrefix = @"Global\";

    // Mutex名は Windows のカーネルオブジェクト名の実用上限（約260文字）に収まる必要があるため、
    // ハッシュは先頭16桁（64bit相当）だけを使う。衝突確率は無視できるほど小さく、
    // 「別々の発行フォルダを誤って同一視してしまう」実害の方が心配すべき対象ではない。
    private const int HashPrefixLength = 16;

    private readonly Mutex? _mutex;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// 指定名の名前付きMutexを取得する。既に他プロセスが起動中で取得できない場合は null を返す。
    ///
    /// 取得を試みる順序:
    /// <list type="number">
    /// <item>"Global\" を付けた名前（セッションをまたいで多重起動を検知できる、本来あるべき挙動）。</item>
    /// <item>それが権限等で作成できない環境向けに、"Global\" 無しの名前（セッション内のみ検知）。</item>
    /// <item>それも作成できない極端な環境では、「判定不能」を安全側＝「起動を許可する」に倒す
    ///   （多重起動防止という安全機構のために、権限問題だけで正常な単独起動までブロックしない）。</item>
    /// </list>
    /// </summary>
    public static SingleInstanceGuard? TryAcquire(string name)
    {
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("名前を指定してください", nameof(name));

        var outcome = TryCreateMutex(GlobalPrefix + name, out var mutex);
        if (outcome == AcquireOutcome.Acquired) return new SingleInstanceGuard(mutex);
        if (outcome == AcquireOutcome.AlreadyRunning)
        {
            mutex?.Dispose();
            return null;
        }

        // ここに来るのは Indeterminate（Global\ 名前空間の作成が権限等で拒否された）場合のみ。
        outcome = TryCreateMutex(name, out mutex);
        if (outcome == AcquireOutcome.Acquired) return new SingleInstanceGuard(mutex);
        if (outcome == AcquireOutcome.AlreadyRunning)
        {
            mutex?.Dispose();
            return null;
        }

        // 両方の名前でMutexの作成自体が例外になった、判定不能な環境。
        // 「多重起動とみなして起動を止める」ではなく「起動を許可する」側に倒す（クラスの説明を参照）。
        // 実体を持たないため、Disposeでは何もしない（ReleaseMutex対象が無い）。
        return new SingleInstanceGuard(null);
    }

    /// <summary>
    /// 課題4: Mutex名に発行フォルダ（<c>AppPaths.BaseDirectory</c> 相当）のパスを混ぜ込み、
    /// 別々の発行フォルダに置かれたGraftを、互いに独立したインスタンスとして扱えるようにする。
    ///
    /// 【なぜフォルダ単位にするか】
    /// 仕様書v1.5 6.8が防ごうとしているのは「バックアップとリビジョン番号の整合が複数プロセスから
    /// 壊されること」であり、対象となるのは settings.json・projects.json・back/・queue.json・logs/
    /// といった <c>installDirectory</c> 配下のファイル群に限られる。異なる発行フォルダは互いに
    /// 完全に別のデータディレクトリを読み書きするため、同時に起動していても実害がない
    /// （例: 本番用と検証用を別フォルダに置いた2つのGraft、USBメモリのポータブル版と手元の
    /// コピーを同時に開く、等）。固定文字列だけのMutex名で全インスタンスを一括りにブロックすると、
    /// この本来無害な並行起動まで妨げてしまい、かえって利用者体験を損なう。仕様書には
    /// 「フォルダ単位にする」という明記は無いため実装判断だが、6.8の意図（データ整合性の保護）に
    /// 忠実な解釈としてこちらを採用した。
    ///
    /// 【なぜ string.GetHashCode() ではなく SHA-256 を使うか】
    /// .NET の <see cref="string.GetHashCode()"/> はハッシュDoS対策のため既定でプロセスごとに
    /// 異なるシードを使い、同じ文字列でも別プロセスでは異なる値になる。Mutex名はプロセスを
    /// またいで一致して初めて意味を持つため、この用途には使えない（気付きにくい罠であり、
    /// 誤って使うと「同じフォルダの2つ目のGraftを起動しても多重起動として検知されない」
    /// 不具合を新たに生む）。決定的なハッシュであるSHA-256を使う。
    /// </summary>
    public static string BuildInstanceScopedName(string prefix, string installDirectory)
    {
        var normalized = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Windowsのパスは大小文字を区別しないため、比較前に揃える
        // （同じフォルダを異なる大小文字で指しても別インスタンス扱いにならないように）。
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return prefix + hash[..HashPrefixLength];
    }

    /// <summary>Mutexを解放する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mutex is null) return; // 判定不能で縮退した場合（実体を保持していない）。
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private enum AcquireOutcome
    {
        /// <summary>このプロセスが新規に取得できた。</summary>
        Acquired,

        /// <summary>既に他プロセスが保持している（＝多重起動）。</summary>
        AlreadyRunning,

        /// <summary>権限やOS側の名前空間の制約等で、取得できたかどうか自体を判定できなかった。</summary>
        Indeterminate,
    }

    private static AcquireOutcome TryCreateMutex(string mutexName, out Mutex? mutex)
    {
        try
        {
            mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
            return createdNew ? AcquireOutcome.Acquired : AcquireOutcome.AlreadyRunning;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            mutex = null;
            return AcquireOutcome.Indeterminate;
        }
    }
}
