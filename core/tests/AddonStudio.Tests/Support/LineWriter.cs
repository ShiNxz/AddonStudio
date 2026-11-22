using System.Text;
using System.Threading.Channels;

namespace AddonStudio.Tests.Support;

/// <summary>A TextWriter that collects complete lines (split on \n) and lets a test wait for them.</summary>
public sealed class LineWriter : TextWriter
{
    private readonly StringBuilder pending = new();
    private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
    private readonly List<string> all = [];
    private readonly object gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public IReadOnlyList<string> All
    {
        get
        {
            lock (gate)
            {
                return all.ToList();
            }
        }
    }

    public override void Write(char value)
    {
        lock (gate)
        {
            if (value == '\n')
            {
                var line = pending.ToString();
                pending.Clear();
                all.Add(line);
                lines.Writer.TryWrite(line);
            }
            else
            {
                pending.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
        {
            return;
        }

        foreach (var character in value)
        {
            Write(character);
        }
    }

    /// <summary>The next line, or throws after the timeout.</summary>
    public async Task<string> NextAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));

        return await lines.Reader.ReadAsync(cts.Token);
    }
}
