namespace Poseidon.Desktop.Services;

/// <summary>
/// Abstracts WPF Dispatcher for unit-testability and thread marshaling.
/// </summary>
public interface IDispatcherService
{
    /// <summary>Invoke an action on the UI thread.</summary>
    void Invoke(Action action);

    /// <summary>Invoke an action on the UI thread asynchronously.</summary>
    Task InvokeAsync(Action action);

    /// <summary>Invoke a function on the UI thread asynchronously.</summary>
    Task<T> InvokeAsync<T>(Func<T> func);
}

