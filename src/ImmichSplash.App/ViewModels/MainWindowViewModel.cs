using CommunityToolkit.Mvvm.ComponentModel;
using ImmichSplash.App.Services;
using ImmichSplash.Core.Settings;

namespace ImmichSplash.App.ViewModels;

/// <summary>Root of the window: onboarding until set up, the lock screen when locked, otherwise the main shell.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    ShellPage _pendingPage = ShellPage.Photos;

    public MainWindowViewModel()
    {
        var setUp = AppServices.Account.IsSignedIn && AppServices.Settings.Current.IsConfigured;
        if (!setUp)
        {
            // Signed out with folders already chosen (e.g. the server dropped the sign-in): straight to signing in
            // to the server it knows, not the whole setup again.
            var s = AppServices.Settings.Current;
            Content = new OnboardingViewModel(this, resumeSignIn: s.Folders.Count > 0 && !string.IsNullOrEmpty(s.WebUrl));
        }
        else if (IsLockEnabled)
            Content = new LockViewModel(this, Unlock);
        else
        {
            Content = new ShellViewModel(this);
            AskUpdatePreferences();
        }
    }

    [ObservableProperty]
    public partial ViewModelBase Content { get; set; }

    [ObservableProperty]
    public partial DialogViewModel? Dialog { get; set; }

    /// <summary>A larger in-window dialog, such as choosing a profile picture.</summary>
    [ObservableProperty]
    public partial ViewModelBase? Overlay { get; set; }

    /// <summary>Files are being dragged over the window (shows "Drop to upload").</summary>
    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    /// <summary>Uploads can only start from the main app (signed in, not locked or setting up).</summary>
    public bool CanAcceptDrop => Content is ShellViewModel && Overlay is null && Dialog is null;

    /// <summary>Photos, videos or folders dropped on the window: offer to upload them.</summary>
    public void OfferUpload(IEnumerable<string> paths)
    {
        if (!CanAcceptDrop)
            return;
        // Dropped onto an open album: it goes into that album (shown in the panel, and you can change it).
        var photos = (Content as ShellViewModel)?.CurrentPage as PhotosViewModel;
        var album = photos?.Drill?.Album;
        ShowOverlay(new DropUploadViewModel(this, Core.Sync.DroppedFilesUploader.CollectMedia(paths, Core.Sync.MediaTypes.Default), album,
            album is null ? null : () => photos!.RefreshCommand.Execute(null)));
    }

    public void ShowOverlay(ViewModelBase overlay)
    {
        CloseOverlay(Overlay);
        Overlay = overlay;
    }

    public void CloseOverlay(ViewModelBase? overlay)
    {
        if (overlay is null || !ReferenceEquals(Overlay, overlay))
            return;
        Overlay = null;
        (overlay as IDisposable)?.Dispose();
    }

    /// <summary>Provided by the window: shows a native folder picker and returns the chosen path.</summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>Provided by the window: shows a native picker for an image file.</summary>
    public Func<string, Task<string?>>? PickImage { get; set; }

    public Task<string?> PickImageAsync(string title) => PickImage?.Invoke(title) ?? Task.FromResult<string?>(null);

    /// <summary>Provided by the window: a native "Save as" dialog (title, suggested file name).</summary>
    public Func<string, string, Task<string?>>? PickSaveFile { get; set; }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName) =>
        PickSaveFile?.Invoke(title, suggestedName) ?? Task.FromResult<string?>(null);

    /// <summary>Provided by the window: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

    public Task CopyTextAsync(string text) => CopyText?.Invoke(text) ?? Task.CompletedTask;

    /// <summary>Provided by the window: its native handle (for the Windows Hello prompt).</summary>
    public Func<IntPtr>? GetWindowHandle { get; set; }

    public static bool IsLockEnabled => AppServices.Settings.Current.AppLock != AppLockMode.Off;

    public bool IsLocked => Content is LockViewModel;

    public Task<string?> PickFolderAsync(string title) => PickFolder?.Invoke(title) ?? Task.FromResult<string?>(null);

    public void ShowShell(ShellPage page = ShellPage.Photos)
    {
        Replace(new ShellViewModel(this));
        Navigate(page);
        AskUpdatePreferences();
    }

    /// <summary>Once, after first-time setup: how should the app update itself?</summary>
    void AskUpdatePreferences()
    {
        if (AppServices.Settings.Current.UpdatePreferencesPending && AppServices.Updates.IsEnabled)
            ShowOverlay(new UpdatePreferencesViewModel(this));
    }

    /// <summary>Back to setup. With <paramref name="resumeSignIn"/>, jumps straight to signing in to the known server.</summary>
    public void ShowOnboarding(bool resumeSignIn) => Replace(new OnboardingViewModel(this, resumeSignIn));

    /// <summary>
    /// "Sign in again" after the server stopped accepting the sign-in: let go of the rejected one first (folders,
    /// settings and the app lock stay), then sign in to the same server, the same way as a fresh sign-in.
    /// </summary>
    public async Task SignInAgainAsync()
    {
        if (AppServices.Account.IsSignedIn)
            await AppServices.Account.SignOutAsync(keepAppLock: true);
        ShowOnboarding(resumeSignIn: true);
    }

    public void Navigate(ShellPage page)
    {
        if (Content is ShellViewModel shell)
            shell.Navigate(page);
        else
            _pendingPage = page; // shown after unlocking
    }

    /// <summary>Locks the window now (if an app lock is set up).</summary>
    public void Lock()
    {
        if (!IsLockEnabled || IsLocked || Content is OnboardingViewModel)
            return;
        Dialog?.CancelCommand.Execute(null);
        CloseOverlay(Overlay);
        _pendingPage = (Content as ShellViewModel)?.SelectedNav.Page ?? ShellPage.Photos;
        Replace(new LockViewModel(this, Unlock));
    }

    void Unlock() => ShowShell(_pendingPage);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, string? cancelText = null, bool danger = false)
    {
        var dialog = new DialogViewModel(title, message, confirmText, cancelText ?? Tr.T("Cancel"), danger);
        Dialog = dialog;
        try
        {
            return await dialog.Result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    void Replace(ViewModelBase content)
    {
        CloseOverlay(Overlay);
        var old = Content;
        Content = content;
        (old as IDisposable)?.Dispose();
        OnPropertyChanged(nameof(IsLocked));
    }

    public void Dispose()
    {
        Dialog?.CancelCommand.Execute(null);
        CloseOverlay(Overlay);
        (Content as IDisposable)?.Dispose();
    }
}
