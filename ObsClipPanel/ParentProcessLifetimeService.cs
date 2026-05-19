using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace ObsClipPanel;

public sealed class ParentProcessLifetimeService : BackgroundService
{
    private const string ParentPidEnv = "CELESTE_REPLAY_PARENT_PID";
    private const string ParentStartTicksEnv = "CELESTE_REPLAY_PARENT_START_TICKS";

    private readonly IHostApplicationLifetime applicationLifetime;

    public ParentProcessLifetimeService(IHostApplicationLifetime applicationLifetime)
    {
        this.applicationLifetime = applicationLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var parent = TryResolveParentProcess();
        if (parent is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsParentAlive(parent.Value))
            {
                applicationLifetime.StopApplication();
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public static bool ShouldTerminateBeforeStartup()
    {
        var parent = TryResolveParentProcess();
        return parent is not null && !IsParentAlive(parent.Value);
    }

    private static ParentProcessIdentity? TryResolveParentProcess()
    {
        var pidText = Environment.GetEnvironmentVariable(ParentPidEnv);
        if (!int.TryParse(pidText, out var pid) || pid <= 0)
        {
            return null;
        }

        long? expectedStartTicks = null;
        var startTicksText = Environment.GetEnvironmentVariable(ParentStartTicksEnv);
        if (long.TryParse(startTicksText, out var parsedTicks) && parsedTicks > 0)
        {
            expectedStartTicks = parsedTicks;
        }

        return new ParentProcessIdentity(pid, expectedStartTicks);
    }

    private static bool IsParentAlive(ParentProcessIdentity parent)
    {
        try
        {
            using var process = Process.GetProcessById(parent.Pid);
            if (process.HasExited)
            {
                return false;
            }

            if (parent.ExpectedStartTicks is { } expectedTicks)
            {
                long actualTicks;
                try
                {
                    actualTicks = process.StartTime.ToUniversalTime().Ticks;
                }
                catch
                {
                    return false;
                }

                if (actualTicks != expectedTicks)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private readonly record struct ParentProcessIdentity(int Pid, long? ExpectedStartTicks);
}
