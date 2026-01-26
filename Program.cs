using System.Globalization;
using System.Net;
using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.Protocol;

namespace Daqifi.Core.Cli;

internal class Program
{
    private const int DefaultPort = 9760;
    private const int DefaultBaudRate = 115200;
    private const int DefaultRate = 100;
    private const int DefaultDurationSeconds = 10;
    private const int DefaultConnectTimeoutSeconds = 5;

    private static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (options.Errors.Count > 0)
        {
            foreach (var error in options.Errors)
            {
                Console.Error.WriteLine(error);
            }

            Console.Error.WriteLine("Use --help to see available options.");
            return 1;
        }

        if (options.Discover)
        {
            await DiscoverAsync(options.DiscoveryTimeoutSeconds);
        }

        if (options.DiscoverSerial)
        {
            DiscoverSerialPorts();
        }

        // Check if we have a connection target (IP or serial)
        var hasIpTarget = !string.IsNullOrWhiteSpace(options.IpAddress);
        var hasSerialTarget = !string.IsNullOrWhiteSpace(options.SerialPort);

        if (!hasIpTarget && !hasSerialTarget)
        {
            if (options.Discover || options.DiscoverSerial)
            {
                return 0;
            }

            Console.Error.WriteLine("Missing required option: --ip or --serial");
            Console.Error.WriteLine("Use --help to see available options.");
            return 1;
        }

        if (hasIpTarget && hasSerialTarget)
        {
            Console.Error.WriteLine("Cannot specify both --ip and --serial. Use one or the other.");
            return 1;
        }

        if (hasIpTarget)
        {
            var ipAddress = options.IpAddress!.Trim();
            if (!IPAddress.TryParse(ipAddress, out _))
            {
                Console.Error.WriteLine($"Invalid IP address: {ipAddress}");
                return 1;
            }
        }

