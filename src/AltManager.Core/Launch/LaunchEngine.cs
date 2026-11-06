using System.Diagnostics;
using AltManager.Core.Models;
using AltManager.Core.Roblox;
using AltManager.Core.Vault;

namespace AltManager.Core.Launch;

public enum LaunchStage { Queued, FetchingTicket, Starting, Running, Failed, Skipped, Cancelled }

public sealed record LaunchProgress(Account Account, LaunchStage Stage, string? Message = null, int? Pid = null);

public sealed class LaunchOptions
{
    /// <summary>Delay between the start of consecutive launches in a batch.</summary>
    public TimeSpan StaggerDelay { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long to wait for RobloxPlayerBeta.exe to appear after invoking the protocol.</summary>
    public TimeSpan ProcessDetectTimeout { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Skip accounts that already have a tracked client running.</summary>
    public bool SkipAlreadyRunning { get; init; } = true;
    /// <summary>Raise the launched client's CPU priority and prefer the high-performance GPU.</summary>
    public bool OptimizeClient { get; init; }
}

/// <summary>
/// Orchestrates: vault cookie → auth ticket → roblox-player: URI → shell launch → PID claim.
/// A single semaphore serializes the "invoke protocol + claim PID" critical section, which
/// is what keeps PID-to-account attribution correct even if two batches overlap.
/// </summary>
public sealed class LaunchEngine
{
    private readonly AccountVault _vault;
    private readonly RobloxApiClient _api;
    private readonly ProcessTracker _tracker;
    private readonly MultiInstanceGuard _guard;
    private readonly SemaphoreSlim _launchLock = new(1, 1);

    public LaunchEngine(AccountVault vault, RobloxApiClient api, ProcessTracker tracker, MultiInstanceGuard guard)
    {
        _vault = vault;
        _api = api;
        _tracker = tracker;
        _guard = guard;
    }

    public async Task<TrackedProcess?> LaunchAsync(
        Account account, LaunchTarget target, LaunchOptions? options = null,
        IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new LaunchOptions();

        if (!_guard.IsActive && _tracker.Active.Count > 0)
            progress?.Report(new(account, LaunchStage.Starting,
                "Multi-instance guard is not active; this launch may replace the running client."));

        if (options.SkipAlreadyRunning && _tracker.IsRunning(account.Id))
        {
            progress?.Report(new(account, LaunchStage.Skipped, "Already running."));
            return null;
        }

        string ticket;
        try
        {
            progress?.Report(new(account, LaunchStage.FetchingTicket));
            ticket = await _api.GetAuthenticationTicketAsync(_vault.GetCookie(account), ct);
        }
        catch (UnauthorizedAccessException)
        {
            account.Status = AccountStatus.Expired;
            progress?.Report(new(account, LaunchStage.Failed, "Cookie expired — re-login required."));
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report(new(account, LaunchStage.Failed, ex.Message));
            return null;
        }

        await _launchLock.WaitAsync(ct);
        try
        {
            var before = ProcessTracker.Snapshot();
            var uri = ProtocolUriBuilder.Build(ticket, target, ProtocolUriBuilder.NewBrowserTrackerId());

            progress?.Report(new(account, LaunchStage.Starting));
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();

            var tp = await _tracker.ClaimNewProcessAsync(before, account.Id, account.Label, options.ProcessDetectTimeout, ct);
            if (tp is null)
            {
                progress?.Report(new(account, LaunchStage.Failed,
                    "Client process did not appear (is Roblox installed / was the launcher updating?)."));
                return null;
            }

            if (options.OptimizeClient) ClientOptimizer.Apply(tp.Pid);

            progress?.Report(new(account, LaunchStage.Running, Pid: tp.Pid));
            return tp;
        }
        finally
        {
            _launchLock.Release();
        }
    }

    /// <summary>Launches accounts one after another with a fixed stagger between start times.</summary>
    public Task LaunchBatchAsync(
        IReadOnlyList<Account> accounts, LaunchTarget target, LaunchOptions? options = null,
        IProgress<LaunchProgress>? progress = null, CancellationToken ct = default) =>
        LaunchBatchAsync(accounts, _ => target, options, progress, ct);

    /// <summary>Same as above, but each account can go to its own game.</summary>
    public async Task LaunchBatchAsync(
        IReadOnlyList<Account> accounts, Func<Account, LaunchTarget> targetFor, LaunchOptions? options = null,
        IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new LaunchOptions();
        foreach (var a in accounts) progress?.Report(new(a, LaunchStage.Queued));

        for (var i = 0; i < accounts.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                foreach (var rest in accounts.Skip(i)) progress?.Report(new(rest, LaunchStage.Cancelled));
                return;
            }

            var started = Stopwatch.StartNew();
            try
            {
                await LaunchAsync(accounts[i], targetFor(accounts[i]), options, progress, ct);
            }
            catch (OperationCanceledException)
            {
                foreach (var rest in accounts.Skip(i)) progress?.Report(new(rest, LaunchStage.Cancelled));
                return;
            }

            // Stagger is measured start-to-start, so slow ticket fetches don't stack extra delay.
            var remaining = options.StaggerDelay - started.Elapsed;
            if (i < accounts.Count - 1 && remaining > TimeSpan.Zero)
            {
                try { await Task.Delay(remaining, ct); }
                catch (OperationCanceledException) { /* handled at loop top */ }
            }
        }
    }
}
