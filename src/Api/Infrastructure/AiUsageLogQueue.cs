using System.Threading.Channels;
using Ccusage.Api.Data.Entities;

namespace Ccusage.Api.Infrastructure;

/// <summary>
/// A bounded in-memory hand-off between the GeminiService chokepoint and the background writer. The AI
/// path never blocks on the database; if the buffer is full (sustained burst) the newest entries are
/// dropped rather than slowing AI calls or growing memory without bound. Mirrors <see cref="RequestLogQueue"/>.
/// </summary>
public sealed class AiUsageLogQueue
{
    private readonly Channel<AiUsageLog> _channel = Channel.CreateBounded<AiUsageLog>(
        new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
        });

    /// <summary>Non-blocking enqueue; returns false if the buffer is full (entry dropped).</summary>
    public bool TryEnqueue(AiUsageLog entry) => _channel.Writer.TryWrite(entry);

    public ChannelReader<AiUsageLog> Reader => _channel.Reader;
}
