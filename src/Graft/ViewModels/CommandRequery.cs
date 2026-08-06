namespace Graft.ViewModels;

/// <summary>
/// コマンドの<c>CanExecute</c>を再評価させる仕組みを、UIフレームワークから切り離して提供する
/// （仕様書v2.1 19章 L3）。WPFには<c>System.Windows.Input.CommandManager</c>という
/// アプリ全体の再評価機構があるが、Avaloniaには対応物が無い。<see cref="RelayCommand"/>等が
/// 直接<c>CommandManager</c>を参照するとViewModel層をWPF専用にしてしまうため、
/// ここを唯一の接点にして各UI側からフックを差し込む。
///
/// フックが設定されていない場合（Avalonia版・単体テスト）は、このクラス自身が持つ
/// 弱参照リストで代替する。<see cref="Invalidate"/>を呼ぶタイミングは各UIの責務で、
/// Avalonia版はWPFの<c>CommandManager</c>と同様にポインタ操作・キー入力・フォーカス移動の
/// 後で呼び出す。
///
/// 購読者を強参照で保持するとバインディング先のUI要素が解放されなくなるため、
/// WPFの<c>CommandManager</c>と同じく弱参照で保持し、回収済みの項目は発火時に取り除く。
/// </summary>
public static class CommandRequery
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<EventHandler>> Handlers = new();

    /// <summary>購読を委譲するフック。WPF版が<c>CommandManager.RequerySuggested</c>へ繋ぐ。</summary>
    public static Action<EventHandler>? SubscribeHook { get; set; }

    /// <summary>購読解除を委譲するフック。</summary>
    public static Action<EventHandler>? UnsubscribeHook { get; set; }

    /// <summary>再評価要求を委譲するフック。</summary>
    public static Action? InvalidateHook { get; set; }

    /// <summary>再評価通知を購読する。</summary>
    public static void Subscribe(EventHandler? handler)
    {
        if (handler is null) return;
        if (SubscribeHook is not null)
        {
            SubscribeHook(handler);
            return;
        }

        lock (Gate)
        {
            Handlers.Add(new WeakReference<EventHandler>(handler));
        }
    }

    /// <summary>再評価通知の購読を解除する。</summary>
    public static void Unsubscribe(EventHandler? handler)
    {
        if (handler is null) return;
        if (UnsubscribeHook is not null)
        {
            UnsubscribeHook(handler);
            return;
        }

        lock (Gate)
        {
            for (var i = Handlers.Count - 1; i >= 0; i--)
            {
                if (!Handlers[i].TryGetTarget(out var existing) || existing == handler)
                {
                    Handlers.RemoveAt(i);
                }
            }
        }
    }

    /// <summary>すべてのコマンドに<c>CanExecute</c>の再評価を促す。</summary>
    public static void Invalidate()
    {
        if (InvalidateHook is not null)
        {
            InvalidateHook();
            return;
        }

        EventHandler[] alive;
        lock (Gate)
        {
            var buffer = new List<EventHandler>(Handlers.Count);
            for (var i = Handlers.Count - 1; i >= 0; i--)
            {
                if (Handlers[i].TryGetTarget(out var handler)) buffer.Add(handler);
                else Handlers.RemoveAt(i);
            }

            alive = buffer.ToArray();
        }

        foreach (var handler in alive)
        {
            handler(null, EventArgs.Empty);
        }
    }

    /// <summary>
    /// フックと購読者をすべて解除する。プロセス内で複数回アプリを起動する単体テストが、
    /// 前のテストの購読を引きずらないようにするために使う。
    /// </summary>
    public static void Reset()
    {
        SubscribeHook = null;
        UnsubscribeHook = null;
        InvalidateHook = null;
        lock (Gate)
        {
            Handlers.Clear();
        }
    }
}
