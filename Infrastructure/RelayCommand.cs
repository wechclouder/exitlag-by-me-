using System.Windows.Input;

namespace wetwiLAG.Infrastructure;

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T>(Action<T?> execute, Predicate<T?>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        canExecute?.Invoke(ConvertParameter(parameter)) ?? true;

    public void Execute(object? parameter) => execute(ConvertParameter(parameter));

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private static T? ConvertParameter(object? parameter) =>
        parameter is T value ? value : default;
}

public sealed class AsyncRelayCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private CancellationTokenSource? _cancellation;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;
    public event Action<Exception>? ExecutionFailed;

    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            _isExecuting = value;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanExecute(object? parameter) => !IsExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        IsExecuting = true;
        _cancellation = new CancellationTokenSource();
        try
        {
            await execute(_cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(exception);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsExecuting = false;
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand<T>(
    Func<T?, CancellationToken, Task> execute,
    Predicate<T?>? canExecute = null) : ICommand
{
    private CancellationTokenSource? _cancellation;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;
    public event Action<Exception>? ExecutionFailed;

    public bool CanExecute(object? parameter) =>
        !_isExecuting && (canExecute?.Invoke(parameter is T value ? value : default) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        _cancellation = new CancellationTokenSource();
        try
        {
            await execute(parameter is T value ? value : default, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(exception);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Cancel() => _cancellation?.Cancel();
    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
