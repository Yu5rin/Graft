namespace Graft.ViewModels;

/// <summary>
/// コマンドの<c>CanExecute</c>を再評価させる仕組み。
///
/// AvaloniaにはWPFの<c>CommandManager</c>のようなアプリ全体の再評価機構が無いため、
/// 同じ役割をここで担う。再評価を促すタイミング（ポインタ操作・キー入力・フォーカス移動の後）は
/// UI側の責務で、<c>App.EnableCommandRequery</c> が入力イベントを購読して
/// <see cref="Invalidate"/> を呼ぶ。
///
/// 購読者を強参照で保持するとバインディング先のUI要素が解放されなくなるため、
/// WPFの<c>CommandManager</c>と同じく弱参照で保持し、回収済みの項目は発火時に取り除く。
/// </summary>
public static class CommandRequery
{
    private static readonly object Gate = new();
    private static readonly List<WeakReference<EventHandler>> Handlers = new();

    /// <summary>再評価通知を購読する。</summary>
    public static void Subscribe(EventHandler? handler)
    {
        if (handler is null) return;

        lock (Gate)
        {
            Handlers.Add(new WeakReference<EventHandler>(handler));
        }
    }

    /// <summary>再評価通知の購読を解除する。</summary>
    public static void Unsubscribe(EventHandler? handler)
    {
        if (handler is null) return;

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
    /// 購読者をすべて解除する。プロセス内で複数回アプリを起動する単体テストが、
    /// 前のテストの購読を引きずらないようにするために使う。
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Handlers.Clear();
        }
    }
}
