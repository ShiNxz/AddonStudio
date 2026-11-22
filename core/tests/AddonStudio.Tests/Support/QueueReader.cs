using System.Threading.Channels;

namespace AddonStudio.Tests.Support;

/// <summary>A TextReader fed line by line from the test; Complete() is end of input.</summary>
public sealed class QueueReader : TextReader
{
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

    public void Send(string line) => lines.Writer.TryWrite(line);

    public void Complete() => lines.Writer.TryComplete();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await lines.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
}
