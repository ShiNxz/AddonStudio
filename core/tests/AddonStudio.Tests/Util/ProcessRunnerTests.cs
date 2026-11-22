using System.Collections.Concurrent;
using System.Diagnostics;
using AddonStudio.Core.Util;
using AddonStudio.Tests.Support;

namespace AddonStudio.Tests.Util;

public sealed class ProcessRunnerTests
{
    [UnixFact]
    public async Task EchoWithArgumentList()
    {
        var result = await ProcessRunner.RunAsync("/bin/echo", ["hello", "two words", "שלום"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Equal("hello two words שלום\n", result.Stdout);
        Assert.Equal("", result.Stderr);
        Assert.False(result.TimedOut);
    }

    [UnixFact]
    public async Task RawArgumentString()
    {
        var result = await ProcessRunner.RunAsync("/bin/sh", "-c \"echo raw; exit 3\"");

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("raw\n", result.Stdout);
    }

    [UnixFact]
    public async Task LineCallbacksSplitOnNewlinesAndCarriageReturns()
    {
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();

        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "printf 'one\\ntwo\\r\\nprogress 10%%\\rprogress 50%%\\rdone'; printf 'warn\\n' 1>&2"],
            new ProcessOptions { OnStdout = stdout.Enqueue, OnStderr = stderr.Enqueue });

        Assert.Equal(["one", "two", "progress 10%", "progress 50%", "done"], stdout);
        Assert.Equal(["warn"], stderr);
        Assert.Equal("one\ntwo\r\nprogress 10%\rprogress 50%\rdone", result.Stdout);
        Assert.Equal("warn\n", result.Stderr);
        Assert.Equal("warn", result.ErrorText);
    }

    [UnixFact]
    public async Task WorkingDirectoryEnvironmentAndInput()
    {
        using var temp = new TempDir();

        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "pwd; echo \"$ADDONSTUDIO_TEST\"; echo \"${HOME:-unset}\"; cat"],
            new ProcessOptions
            {
                WorkingDirectory = temp.Path,
                Environment = new Dictionary<string, string?> { ["ADDONSTUDIO_TEST"] = "set", ["HOME"] = null },
                StandardInput = "from stdin\n",
            });

        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(Path.GetFileName(temp.Path), Path.GetFileName(lines[0]));
        Assert.Equal("set", lines[1]);
        Assert.Equal("unset", lines[2]);
        Assert.Equal("from stdin", lines[3]);
    }

    [UnixFact]
    public async Task StdinIsClosedSoNothingWaits()
    {
        var result = await ProcessRunner.RunAsync("/bin/cat", [], new ProcessOptions { Timeout = TimeSpan.FromSeconds(10) });

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
    }

    [UnixFact]
    public async Task TimeoutKillsTheTree()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await ProcessRunner.RunAsync("/bin/sh", ["-c", "sleep 30 & sleep 30; echo never"], new ProcessOptions { Timeout = TimeSpan.FromMilliseconds(300) });

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.DoesNotContain("never", result.Stdout);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [UnixFact]
    public async Task CancellationKillsAndThrows()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var stopwatch = Stopwatch.StartNew();

        var run = ProcessRunner.RunAsync("/bin/sh", ["-c", "echo started; sleep 30"], new ProcessOptions { OnStdout = _ => started.TrySetResult() }, cts.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"took {stopwatch.Elapsed}");
    }

    [UnixFact]
    public async Task AlreadyCancelledNeverStarts()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("/bin/echo", ["x"], null, cts.Token));
    }

    [UnixFact]
    public async Task CaptureIsCappedButCallbacksSeeEverything()
    {
        var lines = 0;

        var result = await ProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "i=0; while [ $i -lt 200 ]; do echo line$i; i=$((i+1)); done"],
            new ProcessOptions { MaxCaptureChars = 100, OnStdout = _ => Interlocked.Increment(ref lines) });

        Assert.Equal(100, result.Stdout.Length);
        Assert.True(result.StdoutTruncated);
        Assert.False(result.StderrTruncated);
        Assert.StartsWith("line0\nline1\n", result.Stdout);
        Assert.Equal(200, lines);
    }

    [Fact]
    public async Task MissingExecutableIsAStartError()
    {
        var error = await Assert.ThrowsAsync<ProcessStartException>(() => ProcessRunner.RunAsync("addonstudio-no-such-tool-" + Guid.NewGuid().ToString("N"), []));

        Assert.StartsWith("addonstudio-no-such-tool-", error.FileName);
    }

    [UnixFact]
    public void FindExecutableSearchesPath()
    {
        Assert.NotNull(ProcessRunner.FindExecutable("sh"));
        Assert.Equal("/bin/sh", ProcessRunner.FindExecutable("/bin/sh"));
        Assert.Null(ProcessRunner.FindExecutable("addonstudio-no-such-tool"));
        Assert.Null(ProcessRunner.FindExecutable(""));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\Program Files\x\", "\"C:\\Program Files\\x\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"a\\""b", "\"a\\\\\\\\\\\"b\"")]
    [InlineData(@"C:\no\spaces", @"C:\no\spaces")]
    public void QuotesWindowsArguments(string argument, string expected)
    {
        Assert.Equal(expected, ProcessRunner.QuoteWindowsArgument(argument));
    }
}
