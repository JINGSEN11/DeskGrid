using DeskGrid.Core.Services;

namespace DeskGrid.Tests;

public sealed class TaskbarWatchdogProtocolTests
{
    [TestFact]
    public void TryParseArgs_ParsesMinimalSinglePidWatchdogCommand()
    {
        var ok = TaskbarWatchdogProtocol.TryParseArgs(["--watchdog", "4242"], out var opts);
        AssertEx.True(ok, "Minimal --watchdog <pid> invocation must parse.");
        AssertEx.Equal(4242, opts.MainProcessId);
        AssertEx.False(string.IsNullOrWhiteSpace(opts.ConfigDirectory), "Default config directory must be resolved.");
        AssertEx.Equal<string?>(null, opts.CancelEventName);
    }

    [TestFact]
    public void BuildArgs_RoundTripsWithTryParseArgs()
    {
        var args = TaskbarWatchdogProtocol.BuildArgs(9876, @"C:\Users\Test\DeskGrid Config", "Local\\DeskGrid_Cancel_1");
        var ok = TaskbarWatchdogProtocol.TryParseArgs(args.ToArray(), out var opts);
        AssertEx.True(ok);
        AssertEx.Equal(9876, opts.MainProcessId);
        AssertEx.Equal(@"C:\Users\Test\DeskGrid Config", opts.ConfigDirectory);
        AssertEx.Equal<string?>("Local\\DeskGrid_Cancel_1", opts.CancelEventName);
    }

    [TestFact]
    public void TryParseArgs_RejectsNormalLaunchAndInvalidPid()
    {
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs([], out _));
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs(["--minimized"], out _));
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs(["--watchdog"], out _));
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs(["--watchdog", "0"], out _));
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs(["--watchdog", "-5"], out _));
        AssertEx.False(TaskbarWatchdogProtocol.TryParseArgs(["--watchdog", "abc"], out _));
    }

    [TestFact]
    public void SentinelFormatAndParse_HandlesNewAndLegacyFormats()
    {
        var payload = TaskbarWatchdogProtocol.FormatSentinel(new DateTime(2026, 11, 26, 5, 0, 0, DateTimeKind.Utc), 2, 1234);
        AssertEx.True(TaskbarWatchdogProtocol.TryParseSentinelOriginalState(payload, out var state));
        AssertEx.Equal(2, state);

        // Legacy format without PID suffix
        AssertEx.True(TaskbarWatchdogProtocol.TryParseSentinelOriginalState("2026-11-26T05:00:00.0000000Z|1", out var legacyState));
        AssertEx.Equal(1, legacyState);

        // Malformed sentinel returns false with safe default 0
        AssertEx.False(TaskbarWatchdogProtocol.TryParseSentinelOriginalState("corrupt-data", out var fallback));
        AssertEx.Equal(0, fallback);
    }
}
