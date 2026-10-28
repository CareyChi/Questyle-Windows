using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Questyle.Desktop.Services;

public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle activation;
    private readonly RegisteredWaitHandle? listener;
    public bool IsPrimary { get; }

    public SingleInstance(string name, Action restore)
    {
        // Create the event first so a launch during startup leaves a pending signal.
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Activate");
        mutex = new Mutex(true, @"Local\" + name + ".Instance", out var created);
        IsPrimary = created;
        if (IsPrimary)
            listener = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => restore(), null, Timeout.Infinite, false);
    }

    public void ActivateExisting()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
                if (process.Id != current.Id && process.SessionId == current.SessionId)
                    AllowSetForegroundWindow((uint)process.Id);
        }
        activation.Set();
    }

    public void Dispose()
    {
        listener?.Unregister(null);
        activation.Dispose();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
