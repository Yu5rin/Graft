using System.Runtime.InteropServices;
using Graft.Core;
using Graft.Core.Vba;

namespace Graft.Platform.Windows;

/// <summary>
/// COM の失敗を <see cref="GraftIssue"/>（E801／E805）へ写す。COM に触れない（HRESULT の分類は
/// <see cref="VbaComHResult"/>）ので Linux でテストできる。
/// </summary>
public static class ComFailures
{
    /// <summary>呼び出し全体の時間切れ（<c>Vba.CallTimeoutMs</c>）。</summary>
    public static GraftIssue Unresponsive(TimeSpan timeout)
        => GraftIssue.Of(
            ErrorCode.E805,
            $"Excel の応答が {timeout.TotalSeconds:0.#} 秒以内に戻りませんでした。マクロの実行中・中断中、またはダイアログ（VBE のモーダル表示を含む）が開いていないか確認してください。");

    /// <summary>拒否され続けて再試行が尽きた（<c>Vba.ComTimeoutMs</c>）。</summary>
    public static GraftIssue Busy()
        => GraftIssue.Of(
            ErrorCode.E805,
            "Excel が呼び出しを受け付けませんでした。セルの編集中ならセルを確定し、開いているダイアログを閉じてからやり直してください。");

    /// <summary>
    /// 想定外の COM の例外。切断系（Excel の終了、RPC サーバー利用不可）もそれ以外も E801 にし、
    /// 原因の調査に使えるよう HRESULT を詳細に添える。
    /// </summary>
    public static GraftIssue FromException(Exception ex)
    {
        var hresult = ex is COMException com ? com.HResult : ex.HResult;
        var kind = ex is COMException ? VbaComHResult.Classify(hresult) : VbaComFailure.Other;
        var prefix = kind == VbaComFailure.Disconnected
            ? "Excel との接続が切れました（Excel が終了した可能性があります）。"
            : "Excel と通信できませんでした。";
        return GraftIssue.Of(ErrorCode.E801, $"{prefix}（0x{hresult:X8}: {ex.Message}）");
    }
}
