using System.Diagnostics;
using Avalonia.Threading;

namespace Graft.UiTests.TestSupport;

/// <summary>
/// 「UIが固まって見える」ことを、実測できる形で判定するための計測器。
///
/// 判定の考え方: 画面が更新される・マウスのホバー強調が追従する・ボタンが押せる、といった
/// 見た目の応答はすべて「UIスレッドがDispatcherのジョブを1件処理できること」に帰着する。
/// そこで<see cref="DispatcherPriority.Background"/>（＝入力・レイアウト・描画より低い優先度。
/// これが動くなら、それより優先度の高い描画・入力処理は当然動ける）で自分自身を再投函し
/// 続けるジョブを1本流しておき、実行できた時刻の間隔を測る。最大の間隔が「UIスレッドが
/// 連続して塞がっていた時間」であり、これがそのまま「固まって見える時間」になる。
///
/// 点検エージェントが実機Xvfb上で使った「操作直後にマウスを2ボタン間で往復させ、ホバー
/// 強調の追従をフレームのハッシュで比較する」手法と測っているものは同じ（ホバー強調が
/// 追従しない＝この間隔が開いている）だが、こちらはフレーム取得に依存せずヘッドレスの
/// 単体テストとして固定できるため、回帰テストにはこの形を使う。
/// </summary>
internal sealed class UiThreadStallProbe
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTickMs;
    private double _longestGapMs;
    private int _tickCount;
    private bool _running;

    private UiThreadStallProbe()
    {
    }

    /// <summary>計測を開始する。必ずUIスレッド上で呼ぶこと。</summary>
    public static UiThreadStallProbe Start()
    {
        var probe = new UiThreadStallProbe();
        probe._running = true;
        probe._lastTickMs = probe._clock.Elapsed.TotalMilliseconds;
        probe.Schedule();
        return probe;
    }

    /// <summary>計測中にUIスレッドがジョブを処理できた回数。0なら一度も応答していない。</summary>
    public int TickCount => _tickCount;

    /// <summary>
    /// 計測を止め、UIスレッドが連続して塞がっていた最長時間（ミリ秒）を返す。
    /// 最後にジョブを処理してから停止までの空白も1回の停滞として数えるため、
    /// 「操作の最後まで塞がったまま終わった」場合もきちんと計上される。
    /// </summary>
    public double StopAndMeasureLongestStallMs()
    {
        _running = false;
        Record();
        return _longestGapMs;
    }

    private void Schedule()
        => Dispatcher.UIThread.Post(
            () =>
            {
                if (!_running) return;
                _tickCount++;
                Record();
                Schedule();
            },
            DispatcherPriority.Background);

    private void Record()
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        var gap = now - _lastTickMs;
        if (gap > _longestGapMs) _longestGapMs = gap;
        _lastTickMs = now;
    }
}