        return await RunStreamingSessionAsync(options);
    }

    private static async Task<int> RunStreamingSessionAsync(CliOptions options)
    {
        // Build connection options from CLI parameters
        var connectionOptions = new DeviceConnectionOptions
        {
            ConnectionRetry = new ConnectionRetryOptions
            {
                Enabled = options.ConnectAttempts > 1,
                MaxAttempts = Math.Max(1, options.ConnectAttempts),
                ConnectionTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds)
            }
        };

        // Connect via TCP or Serial based on provided options
        DaqifiDevice device;
        string connectionDescription;

        if (!string.IsNullOrWhiteSpace(options.SerialPort))
        {
            device = await DaqifiDeviceFactory.ConnectSerialAsync(
                options.SerialPort,
                options.BaudRate,
                connectionOptions);
            connectionDescription = $"{options.SerialPort} @ {options.BaudRate} baud";
        }
        else
        {
            device = await DaqifiDeviceFactory.ConnectTcpAsync(
                options.IpAddress!,
                options.Port,
                connectionOptions);
            connectionDescription = $"{options.IpAddress}:{options.Port}";
        }

        using var _ = device;
        using var outputWriter = CreateOutputWriter(options);

        device.StatusChanged += (_, eventArgs) =>
        {
            Console.WriteLine($"Status: {eventArgs.Status}");
        };

        using var stopCts = new CancellationTokenSource();
        if (options.DurationSeconds > 0)
        {
            stopCts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        }

        var messageCount = 0;
        device.MessageReceived += (_, eventArgs) =>
        {
            if (stopCts.IsCancellationRequested)
            {
                return;
            }

            if (eventArgs.Message.Data is not DaqifiOutMessage message)
            {
                return;
            }

            var currentCount = Interlocked.Increment(ref messageCount);
            if (options.MessageLimit > 0 && currentCount > options.MessageLimit)
            {
                return;
            }

            if (IsStreamLikeMessage(message))
            {
                WriteStreamSample(outputWriter, message, options.OutputFormat);
            }
            else if (options.ShowStatusMessages && ProtobufProtocolHandler.DetectMessageType(message) == ProtobufMessageType.Status)
            {
                WriteStatusSummary(outputWriter, message);
            }

            if (options.MessageLimit > 0 && currentCount >= options.MessageLimit)
            {
                stopCts.Cancel();
            }
        };

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stopCts.Cancel();
        };

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");

            if (!string.IsNullOrWhiteSpace(options.ChannelMask))
            {
                if (!IsValidChannelMask(options.ChannelMask))
                {
                    Console.Error.WriteLine($"Invalid channel mask: {options.ChannelMask}");
                    return 1;
                }

                device.Send(ScpiMessageProducer.EnableAdcChannels(options.ChannelMask));
            }

            device.Send(ScpiMessageProducer.StartStreaming(options.SampleRate));
            Console.WriteLine($"Streaming at {options.SampleRate} Hz...");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested.
            }

            device.Send(ScpiMessageProducer.StopStreaming);
            Console.WriteLine("Streaming stopped.");

            if (options.MinSamples > 0 && messageCount < options.MinSamples)
            {
                Console.Error.WriteLine(
                    $"Validation failed: received {messageCount} sample(s), expected at least {options.MinSamples}.");
                return 2;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {FormatException(ex)}");
            return 1;
        }
        finally
        {
            try
            {
                if (!options.KeepConnected)
                {
                    device.Disconnect();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}");
            }
        }
    }

    private static async Task DiscoverAsync(int timeoutSeconds)
    {
        using var finder = new WiFiDeviceFinder();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 5 : timeoutSeconds);
        var devices = await finder.DiscoverAsync(timeout);

        Console.WriteLine("Discovered WiFi devices:");
        foreach (var device in devices)
        {
            Console.WriteLine($"  - {device.Name} ({device.IPAddress}:{device.Port}) SN:{device.SerialNumber}");
        }
    }

    private static void DiscoverSerialPorts()
    {
        var ports = SerialStreamTransport.GetAvailablePortNames();

        Console.WriteLine("Available serial ports:");
        if (ports.Length == 0)
        {
            Console.WriteLine("  (none found)");
        }
        else
        {
            foreach (var port in ports)
            {
                Console.WriteLine($"  - {port}");
            }
        }
    }

    private static bool IsStreamLikeMessage(DaqifiOutMessage message)
    {
        return message.AnalogInData.Count > 0 ||
               message.AnalogInDataFloat.Count > 0 ||
               message.DigitalData.Length > 0;
    }

    private static void WriteStreamSample(TextWriter writer, DaqifiOutMessage message, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Jsonl:
                writer.WriteLine(ToJsonLine(message));
                break;
            case OutputFormat.Csv:
                writer.WriteLine(ToCsvLine(message));
                break;
            default:
                writer.WriteLine(ToTextLine(message));
                break;
        }
    }

    private static void WriteStatusSummary(TextWriter writer, DaqifiOutMessage message)
    {
        writer.WriteLine(
            $"Status: analogIn={message.AnalogInPortNum} digital={message.DigitalPortNum} " +
            $"fw={message.DeviceFwRev ?? "unknown"} sn={message.DeviceSn}");
    }

    private static string ToTextLine(DaqifiOutMessage message)
    {
        var builder = new StringBuilder();
        if (message.MsgTimeStamp != 0)
        {
            builder.Append("ts=");
            builder.Append(message.MsgTimeStamp.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
        }

        var analogValues = message.AnalogInDataFloat.Count > 0
            ? message.AnalogInDataFloat.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)).ToList()
            : message.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

        if (analogValues.Count > 0)
        {
            builder.Append("analog=[");
            builder.Append(string.Join(", ", analogValues.Take(8)));
            if (analogValues.Count > 8)
            {
                builder.Append(", ...");
            }
            builder.Append(']');
        }

        if (message.DigitalData.Length > 0)
        {
            var digital = BitConverter.ToString(message.DigitalData.ToByteArray());
            builder.Append(" digital=");
            builder.Append(digital);
        }

        return builder.ToString();
    }

    private static string ToCsvLine(DaqifiOutMessage message)
    {
        var analogValues = message.AnalogInDataFloat.Count > 0
            ? message.AnalogInDataFloat.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)).ToList()
            : message.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

        var timestamp = message.MsgTimeStamp.ToString(CultureInfo.InvariantCulture);
        var analog = string.Join(",", analogValues);
        var digital = message.DigitalData.Length > 0
            ? BitConverter.ToString(message.DigitalData.ToByteArray())
            : string.Empty;

        return $"{timestamp},{analog},{digital}";
    }

    private static string ToJsonLine(DaqifiOutMessage message)
    {
        var analogValues = message.AnalogInDataFloat.Count > 0
            ? message.AnalogInDataFloat.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)).ToList()
            : message.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

        var digitalBytes = message.DigitalData.Length > 0
            ? BitConverter.ToString(message.DigitalData.ToByteArray())
            : string.Empty;

        return "{" +
               $"\"ts\":{message.MsgTimeStamp.ToString(CultureInfo.InvariantCulture)}," +
               $"\"analog\":[{string.Join(",", analogValues)}]," +
               $"\"digital\":\"{digitalBytes}\"" +
               "}";
    }

    private static TextWriter CreateOutputWriter(CliOptions options)
    {
        if (options.OutputFormat == OutputFormat.Csv)
        {
            options.EmitCsvHeader = true;
        }

        if (string.IsNullOrWhiteSpace(options.OutputPath))
        {
            return new PrefixedWriter(Console.Out, options);
        }

        var stream = new FileStream(options.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(false));
        return new PrefixedWriter(writer, options);
    }

    private static bool IsValidChannelMask(string channelMask)
    {
        foreach (var value in channelMask)
        {
            if (value != '0' && value != '1')
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DAQiFi Core CLI");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- --ip <address> [options]");
        Console.WriteLine("  dotnet run -- --serial <port> [options]");
        Console.WriteLine();
        Console.WriteLine("Connection Options:");
        Console.WriteLine("  --ip <address>           Device IP address (for TCP/WiFi connection).");
        Console.WriteLine($"  --port <number>          TCP port (default: {DefaultPort}).");
        Console.WriteLine("  --serial <port>          Serial port name (e.g., COM3, /dev/ttyUSB0, /dev/cu.usbmodem101).");
        Console.WriteLine($"  --baud <rate>            Baud rate for serial connection (default: {DefaultBaudRate}).");
        Console.WriteLine();
        Console.WriteLine("Discovery Options:");
        Console.WriteLine("  -d, --discover           Discover WiFi devices over UDP.");
        Console.WriteLine("  --discover-serial        List available serial ports.");
        Console.WriteLine("  --discover-timeout <s>   WiFi discovery timeout in seconds (default: 5).");
        Console.WriteLine();
        Console.WriteLine("Streaming Options:");
        Console.WriteLine($"  --rate <hz>              Streaming rate in Hz (default: {DefaultRate}).");
        Console.WriteLine($"  --duration <seconds>     Duration to stream (default: {DefaultDurationSeconds}).");
        Console.WriteLine("  --channels <mask>        Enable ADC channels with a 0/1 mask.");
        Console.WriteLine("  --limit <count>          Stop after N stream messages.");
        Console.WriteLine("  --min-samples <count>    Require at least N stream messages (exit code 2 on failure).");
        Console.WriteLine();
        Console.WriteLine("Output Options:");
        Console.WriteLine("  --format <text|csv|jsonl> Output format for stream samples (default: text).");
        Console.WriteLine("  --output <path>          Write samples to file instead of stdout.");
        Console.WriteLine("  --show-status            Print protobuf status messages when received.");
        Console.WriteLine();
        Console.WriteLine("Advanced Options:");
        Console.WriteLine($"  --connect-timeout <s>    Connect timeout in seconds (default: {DefaultConnectTimeoutSeconds}).");
        Console.WriteLine("  --connect-attempts <n>   Total connect attempts (default: 1).");
        Console.WriteLine("  --keep-connected         Keep connection open after streaming stops.");
        Console.WriteLine("  -h, --help               Show this help.");
    }

    private static string FormatException(Exception ex)
    {
        var builder = new StringBuilder();
        builder.Append(ex.GetType().Name);
        builder.Append(": ");
        builder.Append(ex.Message);

        var inner = ex.InnerException;
        while (inner != null)
        {
            builder.Append(" | Inner ");
            builder.Append(inner.GetType().Name);
            builder.Append(": ");
            builder.Append(inner.Message);
            inner = inner.InnerException;
        }

        return builder.ToString();
    }

    private sealed class CliOptions
    {
        public bool Discover { get; private set; }
        public bool DiscoverSerial { get; private set; }
        public string? IpAddress { get; private set; }
        public int Port { get; private set; } = DefaultPort;
        public string? SerialPort { get; private set; }
        public int BaudRate { get; private set; } = DefaultBaudRate;
        public int SampleRate { get; private set; } = DefaultRate;
        public int DurationSeconds { get; private set; } = DefaultDurationSeconds;
        public string? ChannelMask { get; private set; }
        public int MessageLimit { get; private set; }
        public int MinSamples { get; private set; }
        public OutputFormat OutputFormat { get; private set; } = OutputFormat.Text;
        public string? OutputPath { get; private set; }
        public int ConnectTimeoutSeconds { get; private set; } = DefaultConnectTimeoutSeconds;
        public int ConnectAttempts { get; private set; } = 1;
        public bool KeepConnected { get; private set; }
        public bool ShowStatusMessages { get; private set; }
        public bool ShowHelp { get; private set; }
        public int DiscoveryTimeoutSeconds { get; private set; } = 5;
        public bool EmitCsvHeader { get; set; }
        public List<string> Errors { get; } = new();

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-d":
                    case "--discover":
                        options.Discover = true;
                        break;
                    case "--discover-serial":
                        options.DiscoverSerial = true;
                        break;
                    case "--ip":
                        options.IpAddress = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--port":
                        options.Port = GetIntValue(args, ref i, arg, options.Errors, DefaultPort);
                        break;
                    case "--serial":
                        options.SerialPort = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--baud":
                        options.BaudRate = GetIntValue(args, ref i, arg, options.Errors, DefaultBaudRate);
                        break;
                    case "--rate":
                        options.SampleRate = GetIntValue(args, ref i, arg, options.Errors, DefaultRate);
                        break;
                    case "--duration":
                        options.DurationSeconds = GetIntValue(args, ref i, arg, options.Errors, DefaultDurationSeconds);
                        break;
                    case "--channels":
                        options.ChannelMask = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--limit":
                        options.MessageLimit = GetIntValue(args, ref i, arg, options.Errors, 0);
                        break;
                    case "--min-samples":
                        options.MinSamples = GetIntValue(args, ref i, arg, options.Errors, 0);
                        break;
                    case "--format":
                        options.OutputFormat = ParseOutputFormat(GetValue(args, ref i, arg, options.Errors), options.Errors);
                        break;
                    case "--output":
                        options.OutputPath = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--connect-timeout":
                        options.ConnectTimeoutSeconds = GetIntValue(args, ref i, arg, options.Errors, DefaultConnectTimeoutSeconds);
                        break;
                    case "--connect-attempts":
                        options.ConnectAttempts = GetIntValue(args, ref i, arg, options.Errors, 1);
                        break;
                    case "--keep-connected":
                        options.KeepConnected = true;
                        break;
                    case "--discover-timeout":
                        options.DiscoveryTimeoutSeconds = GetIntValue(args, ref i, arg, options.Errors, 5);
                        break;
                    case "--show-status":
                        options.ShowStatusMessages = true;
                        break;
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;
                    default:
                        options.Errors.Add($"Unknown argument: {arg}");
                        break;
                }
            }

            return options;
        }

        private static string? GetValue(string[] args, ref int index, string optionName, List<string> errors)
        {
            if (index + 1 >= args.Length)
            {
                errors.Add($"Missing value for {optionName}.");
                return null;
            }

            index++;
            return args[index];
        }

        private static int GetIntValue(
            string[] args,
            ref int index,
            string optionName,
            List<string> errors,
            int fallback)
        {
            var value = GetValue(args, ref index, optionName, errors);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
            {
                errors.Add($"Invalid integer for {optionName}: {value}");
                return fallback;
            }

            return result;
        }

        private static OutputFormat ParseOutputFormat(string? value, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return OutputFormat.Text;
            }

            return value.Trim().ToLowerInvariant() switch
            {
                "text" => OutputFormat.Text,
                "csv" => OutputFormat.Csv,
                "jsonl" => OutputFormat.Jsonl,
                _ => AddOutputFormatError(errors, value)
            };
        }

        private static OutputFormat AddOutputFormatError(List<string> errors, string value)
        {
            errors.Add($"Invalid format: {value}. Use text, csv, or jsonl.");
            return OutputFormat.Text;
        }
    }

    private enum OutputFormat
    {
        Text,
        Csv,
        Jsonl
    }

    private sealed class PrefixedWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly CliOptions _options;

        public PrefixedWriter(TextWriter inner, CliOptions options)
        {
            _inner = inner;
            _options = options;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void WriteLine(string? value)
        {
            if (_options.EmitCsvHeader && _options.OutputFormat == OutputFormat.Csv)
            {
                _inner.WriteLine("timestamp,analog_values,digital_hex");
                _options.EmitCsvHeader = false;
            }

            _inner.WriteLine(value);
            _inner.Flush();
        }
    }
}
