using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Graft.Platform.Windows;

/// <summary>
/// 取得した COM オブジェクトを、使い終わったら必ず解放するための入れ物（仕様書_VBA連携 5.3）。
/// <para>
/// 【なぜ <c>FinalReleaseComObject</c> で明示的に解放するか】 <c>dynamic</c> の呼び出しが返す COM オブジェクトは
/// ランタイム呼び出し可能ラッパー（RCW）で、GC に任せると解放が遅れる。Application・Workbook・VBProject・
/// VBComponents・VBComponent・CodeModule のどれか1つでも残ると、Excel を閉じてもプロセスが残る原因になる。
/// RCW は同じ COM オブジェクトを返すたびに参照カウントが増えるので、<c>ReleaseComObject</c> を1回呼ぶだけでは
/// 足りないことがあり、カウントを0にする <c>FinalReleaseComObject</c> を使う。
/// </para>
/// <para>
/// そのため呼び出し側は、プロパティを連鎖して書かない（<c>wb.VBProject.VBComponents.Item(x)</c> のような書き方は禁止）。
/// 中間のオブジェクトを必ず変数に受けて <see cref="Own"/> に渡す。<see cref="Own"/> に渡し忘れた中間オブジェクトは
/// 解放されない。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _owned = new();

    /// <summary>COM オブジェクトならこの入れ物の所有にして、そのまま返す。COM でないもの・null は素通し。</summary>
    public object? Own(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) _owned.Add(value);
        return value;
    }

    /// <summary>後から取得したものから先に解放する。解放の失敗は握りつぶす（すでに Excel が終了した場合など）。</summary>
    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            try
            {
                Marshal.FinalReleaseComObject(_owned[i]);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException or COMException)
            {
                // 解放済み、または相手が居ない。残っても害はない。
            }
        }

        _owned.Clear();
    }

    /// <summary>1件だけ今すぐ解放する（ループの中で大量に作るオブジェクト用）。</summary>
    public void Release(object? value)
    {
        if (value is null) return;
        var index = _owned.LastIndexOf(value);
        if (index >= 0) _owned.RemoveAt(index);
        try
        {
            if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException or COMException)
        {
            // 同上。
        }
    }
}
