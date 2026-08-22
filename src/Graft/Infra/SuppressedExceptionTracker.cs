using System.Collections.Concurrent;

namespace Graft.Infra;

/// <summary>
/// v1.0.7 実機不具合対応: 「握りつぶして処理を続行する」想定外の例外の発生回数を、
/// 種類（発生箇所＋例外の型）ごとに数える。
/// <para>
/// 【背景】 以前は各catch節が「インスタンスにつき最初の1回だけ」詳細（スタックトレース）を
/// ログへ残し、以後は完全に黙って握りつぶすだけだった（例:
/// <see cref="Editor.IndentGuideRenderer"/>の旧<c>_loggedDrawFailure</c>、
/// <see cref="Platform.Windows.WindowsTitleBarTheme"/>の旧<c>_dwmFailureLogged</c>）。
/// これだと「1回だけ起きたのか、毎フレーム1万回起きているのか」がログから区別できず、
/// 不具合の深刻度を見誤る恐れがあった。
/// </para>
/// <para>
/// 【使い方】 各catch節は、例外を握りつぶす際に<see cref="Record"/>を呼ぶだけでよい。
/// 「初回だけ詳細を残す」という従来の判断（スタックトレースを出すか・ダイアログを
/// 出すか等）は引き続き呼び出し側が担う。本クラスは純粋に回数だけを数える、
/// 呼び出し側の判断に影響しない副作用として設計している。
/// </para>
/// <para>
/// 【集計の反映】 終了処理の最後（<see cref="Views.StartupCoordinator.DisposeAsync"/>）で
/// <see cref="LogSummary"/>を呼び、1回以上発生した種類だけを件数付きでshutdownの
/// ログへ記録する。0件の種類は出さない（起きなかったことをログへ残す意味が無いため）。
/// </para>
/// </summary>
public sealed class SuppressedExceptionTracker
{
    private readonly ConcurrentDictionary<string, int> _counts = new();

    /// <summary>
    /// アプリ全体で共有する既定インスタンス。DIコンテナを使わない本プロジェクトの流儀
    /// （附録A.3）のもと、<see cref="Editor.IndentGuideRenderer"/>のように多数生成される
    /// インスタンスや、Loggerを引き回していないPlatform層の奥深く（X11のイベントループ等）
    /// からも配線なしにそのまま呼べるよう、staticなシングルトンとして公開する。
    /// </summary>
    public static SuppressedExceptionTracker Shared { get; } = new();

    /// <summary>
    /// 握りつぶした例外を1件記録する。<paramref name="context"/>は発生箇所を表す短い識別子
    /// （既存のLogger eventTypeがあれば揃える。例: "indent-guide-draw"）。
    /// 例外の型名と組み合わせて「発生箇所＋型」の組を1つの種類として数える
    /// （同じ箇所でも例外の型が異なれば別の種類として区別する）。
    /// </summary>
    public void Record(string context, Exception exception)
    {
        ArgumentException.ThrowIfNullOrEmpty(context);
        ArgumentNullException.ThrowIfNull(exception);

        var key = $"{context}:{exception.GetType().Name}";
        _counts.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    /// <summary>
    /// 1回以上発生した種類だけを件数付きでshutdownログへ記録する（eventType="shutdown"）。
    /// 0件の種類（＝1度も発生しなかった箇所）は出さない。
    /// </summary>
    public void LogSummary(Logger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var (key, count) in _counts)
        {
            if (count <= 0) continue;
            logger.Warn("shutdown", $"握りつぶした例外の集計: {key} を起動中に {count} 回捕捉しました");
        }
    }

    /// <summary>
    /// 単体テスト用: 集計をリセットする。<see cref="Shared"/>はプロセス全体（＝テスト
    /// プロセス全体）で共有するため、他のテストが記録した件数に汚染されないよう、
    /// このメソッドを使うテストの前後で呼ぶこと。
    /// </summary>
    internal void Reset() => _counts.Clear();
}
