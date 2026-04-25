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
        await foreach (var entry in _session.Samples.WithCancellation(cancellationToken))
        {
            var ticks = entry.Timestamp.Ticks;
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
/// Wraps any <see cref="ISampleSource"/> and invokes a callback after each sample, used by the
/// CLI to drive a throughput-aware progress display without changing the underlying source.
/// </summary>
internal sealed class CountingSampleSource : ISampleSource
{
    private readonly ISampleSource _inner;
    private readonly Action<long> _onSample;

    public CountingSampleSource(ISampleSource inner, Action<long> onSample)
    {
        _inner = inner;
        _onSample = onSample;
    }

    public IReadOnlyList<ChannelDescriptor> GetChannels() => _inner.GetChannels();

    public ValueTask<int> GetSampleCountAsync(CancellationToken cancellationToken = default)
        => _inner.GetSampleCountAsync(cancellationToken);

    public async IAsyncEnumerable<SampleRow> StreamSamples(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long count = 0;
        await foreach (var sample in _inner.StreamSamples(cancellationToken))
        {
            count++;
            _onSample(count);
            yield return sample;
        }
    }
}
