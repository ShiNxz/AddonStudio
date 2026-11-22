using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AddonStudio.Core.Util;

/// <summary>How to run a process. Everything is optional.</summary>
public sealed record ProcessOptions
{
    /// <summary>Working directory, null = the core's current directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables to set on top of the core's environment. A null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Called for every stdout line (split on \n, \r\n and a lone \r, so progress lines arrive as they change), on a pump thread.</summary>
    public Action<string>? OnStdout { get; init; }

    /// <summary>Same for stderr.</summary>
    public Action<string>? OnStderr { get; init; }

    /// <summary>Kill the process tree after this long and return with TimedOut = true. Null = no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Most chars kept per stream in the result (the start is kept). Line callbacks still see everything.</summary>
    public int MaxCaptureChars { get; init; } = ProcessRunner.DefaultMaxCaptureChars;

    /// <summary>Text written to stdin, which is then closed. Null = stdin is closed right away (nothing can wait on a prompt).</summary>
    public string? StandardInput { get; init; }

    /// <summary>Encoding of stdout/stderr, null = UTF-8.</summary>
    public Encoding? OutputEncoding { get; init; }
}

/// <summary>What a finished process left. ExitCode is the real one even after a kill (then it means little).</summary>
public sealed record ProcessResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool TimedOut,
    bool StdoutTruncated,
    bool StderrTruncated,
    TimeSpan Duration)
{
    /// <summary>Exit code 0 and not timed out.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>Stderr when there is any, else stdout, trimmed: the text to put in an error message.</summary>
    public string ErrorText => (string.IsNullOrWhiteSpace(Stderr) ? Stdout : Stderr).Trim();
}

/// <summary>The executable could not be started (not found, not executable).</summary>
public sealed class ProcessStartException : Exception
{
    public string FileName { get; }

    public ProcessStartException(string fileName, Exception innerException)
        : base($"Could not start \"{fileName}\": {innerException.Message}", innerException)
    {
        FileName = fileName;
    }
}

/// <summary>
/// Runs a child process the Windows-correct way: no shell, CreateNoWindow, UTF-8 pipes, stdin closed, line callbacks,
/// timeout, and cancellation that kills the whole process tree. On Windows every child also joins a kill-on-close job
/// object, so compilers and git die with the core even if it is killed.
/// </summary>
public static class ProcessRunner
{
    public const int DefaultMaxCaptureChars = 4 * 1024 * 1024;

    /// <summary>A single line longer than this is passed on in pieces.</summary>
    public const int MaxLineChars = 64 * 1024;

