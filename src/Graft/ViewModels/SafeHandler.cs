using Graft.Core;

namespace Graft.ViewModels;

/// <summary>
/// UIのイベントハンドラ（<c>async void</c>）から呼ぶ非同期処理の安全網。
///
/// 附録A.4のとおり、ユーザー操作に起因する失敗は <see cref="GraftResult{T}"/> で扱い、
/// 想定外の例外のみ最上位で記録する方針である。しかし <c>async void</c> の中で例外が出ると
/// 最上位のハンドラへ直行し、そこでアプリが終了してしまう。ファイルを1つ開けなかった程度で
/// エディタが落ちるのは設計目標5（製品相当の完成度）に反するため、
/// ハンドラ単位で捕捉して通知し、アプリの継続を優先する。
/// </summary>
public static class SafeHandler
{
    /// <summary>
    /// 想定外の例外が発生したときに呼ばれる。起動処理側でログ出力とUI通知を割り当てる。
    /// 割り当てられていない場合は握り潰さず、そのまま再スローして最上位へ委ねる。
    /// </summary>
    public static Action<string, Exception>? OnUnexpected { get; set; }

    /// <summary>非同期処理を実行し、想定外の例外を捕捉して通知する。</summary>
    /// <param name="context">どの操作で起きたかを示す日本語の短い説明。</param>
    /// <param name="action">実行する処理。</param>
    public static async Task RunAsync(string context, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 取り消しは異常ではない。何も通知しない。
        }
        catch (Exception ex)
        {
            if (OnUnexpected is null) throw;
            OnUnexpected(context, ex);
        }
    }
}
