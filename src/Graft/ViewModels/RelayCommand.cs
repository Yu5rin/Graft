using System.Windows.Input;

namespace Graft.ViewModels;

/// <summary>
/// パラメータなしの同期コマンド。MVVMフレームワークを使わない方針（附録A.3）のため
/// 自前実装する。再評価の通知は<see cref="CommandRequery"/>越しに受け取り、
/// フォーカス移動やキー入力のたびにCanExecuteが再評価されるようにする。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Subscribe(value);
        remove => CommandRequery.Unsubscribe(value);
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>自動再評価だけでは不十分な場合に明示的に呼び出す。</summary>
    public void RaiseCanExecuteChanged() => CommandRequery.Invalidate();
}

/// <summary>パラメータ付きの同期コマンド。</summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Subscribe(value);
        remove => CommandRequery.Unsubscribe(value);
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(ConvertParameter(parameter)) ?? true;

    public void Execute(object? parameter) => _execute(ConvertParameter(parameter));

    public void RaiseCanExecuteChanged() => CommandRequery.Invalidate();

    private static T? ConvertParameter(object? parameter)
    {
        if (parameter is null)
        {
            return default;
        }

        return (T)parameter;
    }
}

/// <summary>
/// 非同期処理を実行するコマンド。実行中は多重起動を防ぐため <see cref="CanExecute"/> が
/// falseを返す。
///
/// 【設計変更の経緯（実機クラッシュの修正）】
/// 旧実装は「例外は握り潰さず、awaitの外（ICommand.Executeが内部的に持つasync void）へ
/// そのまま伝播させ、SynchronizationContext経由でAppDomain.UnhandledExceptionへ渡す」
/// という意図的な設計だった。しかし<c>AppDomain.UnhandledException</c>は「これから
/// プロセスが終了する」ことの通知に過ぎず、ハンドラ内で記録はできてもプロセスの終了
/// そのものは止められない。実機（Windows）で「バージョン情報→最新のログを表示」時に
/// <see cref="Infra.LogTailReader.ReadTail(string, int)"/>が投げた<c>IOException</c>が
/// このパターンで最上位まで突き抜け、アプリ全体が落ちる不具合として顕在化した。
/// これはこのコマンド固有の問題ではなく、<see cref="AsyncRelayCommand"/>を使うすべての
/// コマンド（48箇所）に共通する構造的な危険だったため、個別の呼び出し側で都度catchするの
/// ではなく、ここで一括して塞ぐ。
///
/// 【対処】<see cref="SafeHandler.RunAsync(string, Func{Task})"/>と同じ作法（附録A.4）に
/// 統一する。想定外の例外は<see cref="SafeHandler.OnUnexpected"/>へ委ね、ログ記録＋
/// 日本語の通知だけを行ってアプリの継続を優先する。<c>OperationCanceledException</c>は
/// 異常ではないため通知しない（<see cref="SafeHandler.RunAsync(string, Func{Task})"/>と
/// 同じ扱い）。<see cref="SafeHandler.OnUnexpected"/>が未配線の場合（起動のごく初期等）は
/// 従来どおり再スローし、握り潰さない。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly string _context;
    private bool _isExecuting;

    /// <param name="execute">実行する非同期処理。</param>
    /// <param name="canExecute">実行可能かどうか。省略時は常に実行可能。</param>
    /// <param name="context">
    /// 想定外の例外が発生した際に通知文へ載せる、日本語の短い操作名（例:「最新のログの表示」）。
    /// 省略時は汎用的な文言を使う。
    /// </param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, string? context = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _context = string.IsNullOrWhiteSpace(context) ? "コマンドの実行" : context;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandRequery.Subscribe(value);
        remove => CommandRequery.Unsubscribe(value);
    }

    /// <summary>非同期処理を実行中かどうか。</summary>
    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try
        {
            await SafeHandler.RunAsync(_context, _execute).ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CommandRequery.Invalidate();
}
