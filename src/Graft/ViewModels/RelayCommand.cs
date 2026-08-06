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
/// falseを返す。例外は握り潰さず（附録A.4）、awaitの外へそのまま伝播させる。
/// ICommand.Executeはvoidを返すため内部的には async void になるが、これは
/// SynchronizationContext（UIスレッドのDispatcher）へ例外を伝播させ、
/// 未処理例外として最上位で捕捉できるようにするための意図的な選択である。
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
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
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CommandRequery.Invalidate();
}
