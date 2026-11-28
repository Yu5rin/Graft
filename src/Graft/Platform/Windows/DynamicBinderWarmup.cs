using System.Dynamic;

namespace Graft.Platform.Windows;

/// <summary>
/// Excel VBA 連携の COM 呼び出しは <c>dynamic</c> で書いてあり、実行時に C# の動的バインダー
/// （<c>Microsoft.CSharp</c>）と、式木・DLR の部品（<c>System.Linq.Expressions</c>・<c>System.Dynamic.Runtime</c>）を
/// <b>初めて使うとき</b>に読み込む。起動時に先に使っておき、読み込みを済ませておくためのもの。
/// <para>
/// 【なぜ要るか（実機の報告）】 1.3.1 のまま起動した Graft が、自動更新（確認なしで入れ替える設定）で 1.3.2 の
/// 入れ替えを済ませたあと、再起動する前に、ブックの開閉の調べ直し（「VBA バッジの更新」）が
/// <c>FileNotFoundException: 'Microsoft.CSharp, Version=8.0.0.0 ...'</c> で4回続けて失敗した
/// （入れ替えは 09:17、最初の失敗は 09:24。再起動したあとは出ていない）。ブックの開閉を調べる処理は、画面が前面に
/// 来たときなどに初めて <c>dynamic</c> を使うので、<b>起動から入れ替えまでの間にまだ一度も使っていなければ、動的バインダーを
/// 入れ替えのあとで初めて読み込むことになる</b>。実行中の exe は入れ替えで別の名前に退避され、新しい exe が置かれるため、
/// あとから読み込む部品が見つからなくなる、という見立て。
/// </para>
/// <para>
/// 【対策】 起動のときに、背景のスレッドで <c>dynamic</c> を一度だけ使い、必要な部品を先に読み込む。これなら、
/// 起動した時点（入れ替えより前）に読み込みが済む。画面の立ち上がりを遅らせないよう、呼び出し元を待たせない
/// （<see cref="StartInBackground"/>）。失敗しても何もしない（読み込めなかったときは、従来どおり、使うときに失敗するだけ）。
/// </para>
/// <para>
/// 【確かめられていないこと】 部品が見つからなくなる仕組みは、実機のログからの推定で、Linux では確かめられない。
/// 自動更新のあと、再起動しないままで「VBA バッジの更新」が動くかは、実機で確かめる
/// （docs/Windows実機検証手順.md の 6-37）。
/// </para>
/// </summary>
internal static class DynamicBinderWarmup
{
    private static int _started;

    /// <summary>
    /// 背景のスレッドで <see cref="Run"/> を一度だけ始める。2回目以降は何もしない。呼び出し元は待たされない。
    /// </summary>
    public static void StartInBackground()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(Run);
    }

    /// <summary>
    /// <c>dynamic</c> を一度使って、動的バインダーと式木・DLR の部品を読み込ませる。例外は外へ出さない。
    /// COM のオブジェクトには触れない（Excel を起動しない）。<see cref="ExpandoObject"/> のメンバーの設定と取得で、
    /// COM の呼び出しと同じ C# の動的バインダーの経路（メンバーの取得・設定、結果の型変換）を通す。
    /// </summary>
    internal static void Run()
    {
        try
        {
            // 型の参照だけでもアセンブリは読み込まれるが、バインダーが使う内部の部品（式木のコンパイルなど）は
            // 実際に動的な呼び出しをしないと読み込まれないので、両方行う。
            _ = typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly;

            dynamic probe = new ExpandoObject();
            probe.value = 1;
            int read = probe.value;
            _ = read;
        }
        catch (Exception)
        {
            // 読み込めなかったときは、従来どおり、使うときに失敗するだけ。起動を妨げない。
        }
    }
}
