using CommunityToolkit.Mvvm.Input;

namespace ImmichSplash.App.ViewModels;

/// <summary>An in-window confirmation dialog, optionally with a third choice (e.g. "Don't ask again").</summary>
public sealed partial class DialogViewModel(string title, string message, string confirmText, string cancelText, bool isDanger, string? otherText = null) : ViewModelBase
{
    /// <summary>1 = confirm, 2 = the other choice, 0 = cancel.</summary>
    readonly TaskCompletionSource<int> _choice = new();

    public string Title { get; } = title;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public string CancelText { get; } = cancelText;
    public bool IsDanger { get; } = isDanger;
    public string? OtherText { get; } = otherText;
    public bool HasOther => OtherText is not null;

    public Task<bool> Result => _choice.Task.ContinueWith(t => t.Result == 1, TaskScheduler.Default);

    /// <summary>True: confirmed. False: the other choice. Null: cancelled.</summary>
    public Task<bool?> Choice => _choice.Task.ContinueWith(t => t.Result switch { 1 => true, 2 => (bool?)false, _ => null }, TaskScheduler.Default);

    [RelayCommand]
    void Confirm() => _choice.TrySetResult(1);

    [RelayCommand]
    void Other() => _choice.TrySetResult(2);

    [RelayCommand]
    void Cancel() => _choice.TrySetResult(0);
}
