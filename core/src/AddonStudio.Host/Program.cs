using System.Text;
using AddonStudio.Core.Util;
using AddonStudio.Host;
using AddonStudio.Host.Cli;
using AddonStudio.Host.Rpc;

// stdout carries only protocol messages (rpc) or the JSON result (CLI). Pipes are UTF-8 without BOM whatever the
// console code page is, and anything else that writes to Console.Out lands on stderr.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };
Console.SetError(stderr);
Console.SetOut(stderr);
Log.Writer = stderr;

AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    Log.Warn($"Unobserved task exception: {e.Exception.GetBaseException().Message}");
    e.SetObserved();
};

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

if (args.Length > 0 && args[0] == "rpc")
{
    using var services = Services.CreateDefault();
    using var server = new RpcServer(services, stdout);
    using var stdin = new StreamReader(Console.OpenStandardInput(), utf8, detectEncodingFromByteOrderMarks: false);

    Log.Info($"addonstudio-core {HostInfo.Version} rpc on {Platform.Os}");
    await server.RunAsync(stdin, cancel.Token);
    stdout.Flush();

    return 0;
}

if (args.Length == 0 || args[0] is "help" or "--help" or "-h" or "/?")
{
    stderr.WriteLine(CliRunner.Usage);
    return args.Length == 0 ? CliRunner.ExitUsage : CliRunner.ExitOk;
}

using (var services = Services.CreateDefault())
{
    var code = await CliRunner.RunAsync(args, services, stdout, stderr, cancel.Token);
    stdout.Flush();

    return code;
}