    /// <summary>How long to wait for the pipes to close after the process exited (a grandchild can hold them open).</summary>
    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Runs with an argument list, each element quoted as needed by .NET (safe for any characters).</summary>
    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessOptions? options = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName);

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return RunAsync(startInfo, options ?? new ProcessOptions(), ct);
    }

    /// <summary>Runs with a raw command line string, passed as-is (for user templates like compilerArgs).</summary>
    public static Task<ProcessResult> RunAsync(string fileName, string arguments, ProcessOptions? options = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName) { Arguments = arguments };

        return RunAsync(startInfo, options ?? new ProcessOptions(), ct);
    }

    private static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, ProcessOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var encoding = options.OutputEncoding ?? Utf8NoBom;

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = encoding;
        startInfo.StandardErrorEncoding = encoding;
        startInfo.StandardInputEncoding = Utf8NoBom;

        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.Environment != null)
        {
            foreach (var (key, value) in options.Environment)
            {
                if (value == null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new ProcessStartException(startInfo.FileName, new InvalidOperationException("The process did not start."));
            }
        }
        catch (Win32Exception exception)
        {
            throw new ProcessStartException(startInfo.FileName, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ProcessStartException(startInfo.FileName, exception);
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsJob.TryAssign(process);
        }

        var stdout = new StreamCapture(options.MaxCaptureChars, options.OnStdout);
        var stderr = new StreamCapture(options.MaxCaptureChars, options.OnStderr);
        var stdoutPump = stdout.PumpAsync(process.StandardOutput);
        var stderrPump = stderr.PumpAsync(process.StandardError);

        try
        {
            if (options.StandardInput != null)
            {
                await process.StandardInput.WriteAsync(options.StandardInput.AsMemory(), ct).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // the process exited before reading its input, or we are cancelling: the wait below sorts it out
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        using var timeout = options.Timeout is { } limit ? new CancellationTokenSource(limit) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            timedOut = !ct.IsCancellationRequested && timeout.IsCancellationRequested;

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // a process that will not die: give up on it, the job object still gets it when the core exits
            }
        }

        var pumps = Task.WhenAll(stdoutPump, stderrPump);
        await Task.WhenAny(pumps, Task.Delay(PipeDrainGrace, CancellationToken.None)).ConfigureAwait(false);

        stdout.Finish();
        stderr.Finish();
        stopwatch.Stop();

        if (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("The process was cancelled.", ct);
        }

        int exitCode;

        try
        {
            exitCode = process.HasExited ? process.ExitCode : -1;
        }
        catch (InvalidOperationException)
        {
            exitCode = -1;
        }

        return new ProcessResult(exitCode, stdout.Text, stderr.Text, timedOut, stdout.Truncated, stderr.Truncated, stopwatch.Elapsed);
    }

    /// <summary>Kills the process and everything it started. Never throws.</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            try
            {
                process.Kill();
            }
            catch (Exception inner) when (inner is InvalidOperationException or Win32Exception or NotSupportedException)
            {
            }
        }
    }

    /// <summary>
    /// The full path of an executable found the way a shell would: a path with a folder is checked as-is, a bare name
    /// is looked up in PATH (with PATHEXT on Windows when it has no extension). Null when not found.
    /// </summary>
    public static string? FindExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var windows = OperatingSystem.IsWindows();
        var extensions = windows && !Path.HasExtension(name)
            ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        if (name.Contains('/') || (windows && name.Contains('\\')))
        {
            return extensions.Select(extension => name + extension).Where(File.Exists).Select(Path.GetFullPath).FirstOrDefault();
        }

        var pathVariable = System.Environment.GetEnvironmentVariable("PATH") ?? "";

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var folder = directory.Trim('"');

            foreach (var extension in extensions)
            {
                string candidate;

                try
                {
                    candidate = Path.Combine(folder, name + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Quotes one argument for a Windows command line (CommandLineToArgvW rules: backslashes before a quote are
    /// doubled). For building raw argument strings; RunAsync with a list does this by itself.
    /// </summary>
    public static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }

            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');

        return builder.ToString();
    }

    /// <summary>Reads one pipe: keeps the start of it (capped) and splits it into lines for the callback.</summary>
    private sealed class StreamCapture(int maxChars, Action<string>? onLine)
    {
        private readonly StringBuilder captured = new();
        private readonly StringBuilder line = new();
        private readonly object gate = new();
        private bool lastWasCarriageReturn;
        private bool finished;

        public bool Truncated { get; private set; }

        public string Text
        {
            get
            {
                lock (gate)
                {
                    return captured.ToString();
                }
            }
        }

        public async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[8192];

            try
            {
                while (true)
                {
                    var read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    Append(buffer.AsSpan(0, read));
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // the pipe went away (process disposed after the drain grace)
            }
        }

        private void Append(ReadOnlySpan<char> chunk)
        {
            lock (gate)
            {
                if (finished)
                {
                    return;
                }

                if (!Truncated)
                {
                    var room = Math.Max(0, maxChars - captured.Length);

                    if (chunk.Length <= room)
                    {
                        captured.Append(chunk);
                    }
                    else
                    {
                        captured.Append(chunk[..room]);
                        Truncated = true;
                    }
                }

                if (onLine == null)
                {
                    return;
                }

                foreach (var character in chunk)
                {
                    switch (character)
                    {
                        case '\n' when lastWasCarriageReturn:
                            lastWasCarriageReturn = false;
                            break;
                        case '\n':
                            EmitLine();
                            break;
                        case '\r':
                            EmitLine();
                            lastWasCarriageReturn = true;
                            break;
                        default:
                            lastWasCarriageReturn = false;
                            line.Append(character);

                            if (line.Length >= MaxLineChars)
                            {
                                EmitLine();
                            }

                            break;
                    }
                }
            }
        }

        /// <summary>Passes on a last line without a newline. Nothing arrives after this.</summary>
        public void Finish()
        {
            lock (gate)
            {
                if (finished)
                {
                    return;
                }

                if (line.Length > 0)
                {
                    EmitLine();
                }

                finished = true;
            }
        }

        private void EmitLine()
        {
            var text = line.ToString();
            line.Clear();

            try
            {
                onLine?.Invoke(text);
            }
            catch
            {
                // a callback never breaks the pump
            }
        }
    }

    /// <summary>A kill-on-close job object every child joins, so children die with the core.</summary>
    private static class WindowsJob
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private static readonly Lazy<IntPtr> Handle = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

        public static void TryAssign(Process process)
        {
            try
            {
                var job = Handle.Value;

                if (job != IntPtr.Zero && !process.HasExited)
                {
                    AssignProcessToJobObject(job, process.Handle);
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
            {
                // without the job a killed core can leave children behind, everything else still works
            }
        }

        private static IntPtr Create()
        {
            try
            {
                var job = CreateJobObjectW(IntPtr.Zero, null);

                if (job == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var info = new JobObjectExtendedLimitInformationStruct();
                info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>()))
                {
                    CloseHandle(job);
                    return IntPtr.Zero;
                }

                // never closed: the handle goes away with the core, and the job kills what is left
                return job;
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
            {
                return IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationStruct
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformationStruct info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
