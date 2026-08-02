using System.Runtime.CompilerServices;
using Daqifi.Core.Channel;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Logging.Export;

namespace Daqifi.Core.Cli;

/// <summary>
/// Adapts an <see cref="SdCardLogSession"/> to <see cref="ISampleSource"/> so the core CSV
/// exporter can consume SD card log files. Emits one column per analog channel and one
/// column for the digital port (as the raw uint value).
/// </summary>
internal sealed class SdCardSampleSource : ISampleSource
{
    private const string DeviceName = "Daqifi";
    private const string DigitalChannelName = "DIO";

    private readonly SdCardLogSession _session;
    private readonly List<ChannelDescriptor> _channels;
    private readonly string[] _analogKeys;
    private readonly string _digitalKey;

    public SdCardSampleSource(SdCardLogSession session, int analogPortCount)
    {
        _session = session;
        var serial = session.DeviceConfig?.DeviceSerialNumber ?? "unknown";

        _channels = new List<ChannelDescriptor>(analogPortCount + 1);
        _analogKeys = new string[analogPortCount];
        for (var i = 0; i < analogPortCount; i++)
        {
            var name = $"AI{i}";
            var descriptor = new ChannelDescriptor(DeviceName, serial, name, ChannelType.Analog);
            _channels.Add(descriptor);
            _analogKeys[i] = descriptor.Key;
        }

        var digital = new ChannelDescriptor(DeviceName, serial, DigitalChannelName, ChannelType.Digital);
        _channels.Add(digital);
        _digitalKey = digital.Key;
    }

    public IReadOnlyList<ChannelDescriptor> GetChannels() => _channels;

    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(0);

    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var truncationWarned = false;

        await foreach (var entry in _session.Samples.WithCancellation(cancellationToken))
        {
            var ticks = entry.Timestamp.Ticks;
            if (!truncationWarned && entry.AnalogValues.Count > _analogKeys.Length)
            {
                Console.Error.WriteLine(
                    $"Warning: sample has {entry.AnalogValues.Count} analog values but configured channel count is " +
                    $"{_analogKeys.Length}; extra channels will not appear in the CSV.");
                truncationWarned = true;
            }

            var count = Math.Min(entry.AnalogValues.Count, _analogKeys.Length);
            for (var i = 0; i < count; i++)
            {
                yield return new SampleRow(ticks, _analogKeys[i], entry.AnalogValues[i]);
            }

            yield return new SampleRow(ticks, _digitalKey, entry.DigitalData);
        }
    }
}

/// <summary>
/// Wraps any <see cref="ISampleSource"/> and reports a running count of the CSV rows
/// <see cref="CsvExporter"/> will write, used by the CLI to drive a throughput-aware progress
/// display without changing the underlying source.
/// </summary>
/// <remarks>
/// The exporter collapses every consecutive <see cref="SampleRow"/> that shares a timestamp into a
/// single CSV line, so a row is a timestamp, not a sample. Counting the samples themselves — one
/// per channel per log entry — over-reports by the channel count, which is what made a 95-sample
/// two-channel export claim 190 rows for a 96-line file.
/// </remarks>
internal sealed class RowCountingSampleSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly Action<long> _onRow;

    public RowCountingSampleSource(ISampleSource inner, Action<long> onRow)
    {
        _inner = inner;
        _onRow = onRow;
    }

    public IReadOnlyList<ChannelDescriptor> GetChannels() => _inner.GetChannels();

    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetSampleCountAsync(cancellationToken);

    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long rows = 0;
        long? currentTicks = null;

        await foreach (var sample in _inner.StreamSamples(cancellationToken))
        {
            // Mirrors the exporter's own grouping rule (flush when the timestamp changes), so the
            // count matches the file line for line — including when consecutive entries repeat a
            // timestamp and the exporter merges them into one row.
            if (currentTicks != sample.TimestampTicks)
            {
                currentTicks = sample.TimestampTicks;
                rows++;
                _onRow(rows);
            }

            yield return sample;
        }
    }
}
