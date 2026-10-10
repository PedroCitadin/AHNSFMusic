using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AHNSF.Music.Desktop.Platform;

/// <summary>
/// Session mutex plus a local named pipe that accepts a single fixed "activate" request.
/// The pipe denies network access and is restricted to the current user; it never changes player state.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string ActivateCommand = "AHNSF-ACTIVATE";
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource stopping = new();
    private Task? server;
    private bool disposed;
    public bool RecoveredAbandoned { get; }
    private SingleInstance(Mutex mutex, string pipeName, bool abandoned) { this.mutex = mutex; this.pipeName = pipeName; RecoveredAbandoned = abandoned; }
    public static string DefaultMutexName => @"Local\AHNSF.Music.Desktop";
    public static string DefaultPipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"AHNSF.Music.Desktop.{System.Diagnostics.Process.GetCurrentProcess().SessionId}.{identity.User?.Value}";
        }
    }
    /// <summary>Returns the owner instance, or null when another live instance owns the mutex. Abandoned mutexes are taken over.</summary>
    public static SingleInstance? TryAcquire(string mutexName, string pipeName, TimeSpan wait = default)
    {
        var mutex = new Mutex(false, mutexName);
        bool acquired, abandoned = false;
        try { acquired = mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { acquired = true; abandoned = true; }
        if (acquired) return new SingleInstance(mutex, pipeName, abandoned);
        mutex.Dispose();
        return null;
    }
    /// <summary>Starts listening for activation requests from later launches. The callback runs on a background thread.</summary>
    public void Listen(Action activate, Action<Exception>? error = null)
    {
        if (server is not null) return;
        server = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = CreateServer();
                    await pipe.WaitForConnectionAsync(stopping.Token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    var buffer = new byte[64]; var read = 0;
                    while (read < buffer.Length)
                    {
                        var count = await pipe.ReadAsync(buffer.AsMemory(read), timeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        read += count;
                    }
                    if (Encoding.ASCII.GetString(buffer, 0, read) == ActivateCommand) activate();
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    error?.Invoke(ex);
                    try { await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                }
            }
        });
    }
    private NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }
    /// <summary>Asks the running instance to restore its window. Returns false when nobody answered.</summary>
    public static bool SignalExisting(string pipeName, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect((int)timeout.TotalMilliseconds);
            // Lets the running instance bring its window to the foreground (the new launch owns the foreground right).
            if (OperatingSystem.IsWindows() && GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid)) AllowSetForegroundWindow(pid);
            var bytes = Encoding.ASCII.GetBytes(ActivateCommand);
            client.Write(bytes, 0, bytes.Length); client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; stopping.Cancel();
        try { server?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose(); stopping.Dispose();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AllowSetForegroundWindow(uint processId);
}
