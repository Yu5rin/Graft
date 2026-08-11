namespace Graft.Core;

/// <summary>
/// 不具合1（実機で確認された未処理例外の修正）: AvaloniaEdit内部（折りたたみの再描画等、
/// レイアウト/描画パスの中）から実行時に投げられる例外は、その発生元が
/// <c>Avalonia.Threading.DispatcherOperation.InvokeCore</c>から直接であり、このアプリのコードの
/// どのtry/catchにも引っかからないまま<c>AppDomain.UnhandledException</c>まで抜けてプロセスごと
/// 落ちる（実機ログ・<see cref="Editor.FoldingSupport"/>のクラスコメント・
/// tests/Graft.UiTests/EditorTests.csの再現テスト参照）。
///
/// Avaloniaの<c>Dispatcher.UIThread.UnhandledException</c>イベントでハンドラが
/// <c>e.Handled = true</c>にすると、そのジョブ1回分の失敗として記録するだけでアプリの継続を
/// 許す（<c>Avalonia.Threading.DispatcherOperation.InvokeCore</c>の実装で確認済み）。ただし
/// 何でも握りつぶすと本当に致命的な状態異常まで隠してしまい設計目標5（製品相当の完成度）に
/// 反するため、発生元がAvaloniaEdit自身（このアプリが直接書いていないサードパーティコード）で
/// あるものに限って継続を許可する、という判定だけを純粋関数として切り出す
/// （<c>App.axaml.cs</c>側から呼ぶ。Avalonia型に依存させないため<c>Graft.Core</c>に置く）。
/// </summary>
public static class AvaloniaEditExceptionGuard
{
    private const string AvaloniaEditAssemblyName = "AvaloniaEdit";

    /// <summary>
    /// UIスレッドの未処理例外を握りつぶしてアプリを継続させてよいかどうか。
    /// <see cref="Exception.Source"/>（例外を投げたアセンブリ名。.NETが例外送出時に自動設定する）が
    /// AvaloniaEditである場合のみtrueを返す。それ以外（このアプリ自身のコードが投げた想定外の
    /// 例外を含む）はfalseとし、これまでどおりアプリを終了させる（本当に致命的な状態異常を
    /// 握りつぶさないため）。
    /// </summary>
    public static bool ShouldContinue(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return string.Equals(exception.Source, AvaloniaEditAssemblyName, StringComparison.Ordinal);
    }
}
