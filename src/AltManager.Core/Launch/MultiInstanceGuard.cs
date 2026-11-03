namespace AltManager.Core.Launch;

/// <summary>
/// Roblox enforces a single client by opening a named kernel object at startup;
/// if it already exists, the new client hands off to the old one and exits.
/// If WE create and hold that object first, every client sees "already exists
/// but not mine", skips the single-instance path, and runs side by side.
///
/// Must be acquired BEFORE any Roblox client is running, and held for the
/// lifetime of the manager. Mutex ownership is thread-affine, so call
/// <see cref="Acquire"/> from a thread that lives as long as the app (the UI thread).
/// </summary>
public sealed class MultiInstanceGuard : IDisposable
{
    // Historic name: a Mutex. Newer builds have also checked an Event with a
    // sibling name; holding both is harmless and covers either code path.
    private const string MutexName = "ROBLOX_singletonMutex";
    private const string EventName = "ROBLOX_singletonEvent";

    private Mutex? _mutex;
    private EventWaitHandle? _event;

    public bool IsActive { get; private set; }

    /// <returns>
    /// true if we own the singleton objects. false means a Roblox client already
    /// created them — close all Roblox windows and call again.
    /// </returns>
    public bool Acquire()
    {
        if (IsActive) return true;

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // The object already exists. That's fine if nobody owns it — e.g. clients
            // launched by a previous manager session keep handles open after we exit.
            // Only a live owner (a client started without us) is a real conflict.
            bool owned;
            try { owned = _mutex.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; } // previous owner died; we now own it

            if (!owned)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }

        // Best-effort: if the name already exists as a different object type
        // (e.g. a Roblox build that uses it as a mutex), skip it rather than crash.
        try { _event = new EventWaitHandle(false, EventResetMode.ManualReset, EventName, out _); }
        catch (WaitHandleCannotBeOpenedException) { _event = null; }
        catch (UnauthorizedAccessException) { _event = null; }
        IsActive = true;
        return true;
    }

    public void Dispose()
    {
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned on this thread */ }
            _mutex.Dispose();
        }
        _event?.Dispose();
        IsActive = false;
    }
}
