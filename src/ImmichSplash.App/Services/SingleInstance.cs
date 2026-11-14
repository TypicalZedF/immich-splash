using System.IO.Pipes;
using ImmichSplash.Core;

namespace ImmichSplash.App.Services;

/// <summary>
/// Ensures one instance per user. Other launches send the running instance a command over a named pipe:
/// show the window, deliver an SSO callback link, or quit (used by the installer before updating/uninstalling).
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public enum Command
    {
        Activate,
        OpenLink,
        Quit,
        /// <summary>Files or folders to upload (from the file manager), separated by <see cref="PathSeparator"/>.</summary>
        Upload,
        /// <summary>A jump list task, e.g. "sync".</summary>
        Run,
    }

    /// <summary>Between paths in an Upload command (a character paths don't contain).</summary>
    public const char PathSeparator = '|';

    static string Suffix => $"{AppInfo.Id}-{Environment.UserName}";
    // On Linux, .NET scopes "Local\" names to the login session, and an app started by a browser opening a
    // link can be in a different session than the one in the tray, so the name is per user, machine-wide.
    static string MutexName => OperatingSystem.IsWindows() ? $@"Local\{Suffix}" : $@"Global\{Suffix}";
    static string PipeName => $"{Suffix}-commands";

    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Raised on a background thread for each command received.</summary>
    public event Action<Command, string?>? CommandReceived;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (createdNew)
            return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    public static bool Send(Command command, string? argument = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(argument is null ? command.ToString() : $"{command} {argument}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not send {command} to the running instance", ex);
            return false;
        }
    }

    /// <summary>Whether another copy is listening for commands (it then gets an empty line, which it ignores).</summary>
    public static bool IsAnotherListening()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(300);
            return true;
        }
        catch
        {
            return false; // nobody there
        }
    }

    /// <summary>Asks a running instance to quit and waits until it has exited.</summary>
    public static void RequestQuit(TimeSpan timeout)
    {
        if (!Mutex.TryOpenExisting(MutexName, out var existing))
            return; // not running
        using (existing)
        {
            if (!Send(Command.Quit))
                return;
            try
            {
                // The running instance releases the mutex as it exits.
                if (existing.WaitOne(timeout))
                    existing.ReleaseMutex();
            }
            catch (AbandonedMutexException)
            {
                // It exited without releasing; that's fine.
            }
        }
    }

    public void StartListening()
    {
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split(' ', 2);
                    if (Enum.TryParse<Command>(parts[0], out var command))
                        CommandReceived?.Invoke(command, parts.Length > 1 ? parts[1] : null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Command pipe error", ex);
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { /* not owned on this thread */ }
        _mutex.Dispose();
    }
}
