using System;
using System.Diagnostics;
using System.Linq;

namespace Graft.Tests.TestSupport;

/// <summary>
/// 「この処理は十分速いか」を絶対時間のしきい値で確かめる性能テスト用の測り方。
///
/// 【なぜ1回きりの測定をやめたか】以前の性能テストは、呼び出しを1回だけ <see cref="Stopwatch"/> で測って
/// しきい値と比べていた。これには2つの弱点がある。
/// <list type="number">
/// <item>最初の1回には JIT コンパイル（と型の初期化）の費用が乗る。実測では、手元で 12〜17ms の判定が
/// CI のリリース実行（Linux の共有ランナー）で 384.89ms（基準 200ms）になり、処理そのものは変わっていないのに落ちた。</item>
/// <item>共有ランナーでは、ほかのテストが並行に走って CPU を奪い合う。1回の測定は、その瞬間の揺れをそのまま受ける。</item>
/// </list>
/// そこで、<b>最初に1回呼んで温め</b>（JIT・初期化の費用を測定から外す）、そのあと複数回測った
/// <b>中央値</b>で判定する。中央値は、たまたま1〜2回だけ他のテストに邪魔されて遅くなっても動かないが、
/// 処理が本当に遅くなった（毎回遅い）ときは確実に上がる。つまり「揺れには強く、退行には敏感」。
/// しきい値そのものは緩めない（緩めると本当の退行を見逃すため）。
///
/// 最小値でなく中央値にしているのは、最小値だと「たまたま速かった1回」を拾って、毎回は遅い退行を
/// 見逃しうるため。
/// </summary>
public static class PerformanceMeasure
{
    /// <summary>測定の回数（温め用の1回は含まない）。奇数にして中央値を1つの実測値にする。</summary>
    public const int DefaultRuns = 7;

    /// <summary>
    /// <paramref name="action"/> を最初に1回だけ呼んで温めてから <paramref name="runs"/> 回測り、中央値（ミリ秒）を返す。
    /// 温め用の1回の結果は捨てる。
    /// </summary>
    public static double MedianMilliseconds(Action action, int runs = DefaultRuns)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (runs < 1) throw new ArgumentOutOfRangeException(nameof(runs));

        action(); // 温め。初回の JIT・型の初期化の費用を測定から外す。

        var times = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        // 小数精度で比べる（ElapsedMilliseconds の整数丸めだと、1ms 未満の差が潰れる）。
        return times.OrderBy(t => t).ElementAt(runs / 2);
    }
}
