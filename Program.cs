using System.Globalization;
using System.Net;
using System.Text;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Producers;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Diagnostics;
using Daqifi.Core.Device.Discovery;
using Daqifi.Core.Device.Protocol;
using Daqifi.Core.Device.SdCard;
using Daqifi.Core.Firmware;
using Daqifi.Core.Logging.Export;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;

namespace Daqifi.Core.Cli;

internal class Program
{
    private const int DefaultPort = 9760;
    private const int DefaultBaudRate = 9600;
    private const int DefaultRate = 100;
    private const int DefaultDurationSeconds = 10;
    private const int DefaultConnectTimeoutSeconds = 5;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception ex)
        {
            // Last-resort guard. An exception that escapes Main aborts the process with SIGABRT
            // (exit code 134) and dumps a stack trace, which reads as a tool defect rather than an
            // expected operational failure. Report it the way every other failure path does.
            Console.Error.WriteLine($"Error: {FormatException(ex)}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
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

        // Continuous "watch" mode owns its own exit code and is a dedicated dispatch
        // path: handle it before one-shot discovery so the two never run together.
        if (options.Watch && options.WatchSerial)
        {
            Console.Error.WriteLine("Cannot specify both --watch and --watch-serial. Use one or the other.");
            return 1;
        }

        if (options.Watch)
        {
            return await RunWatchAsync(new WiFiDeviceFinder(), options);
        }

        if (options.WatchSerial)
        {
            return await RunWatchAsync(new SerialDeviceFinder(), options);
        }

        if (options.Discover)
        {
            await DiscoverAsync(options.DiscoveryTimeoutSeconds);
        }

        if (options.DiscoverSerial)
        {
            await DiscoverSerialDevicesAsync(options.DiscoveryTimeoutSeconds);
        }

        // SD card file parse is a local-only operation (no device needed)
        if (!string.IsNullOrWhiteSpace(options.SdParsePath))
        {
            return await RunSdCardParseAsync(options);
        }

        if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadLatestDirectory))
        {
            return await RunFirmwareDownloadLatestAsync(options);
        }

        if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadTag))
        {
            return await RunFirmwareDownloadByTagAsync(options);
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

        if (!string.IsNullOrWhiteSpace(options.FirmwareUpdateLatestDirectory))
        {
            return await RunFirmwareUpdateLatestAsync(options);
        }

        if (!string.IsNullOrWhiteSpace(options.FirmwareHexPath))
        {
            return await RunFirmwareUpdateAsync(options);
        }

        // Route to capture-and-parse (captures live stream, then parses as SD card file)
        if (!string.IsNullOrWhiteSpace(options.CaptureAndParsePath))
        {
            return await RunCaptureAndParseAsync(options);
        }

        // Route to SD card operations if any SD card flags are set
        if (options.SdList || options.SdLogStart || options.SdLogStop ||
            options.SdDeleteFileName != null || options.SdDownloadFileName != null ||
            options.SdFormat || options.SdStorage)
        {
            return await RunSdCardOperationAsync(options);
        }

        if (options.LanChipInfo)
        {
            return await RunLanChipInfoAsync(options);
        }

        if (options.Diagnostics)
        {
            return await RunDiagnosticsAsync(options);
        }

        return await RunStreamingSessionAsync(options);
    }

    /// <summary>
    /// A connected device together with the human-readable description of how it was reached.
    /// </summary>
    private sealed record Connection(DaqifiDevice Device, string Description);

    /// <summary>
    /// Connects to the device described by <paramref name="options"/> over serial or TCP.
    /// </summary>
    /// <returns>
    /// The connection, or <c>null</c> when connecting failed — in which case a single-line error has
    /// already been written to stderr and the caller should return exit code 1.
    /// </returns>
    private static async Task<Connection?> ConnectAsync(CliOptions options)
    {
        var connectionOptions = new DeviceConnectionOptions
        {
            ConnectionRetry = new ConnectionRetryOptions
            {
                Enabled = options.ConnectAttempts > 1,
                MaxAttempts = Math.Max(1, options.ConnectAttempts),
                ConnectionTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds)
            }
        };

        var useSerial = !string.IsNullOrWhiteSpace(options.SerialPort);
        var description = useSerial
            ? $"{options.SerialPort} @ {options.BaudRate} baud"
            : $"{options.IpAddress}:{options.Port}";

        try
        {
            var device = useSerial
                ? await DaqifiDeviceFactory.ConnectSerialAsync(
                    options.SerialPort!,
                    options.BaudRate,
                    connectionOptions)
                : await DaqifiDeviceFactory.ConnectTcpAsync(
                    options.IpAddress!,
                    options.Port,
                    connectionOptions);

            return new Connection(device, description);
        }
        catch (Exception ex)
        {
            // Caught broadly on purpose. A failed connect is an ordinary outcome for a CLI (device
            // unplugged, wrong port, wrong IP), and the transport layer surfaces it as any of a
            // long and evolving list of exception types — IO, UnauthorizedAccess, Timeout, socket
            // and argument errors among them. Catching the specific types would leave the crash in
            // place for whichever one we did not list.
            Console.Error.WriteLine($"Error: Could not connect to {description}: {FormatException(ex)}");
            return null;
        }
    }

    private static async Task<int> RunStreamingSessionAsync(CliOptions options)
    {
        var connection = await ConnectAsync(options);
        if (connection is null)
        {
            return 1;
        }

        var device = connection.Device;
        var connectionDescription = connection.Description;

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

        // The device sends analog and digital data in separate protobuf
        // messages that share the same timestamp. We buffer the pending
        // analog message and merge it with the subsequent digital message
        // before writing a single combined output row.
        DaqifiOutMessage? pendingAnalog = null;
        var pendingLock = new object();

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

            if (options.ShowStatusMessages && ProtobufProtocolHandler.DetectMessageType(message) == ProtobufMessageType.Status)
            {
                WriteStatusSummary(message);
                return;
            }

            if (!IsStreamLikeMessage(message))
            {
                return;
            }

            lock (pendingLock)
            {
                var hasAnalog = message.AnalogInData.Count > 0 || message.AnalogInDataFloat.Count > 0;
                var hasDigital = message.DigitalData.Length > 0;

                if (hasAnalog && !hasDigital)
                {
                    // Flush any stale pending message before buffering the new one
                    if (pendingAnalog != null)
                    {
                        WriteMergedSample(outputWriter, pendingAnalog, null, options.OutputFormat, ref messageCount, options.MessageLimit, stopCts);
                    }

                    pendingAnalog = message;
                    return;
                }

                if (hasDigital && pendingAnalog != null && pendingAnalog.MsgTimeStamp == message.MsgTimeStamp)
                {
                    // Matching pair — merge and write
                    WriteMergedSample(outputWriter, pendingAnalog, message, options.OutputFormat, ref messageCount, options.MessageLimit, stopCts);
                    pendingAnalog = null;
                    return;
                }

                // Digital-only with no matching analog, or timestamp mismatch
                if (pendingAnalog != null)
                {
                    WriteMergedSample(outputWriter, pendingAnalog, null, options.OutputFormat, ref messageCount, options.MessageLimit, stopCts);
                    pendingAnalog = null;
                }

                WriteMergedSample(outputWriter, message, null, options.OutputFormat, ref messageCount, options.MessageLimit, stopCts);
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

            if (options.ShowStatusMessages)
            {
                // The device's status message is requested and consumed during connect, before this
                // method gets a chance to subscribe, and the device does not re-send one while
                // streaming. Print the summary from the metadata that connect already parsed
                // instead of waiting for a message that will never arrive.
                WriteStatusSummary(device.Metadata);
            }

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

            // Flush any buffered analog-only message that never got a matching digital
            lock (pendingLock)
            {
                if (pendingAnalog != null)
                {
                    WriteMergedSample(outputWriter, pendingAnalog, null, options.OutputFormat, ref messageCount, options.MessageLimit, stopCts);
                    pendingAnalog = null;
                }
            }

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

    private static async Task<int> RunFirmwareUpdateAsync(
        CliOptions options,
        string? firmwareHexPathOverride = null)
    {
        var firmwareHexPath = firmwareHexPathOverride ?? options.FirmwareHexPath;
        if (string.IsNullOrWhiteSpace(firmwareHexPath))
        {
            Console.Error.WriteLine("Firmware update requires a HEX path.");
            return 1;
        }

        var connection = await ConnectAsync(options);
        if (connection is null)
        {
            return 1;
        }

        var device = connection.Device;
        var connectionDescription = connection.Description;

        using var _ = device;
        using var hidTransport = new HidLibraryTransport();
        using var httpClient = new HttpClient();
        using var firmwareUpdateService = new FirmwareUpdateService(
            hidTransport,
            new GitHubFirmwareDownloadService(httpClient),
            new ProcessExternalProcessRunner(),
            NullLogger<FirmwareUpdateService>.Instance);

        firmwareUpdateService.StateChanged += (_, stateArgs) =>
        {
            Console.WriteLine(
                $"[State] {stateArgs.PreviousState} -> {stateArgs.CurrentState} | " +
                $"{stateArgs.Operation} | {stateArgs.ChangedAtUtc:O}");
        };

        var progress = new Progress<FirmwareUpdateProgress>(report =>
        {
            var byteSummary = report.TotalBytes > 0
                ? $" [{report.BytesWritten}/{report.TotalBytes} bytes]"
                : string.Empty;

            Console.WriteLine(
                $"[Progress] {report.PercentComplete,6:F1}% | " +
                $"{report.State} | {report.CurrentOperation}{byteSummary}");
        });

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");

            if (device is not DaqifiStreamingDevice streamingDevice)
            {
                Console.Error.WriteLine("Firmware update requires a streaming device connection.");
                return 1;
            }

            Console.WriteLine($"Starting PIC32 firmware update with HEX file: {firmwareHexPath}");
            await firmwareUpdateService.UpdateFirmwareAsync(
                streamingDevice,
                firmwareHexPath,
                progress);

            Console.WriteLine("Firmware update completed successfully.");
            return 0;
        }
        catch (FirmwareUpdateException ex)
        {
            Console.Error.WriteLine("Firmware update failed.");
            Console.Error.WriteLine($"  State: {ex.FailedState}");
            Console.Error.WriteLine($"  Operation: {ex.Operation}");
            Console.Error.WriteLine($"  Message: {ex.Message}");

            if (!string.IsNullOrWhiteSpace(ex.RecoveryGuidance))
            {
                Console.Error.WriteLine($"  Recovery: {ex.RecoveryGuidance}");
            }

            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"  Inner: {FormatException(ex.InnerException)}");
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Firmware update invocation error: {FormatException(ex)}");
            Console.Error.WriteLine($"  State: {firmwareUpdateService.CurrentState}");
            return 1;
        }
        finally
        {
            try
            {
                device.Disconnect();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}");
            }
        }
    }

    private static async Task<int> RunFirmwareDownloadLatestAsync(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmwareDownloadLatestDirectory))
        {
            Console.Error.WriteLine("Missing destination directory for --fw-download-latest.");
            return 1;
        }

        using var httpClient = new HttpClient();
        var downloadService = new GitHubFirmwareDownloadService(httpClient);
        var progress = new Progress<int>(percent =>
        {
            Console.WriteLine($"[Download] {percent,3}%");
        });

        try
        {
            Console.WriteLine("Downloading latest PIC32 firmware...");
            var downloadedPath = await downloadService.DownloadLatestFirmwareAsync(
                options.FirmwareDownloadLatestDirectory,
                progress: progress);

            if (string.IsNullOrWhiteSpace(downloadedPath))
            {
                Console.Error.WriteLine("No latest firmware HEX asset found.");
                return 1;
            }

            Console.WriteLine($"Downloaded latest firmware HEX: {downloadedPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Firmware download failed: {FormatException(ex)}");
            return 1;
        }
    }

    private static async Task<int> RunFirmwareDownloadByTagAsync(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmwareDownloadTag))
        {
            Console.Error.WriteLine("Missing tag for --fw-download-tag.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.FirmwareDownloadTagDirectory))
        {
            Console.Error.WriteLine("Missing destination directory for --fw-download-tag.");
            return 1;
        }

        using var httpClient = new HttpClient();
        var downloadService = new GitHubFirmwareDownloadService(httpClient);
        var progress = new Progress<int>(percent =>
        {
            Console.WriteLine($"[Download] {percent,3}%");
        });

        try
        {
            Console.WriteLine($"Downloading PIC32 firmware for tag {options.FirmwareDownloadTag}...");
            var downloadedPath = await downloadService.DownloadFirmwareByTagAsync(
                options.FirmwareDownloadTag,
                options.FirmwareDownloadTagDirectory,
                progress: progress);

            if (string.IsNullOrWhiteSpace(downloadedPath))
            {
                Console.Error.WriteLine(
                    $"No HEX firmware asset found for tag {options.FirmwareDownloadTag}.");
                return 1;
            }

            Console.WriteLine($"Downloaded firmware HEX: {downloadedPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Firmware download failed: {FormatException(ex)}");
            return 1;
        }
    }

    private static async Task<int> RunFirmwareUpdateLatestAsync(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmwareUpdateLatestDirectory))
        {
            Console.Error.WriteLine("Missing destination directory for --fw-update-latest.");
            return 1;
        }

        using var httpClient = new HttpClient();
        var downloadService = new GitHubFirmwareDownloadService(httpClient);
        var progress = new Progress<int>(percent =>
        {
            Console.WriteLine($"[Download] {percent,3}%");
        });

        try
        {
            Console.WriteLine("Downloading latest PIC32 firmware before update...");
            var downloadedPath = await downloadService.DownloadLatestFirmwareAsync(
                options.FirmwareUpdateLatestDirectory,
                progress: progress);

            if (string.IsNullOrWhiteSpace(downloadedPath))
            {
                Console.Error.WriteLine("No latest firmware HEX asset found.");
                return 1;
            }

            Console.WriteLine($"Downloaded latest firmware HEX: {downloadedPath}");
            return await RunFirmwareUpdateAsync(options, downloadedPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Firmware download/update failed: {FormatException(ex)}");
            return 1;
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

    private static async Task DiscoverSerialDevicesAsync(int timeoutSeconds)
    {
        Console.WriteLine("Discovering serial devices (this may take a moment)...");

        using var finder = new SerialDeviceFinder();
        var timeout = TimeSpan.FromSeconds(timeoutSeconds <= 0 ? 30 : timeoutSeconds);

        finder.DeviceDiscovered += (_, args) =>
        {
            Console.WriteLine($"  Found: {args.DeviceInfo.Name} ({args.DeviceInfo.PortName}) " +
                              $"SN:{args.DeviceInfo.SerialNumber} FW:{args.DeviceInfo.FirmwareVersion}");
        };

        List<IDeviceInfo> devices;
        try
        {
            devices = (await finder.DiscoverAsync(timeout)).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during serial discovery: {ex.Message}");
            devices = new List<IDeviceInfo>();
        }

        Console.WriteLine();
        Console.WriteLine($"Discovered {devices.Count} DAQiFi device(s):");
        if (devices.Count == 0)
        {
            Console.WriteLine("  (no DAQiFi devices found)");
            Console.WriteLine();
            Console.WriteLine("Available serial ports (not verified as DAQiFi devices):");
            var ports = SerialStreamTransport.GetAvailablePortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (var port in ports)
                {
                    Console.WriteLine($"  - {port}");
                }
            }
        }
        else
        {
            foreach (var device in devices)
            {
                Console.WriteLine($"  - {device.Name} ({device.PortName}) SN:{device.SerialNumber} FW:{device.FirmwareVersion}");
            }
        }
    }

    private static async Task<int> RunWatchAsync(IDeviceFinder finder, CliOptions options)
    {
        // A positive --duration auto-stops the watch; <= 0 means run until Ctrl+C,
        // matching how streaming and SD logging treat DurationSeconds in this CLI.
        var bounded = options.DurationSeconds > 0;

        if (bounded)
        {
            Console.WriteLine($"Watching for devices for {options.DurationSeconds}s (Ctrl+C to stop early)...");
        }
        else
        {
            Console.WriteLine("Watching for devices (Ctrl+C to stop)...");
        }

        Console.WriteLine("Legend: [+] discovered, [-] lost");
        Console.WriteLine();

        // Surface operational errors (scan failures, a failed stop) via the exit code so
        // scripts/CI don't read a clean exit as success, consistent with the other handlers.
        var hadError = false;

        // ContinuousDeviceFinder owns and disposes the inner finder (LeaveInnerFinderOpen = false),
        // so a single using covers both.
        using var watcher = new ContinuousDeviceFinder(finder, new ContinuousDiscoveryOptions
        {
            Interval = TimeSpan.FromSeconds(1),
            PassTimeout = TimeSpan.FromSeconds(3),
            MissThreshold = 2,
        });

        watcher.DeviceDiscovered += (_, args) =>
        {
            var d = args.DeviceInfo;
            Console.WriteLine($"  [+] discovered {d.Name} SN:{d.SerialNumber} ({DescribeEndpoint(d)})");
        };

        watcher.DeviceLost += (_, args) =>
        {
            var d = args.DeviceInfo;
            Console.WriteLine($"  [-] lost       {d.Name} SN:{d.SerialNumber} ({DescribeEndpoint(d)})");
        };

        watcher.ScanError += (_, args) =>
        {
            hadError = true;
            Console.Error.WriteLine($"Scan error: {args.Exception.Message}");
        };

        using var stopCts = new CancellationTokenSource();
        if (bounded)
        {
            stopCts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
        }

        // Register Ctrl+C BEFORE starting the scan so an early Ctrl+C triggers graceful
        // shutdown instead of killing the process. CancelKeyPress is a process-global event,
        // so we remove the handler again in the outer finally (it would otherwise leak and
        // could fire against a disposed token if watch mode runs more than once in-process).
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            // Ctrl+C fires on its own thread and can race shutdown disposing stopCts.
            try { stopCts.Cancel(); }
            catch (ObjectDisposedException) { /* already shutting down */ }
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            try
            {
                watcher.Start();
            }
            catch (Exception ex)
            {
                // Never started, so there is nothing to stop; the outer finally unsubscribes.
                Console.Error.WriteLine($"Error: {FormatException(ex)}");
                return 1;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: duration elapsed or Ctrl+C pressed.
            }
            finally
            {
                // Always stop the scan loop, even if the wait above threw unexpectedly.
                try
                {
                    await watcher.StopAsync();
                }
                catch (Exception ex)
                {
                    hadError = true;
                    Console.Error.WriteLine($"Error stopping watcher: {FormatException(ex)}");
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        var live = watcher.Devices;
        Console.WriteLine();
        Console.WriteLine($"Final live set: {live.Count} device(s)");
        foreach (var d in live)
        {
            Console.WriteLine($"  - {d.Name} SN:{d.SerialNumber} ({DescribeEndpoint(d)})");
        }

        return hadError ? 1 : 0;
    }

    // Renders whichever endpoint the transport populated (IP for WiFi, port for serial).
    private static string DescribeEndpoint(IDeviceInfo device)
    {
        if (!string.IsNullOrWhiteSpace(device.PortName))
        {
            return device.PortName!;
        }

        if (device.IPAddress != null)
        {
            return $"{device.IPAddress}:{device.Port}";
        }

        return device.ConnectionType.ToString();
    }

    private static async Task<int> RunSdCardOperationAsync(CliOptions options)
    {
        // Resolve (and reject) the download destination before connecting, so a bad path costs
        // nothing and can never be discovered after a long transfer.
        string? sdDownloadDestination = null;
        if (!string.IsNullOrWhiteSpace(options.SdDownloadFileName))
        {
            sdDownloadDestination = ResolveSdDownloadDestination(
                options.SdDownloadFileName,
                options.SdDownloadDestination,
                options.Overwrite,
                out var destinationError);

            if (sdDownloadDestination is null)
            {
                Console.Error.WriteLine(destinationError);
                return 1;
            }
        }

        var connection = await ConnectAsync(options);
        if (connection is null)
        {
            return 1;
        }

        var device = connection.Device;
        var connectionDescription = connection.Description;

        using var _ = device;

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");

            if (device is not DaqifiStreamingDevice streamingDevice)
            {
                Console.Error.WriteLine("SD card operations require a streaming device.");
                return 1;
            }

            await streamingDevice.InitializeAsync();

            if (options.SdStorage)
            {
                Console.WriteLine("Querying SD card storage...");
                var storage = await streamingDevice.GetSdCardStorageAsync();
                Console.WriteLine($"  Free:  {storage.FreeBytes,15:N0} bytes ({storage.FreeBytes / 1024.0 / 1024.0:F2} MiB)");
                Console.WriteLine($"  Used:  {storage.UsedBytes,15:N0} bytes ({storage.UsedBytes / 1024.0 / 1024.0:F2} MiB)");
                Console.WriteLine($"  Total: {storage.TotalBytes,15:N0} bytes ({storage.TotalBytes / 1024.0 / 1024.0:F2} MiB)");
                if (storage.TotalBytes > 0)
                {
                    Console.WriteLine($"  Used%: {storage.UsedBytes * 100.0 / storage.TotalBytes:F1}%");
                }
            }
            else if (options.SdList)
            {
                Console.WriteLine("Listing SD card files...");
                var files = await streamingDevice.GetSdCardFilesAsync();

                if (files.Count == 0)
                {
                    Console.WriteLine("  (no files found)");
                }
                else
                {
                    foreach (var file in files)
                    {
                        var dateStr = file.CreatedDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown date";
                        var formatStr = GetLogFormatLabel(file.FileName);
                        Console.WriteLine($"  {file.FileName,-35} {dateStr}  [{formatStr}]");
                    }
                }

                Console.WriteLine($"Total: {files.Count} file(s)");
            }
            else if (options.SdLogStart)
            {
                streamingDevice.StreamingFrequency = options.SampleRate;

                // Enable channels before starting SD card logging. Core's
                // StartSdCardLoggingAsync only forwards the channel mask — it does
                // not enable channels itself. Without an explicit mask we enable all
                // ADC channels (the device reports AnalogInputChannels in its
                // capabilities after InitializeAsync) and DIO ports so the log
                // file is not empty.
                var channelMask = options.ChannelMask;
                if (!string.IsNullOrWhiteSpace(channelMask) && !IsValidChannelMask(channelMask))
                {
                    Console.Error.WriteLine($"Invalid channel mask: {channelMask}");
                    return 1;
                }

                if (string.IsNullOrWhiteSpace(channelMask))
                {
                    var adcCount = streamingDevice.Metadata.Capabilities.AnalogInputChannels;
                    if (adcCount > 0)
                    {
                        channelMask = ((1u << adcCount) - 1).ToString();
                    }
                }

                if (!string.IsNullOrWhiteSpace(channelMask))
                {
                    streamingDevice.Send(ScpiMessageProducer.EnableAdcChannels(channelMask));
                    await Task.Delay(100);
                }

                streamingDevice.Send(ScpiMessageProducer.EnableDioPorts());
                await Task.Delay(100);

                await streamingDevice.StartSdCardLoggingAsync(
                    channelMask: channelMask,
                    format: options.SdLogFormat);
                Console.WriteLine("SD card logging started.");

                if (options.DurationSeconds > 0)
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));

                    Console.CancelKeyPress += (_, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cts.Cancel();
                    };

                    try
                    {
                        Console.WriteLine($"Logging for {options.DurationSeconds} seconds (Ctrl+C to stop early)...");
                        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }

                    await streamingDevice.StopSdCardLoggingAsync();
                    Console.WriteLine("SD card logging stopped.");
                }
                else
                {
                    Console.WriteLine("Use --sd-log-stop to stop logging.");
                }
            }
            else if (options.SdLogStop)
            {
                await streamingDevice.StopSdCardLoggingAsync();
                Console.WriteLine("SD card logging stopped.");
            }
            else if (!string.IsNullOrWhiteSpace(options.SdDeleteFileName))
            {
                Console.WriteLine($"Deleting SD card file: {options.SdDeleteFileName}");
                await streamingDevice.DeleteSdCardFileAsync(options.SdDeleteFileName);
                Console.WriteLine("Delete command sent.");

                Console.WriteLine("Refreshing file list...");
                var files = streamingDevice.SdCardFiles;
                foreach (var file in files)
                {
                    var dateStr = file.CreatedDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown date";
                    var formatStr = GetLogFormatLabel(file.FileName);
                    Console.WriteLine($"  {file.FileName,-35} {dateStr}  [{formatStr}]");
                }
                Console.WriteLine($"Total: {files.Count} file(s)");
            }
            else if (!string.IsNullOrWhiteSpace(options.SdDownloadFileName))
            {
                Console.WriteLine($"Downloading SD card file: {options.SdDownloadFileName}");

                var progress = new Progress<SdCardTransferProgress>(p =>
                {
                    Console.Write($"\r  Received {p.BytesReceived:N0} bytes...");
                });

                // The destination-stream overload is used instead of the convenience overload so
                // the file lands where the user can find it. The convenience overload writes to
                // Path.GetTempPath()/daqifi_<guid>.bin, which is subject to OS temp cleanup and
                // bears no relation to the source name.
                var destinationPath = sdDownloadDestination!;
                SdCardDownloadResult result;

                // Download into a sibling temporary file and publish it only once the transfer has
                // completed. Writing straight to the destination would mean a stalled or failed
                // download destroys whatever was already there (with --overwrite, the moment the
                // file is opened) or leaves a partial one that looks like a good download. A
                // sibling rather than the system temp directory keeps the publish step a
                // same-volume rename instead of a cross-device copy.
                var tempPath = $"{destinationPath}.part-{Guid.NewGuid():N}";
                try
                {
                    await using (var destinationStream = new FileStream(
                        tempPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 65536,
                        useAsync: true))
                    {
                        result = await streamingDevice.DownloadSdCardFileAsync(
                            options.SdDownloadFileName, destinationStream, progress);
                    }

                    // A single rename on both POSIX and Windows, so the destination is never
                    // observed missing or half-written. The two-argument overload throws if the
                    // destination appeared while the transfer was running, which is the race guard
                    // that opening with CreateNew used to provide.
                    if (options.Overwrite)
                    {
                        File.Move(tempPath, destinationPath, overwrite: true);
                    }
                    else
                    {
                        File.Move(tempPath, destinationPath);
                    }
                }
                catch
                {
                    // Only ever removes our own temp file; the destination is untouched unless the
                    // rename above already succeeded.
                    try { File.Delete(tempPath); } catch { /* best effort */ }
                    throw;
                }

                Console.WriteLine();
                Console.WriteLine($"Download complete: {result.FileSize:N0} bytes in {result.Duration.TotalSeconds:F1}s");
                Console.WriteLine($"Saved to: {destinationPath}");

                // Parse the downloaded file (any supported format). The format is a property of the
                // file's name on the device, not of wherever the user chose to save it — otherwise
                // a --sd-download-to path with no extension (or a different one) would silently
                // skip the parse that --sd-download documents.
                if (IsParseableLogFile(options.SdDownloadFileName))
                {
                    Console.WriteLine();
                    Console.WriteLine("--- Parsing downloaded file ---");
                    options.SdParsePath = destinationPath;

                    // Pass the connected device's config so the parser can
                    // scale raw ADC values using the device's calibration.
                    var deviceConfig = SdCardDeviceConfiguration.FromDevice((DaqifiDevice)device);
                    return await RunSdCardParseAsync(options, deviceConfig, options.SdDownloadFileName);
                }
            }
            else if (options.SdFormat)
            {
                Console.Write("Are you sure you want to format the SD card? This erases ALL data. (y/N): ");
                var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                if (confirm == "y")
                {
                    await streamingDevice.FormatSdCardAsync();
                    Console.WriteLine("Format command sent.");
                }
                else
                {
                    Console.WriteLine("Format canceled.");
                }
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
                device.Disconnect();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}");
            }
        }
    }

    /// <summary>
    /// Works out where <c>--sd-download</c> should write, defaulting to the source file name in the
    /// current directory and honouring <c>--sd-download-to</c> when given.
    /// </summary>
    /// <param name="sourceFileName">The file name on the device's SD card.</param>
    /// <param name="requestedDestination">The <c>--sd-download-to</c> value, if any.</param>
    /// <param name="allowOverwrite">Whether <c>--overwrite</c> was given.</param>
    /// <param name="error">Set to the message to print when the destination is unusable.</param>
    /// <returns>The absolute destination path, or <c>null</c> when it is unusable.</returns>
    private static string? ResolveSdDownloadDestination(
        string sourceFileName,
        string? requestedDestination,
        bool allowOverwrite,
        out string? error)
    {
        error = null;

        // The file name is echoed back from the device's own listing, so treat it as untrusted:
        // strip any directory component before using it, or a name like "../../evil.bin" would
        // write outside the directory the user chose.
        var safeName = Path.GetFileName(sourceFileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or "..")
        {
            error = $"Cannot derive a destination file name from '{sourceFileName}'. Use --sd-download-to <path>.";
            return null;
        }

        string candidate;
        if (string.IsNullOrWhiteSpace(requestedDestination))
        {
            candidate = safeName;
        }
        else if (Directory.Exists(requestedDestination) || EndsWithDirectorySeparator(requestedDestination))
        {
            candidate = Path.Combine(requestedDestination, safeName);
        }
        else
        {
            candidate = requestedDestination;
        }

        string destinationPath;
        try
        {
            destinationPath = Path.GetFullPath(candidate);
        }
        catch (Exception ex)
        {
            error = $"Invalid destination path '{candidate}': {FormatException(ex)}";
            return null;
        }

        if (Directory.Exists(destinationPath))
        {
            error = $"Destination is a directory: {destinationPath}";
            return null;
        }

        if (!allowOverwrite && File.Exists(destinationPath))
        {
            error = $"Destination already exists: {destinationPath}. " +
                    "Pass --overwrite to replace it, or use --sd-download-to <path> to write somewhere else.";
            return null;
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            error = $"Destination directory does not exist: {directory}";
            return null;
        }

        return destinationPath;
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> names a log format the parser understands.
    /// </summary>
    /// <remarks>
    /// The extensions are listed here rather than read from Core (<c>TryDetectFormat</c> /
    /// <c>SupportedExtensions</c>) because this project also builds against the pinned
    /// <c>Daqifi.Core</c> package release, which predates both — the same reason
    /// <see cref="GetLogFormatLabel(string)"/> lists them too.
    /// </remarks>
    private static bool IsParseableLogFile(string fileName)
        => Path.GetExtension(fileName).ToLowerInvariant() is ".bin" or ".csv" or ".json";

    private static bool EndsWithDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);
    }

    /// <param name="options">Parsed CLI options; <c>SdParsePath</c> selects the file to read.</param>
    /// <param name="deviceConfig">Optional live device configuration used to scale raw ADC values.</param>
    /// <param name="sourceFileName">
    /// The name the file had on the device, when it was just downloaded. The format and the logging
    /// date are read from this name rather than from the local path, so saving under a different
    /// name (or none) via <c>--sd-download-to</c> does not change how the file is parsed.
    /// </param>
    private static async Task<int> RunSdCardParseAsync(
        CliOptions options,
        SdCardDeviceConfiguration? deviceConfig = null,
        string? sourceFileName = null)
    {
        var filePath = options.SdParsePath!;
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"File not found: {filePath}");
            return 1;
        }

        // When the file was downloaded, its identity for parsing purposes is the device-side name.
        var formatSourceName = sourceFileName ?? filePath;

        SdCardLogFormat format;
        try
        {
            format = SdCardFileParserFactory.DetectFormat(formatSourceName);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var formatLabel = GetLogFormatLabel(format);
        Console.WriteLine($"Parsing {formatLabel} SD card log file: {filePath}");

        var parseOptions = new SdCardParseOptions
        {
            Progress = new Progress<SdCardParseProgress>(p =>
            {
                var pct = p.TotalBytes > 0
                    ? (p.BytesRead * 100 / p.TotalBytes).ToString(CultureInfo.InvariantCulture)
                    : "?";
                var unit = format == SdCardLogFormat.Protobuf ? "messages" : "lines";
                Console.Write($"\r  {pct}% — {p.MessagesRead} {unit} read ({p.BytesRead} bytes)");
            }),
            ConfigurationOverride = deviceConfig
        };

        try
        {
            // Parsed with an explicit format rather than via ParseFileAsync, which would re-derive
            // the format from the local path. Identical for a plain --sd-parse (the name passed for
            // metadata is the same one ParseFileAsync would use); for a download it keeps the
            // device-side name, so the format and the logging date survive --sd-download-to.
            await using var parseStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: parseOptions.BufferSize,
                useAsync: true);

            var session = await SdCardFileParserFactory.ParseWithFormatAsync(
                parseStream,
                Path.GetFileName(formatSourceName),
                format,
                parseOptions);
            Console.WriteLine();

            Console.WriteLine($"File: {session.FileName}");
            if (session.FileCreatedDate.HasValue)
            {
                Console.WriteLine($"Created: {session.FileCreatedDate.Value:yyyy-MM-dd HH:mm:ss}");
            }

            if (session.DeviceConfig != null)
            {
                var cfg = session.DeviceConfig;
                Console.WriteLine($"Device Config:");
                Console.WriteLine($"  Analog ports:     {cfg.AnalogPortCount}");
                Console.WriteLine($"  Digital ports:    {cfg.DigitalPortCount}");
                Console.WriteLine($"  Timestamp freq:   {cfg.TimestampFrequency} Hz");
                if (cfg.FirmwareRevision != null) Console.WriteLine($"  Firmware:         {cfg.FirmwareRevision}");
                if (cfg.DevicePartNumber != null) Console.WriteLine($"  Part number:      {cfg.DevicePartNumber}");
                if (cfg.DeviceSerialNumber != null) Console.WriteLine($"  Serial number:    {cfg.DeviceSerialNumber}");
            }

            if (!string.IsNullOrWhiteSpace(options.SdExportCsvPath))
            {
                return await ExportSdSessionToCsvAsync(session, options.SdExportCsvPath!);
            }

            var sampleCount = 0;
            using var outputWriter = CreateOutputWriter(options);

            await foreach (var sample in session.Samples)
            {
                sampleCount++;

                if (options.MessageLimit > 0 && sampleCount > options.MessageLimit)
                {
                    break;
                }

                var analogStr = string.Join(", ",
                    sample.AnalogValues.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)));
                outputWriter.WriteLine(
                    $"[{sample.Timestamp:HH:mm:ss.fff}] analog=[{analogStr}] digital=0x{sample.DigitalData:X}");
            }

            Console.WriteLine($"Total samples: {sampleCount}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing file: {FormatException(ex)}");
            return 1;
        }
    }

    private static async Task<int> ExportSdSessionToCsvAsync(SdCardLogSession session, string outputPath)
    {
        // Determine analog port count: prefer the file's status message; fall back to peeking
        // the first sample (which carries the actual emitted analog values).
        IAsyncEnumerable<SdCardLogEntry> samplesForExport;
        int analogCount;

        if (session.DeviceConfig is not null)
        {
            analogCount = session.DeviceConfig.AnalogPortCount;
            samplesForExport = session.Samples;
        }
        else
        {
            var enumerator = session.Samples.GetAsyncEnumerator();
            if (!await enumerator.MoveNextAsync())
            {
                Console.Error.WriteLine("Cannot export to CSV: file is empty.");
                await enumerator.DisposeAsync();
                return 1;
            }

            var first = enumerator.Current;
            analogCount = first.AnalogValues.Count;
            samplesForExport = PrependAndStreamAsync(first, enumerator);
        }

        var sampleSource = new SdCardSampleSource(
            new SdCardLogSession(session.FileName, session.FileCreatedDate, session.DeviceConfig, samplesForExport),
            analogCount);
        var exporter = new CsvExporter();
        var exportOptions = new CsvExportOptions { UseRelativeTime = false };

        Console.WriteLine($"Exporting CSV to: {outputPath}");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long rowsWritten = 0;
        var lastReport = stopwatch.Elapsed;

        await using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(fileStream))
        {
            var countingSource = new RowCountingSampleSource(sampleSource, rows =>
            {
                rowsWritten = rows;
                if (stopwatch.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    Console.Write($"\r  {rows} rows written ({rows / Math.Max(1, stopwatch.Elapsed.TotalSeconds):N0} rows/s)");
                    lastReport = stopwatch.Elapsed;
                }
            });

            await exporter.ExportAsync(countingSource, writer, exportOptions);
        }

        stopwatch.Stop();
        Console.WriteLine();

        var fileInfo = new FileInfo(outputPath);
        var seconds = stopwatch.Elapsed.TotalSeconds;
        var rowsPerSec = seconds > 0 ? rowsWritten / seconds : 0;
        var mbPerSec = seconds > 0 ? fileInfo.Length / seconds / (1024.0 * 1024.0) : 0;

        Console.WriteLine($"Wrote {rowsWritten:N0} rows ({fileInfo.Length:N0} bytes) in {seconds:F2}s");
        Console.WriteLine($"  Throughput: {rowsPerSec:N0} rows/s, {mbPerSec:F2} MB/s");
        return 0;
    }

    private static async IAsyncEnumerable<SdCardLogEntry> PrependAndStreamAsync(
        SdCardLogEntry first,
        IAsyncEnumerator<SdCardLogEntry> rest)
    {
        try
        {
            yield return first;
            while (await rest.MoveNextAsync())
            {
                yield return rest.Current;
            }
        }
        finally
        {
            await rest.DisposeAsync();
        }
    }

    /// <summary>
    /// Captures raw protobuf stream data from device to a local .bin file,
    /// then parses it with the SD card file parser.
    /// </summary>
    private static async Task<int> RunCaptureAndParseAsync(CliOptions options)
    {
        var connection = await ConnectAsync(options);
        if (connection is null)
        {
            return 1;
        }

        var device = connection.Device;
        var connectionDescription = connection.Description;

        using var _ = device;
        var capturePath = options.CaptureAndParsePath!;

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");
            Console.WriteLine($"Capturing raw stream to: {capturePath}");

            await using var captureStream = new FileStream(capturePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            var messageCount = 0;
            var statusCaptured = false;

            using var stopCts = new CancellationTokenSource();
            if (options.DurationSeconds > 0)
            {
                stopCts.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
            }

            var captureLock = new object();
            device.MessageReceived += (_, eventArgs) =>
            {
                if (stopCts.IsCancellationRequested) return;
                if (eventArgs.Message.Data is not DaqifiOutMessage message) return;

                lock (captureLock)
                {
                    // Write varint-prefixed protobuf to file
                    var payload = message.ToByteArray();
                    var coded = new Google.Protobuf.CodedOutputStream(captureStream, leaveOpen: true);
                    coded.WriteLength(payload.Length);
                    coded.Flush();
                    captureStream.Write(payload, 0, payload.Length);
                    captureStream.Flush();

                    var msgType = ProtobufProtocolHandler.DetectMessageType(message);
                    if (msgType == ProtobufMessageType.Status) statusCaptured = true;
                }

                Interlocked.Increment(ref messageCount);
                Console.Write($"\r  Captured {messageCount} messages (status: {(statusCaptured ? "yes" : "no")})");
            };

            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stopCts.Cancel();
            };

            device.Send(ScpiMessageProducer.StartStreaming(options.SampleRate));
            Console.WriteLine($"Streaming at {options.SampleRate} Hz for {options.DurationSeconds}s...");

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stopCts.Token);
            }
            catch (OperationCanceledException) { }

            device.Send(ScpiMessageProducer.StopStreaming);
            await Task.Delay(500); // Let final messages arrive
            Console.WriteLine();
            Console.WriteLine($"Capture complete: {messageCount} messages written to {capturePath}");

            // Now parse the captured file
            Console.WriteLine();
            Console.WriteLine("--- Parsing captured file ---");
            options.SdParsePath = capturePath;
            return await RunSdCardParseAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {FormatException(ex)}");
            return 1;
        }
        finally
        {
            try { device.Disconnect(); }
            catch (Exception ex) { Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}"); }
        }
    }

    private static async Task<int> RunLanChipInfoAsync(CliOptions options)
    {
        var connection = await ConnectAsync(options);
        if (connection is null)
        {
            return 1;
        }

        var device = connection.Device;
        var connectionDescription = connection.Description;

        using var _ = device;

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");

            if (device is not DaqifiStreamingDevice streamingDevice)
            {
                Console.Error.WriteLine("LAN chip info query requires a streaming device.");
                return 1;
            }

            await streamingDevice.InitializeAsync();

            Console.WriteLine("Querying LAN chip info...");
            var info = await streamingDevice.GetLanChipInfoAsync();

            if (info == null)
            {
                Console.Error.WriteLine("Device did not return a recognizable LAN chip info response.");
                return 1;
            }

            Console.WriteLine($"ChipId:    {info.ChipId}");
            Console.WriteLine($"FwVersion: {info.FwVersion}");
            Console.WriteLine($"BuildDate: {info.BuildDate}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {FormatException(ex)}");
            return 1;
        }
        finally
        {
            try { device.Disconnect(); }
            catch (Exception ex) { Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}"); }
        }
    }

    private static async Task<int> RunDiagnosticsAsync(CliOptions options)
    {
        var connectionOptions = new DeviceConnectionOptions
        {
            ConnectionRetry = new ConnectionRetryOptions
            {
                Enabled = options.ConnectAttempts > 1,
                MaxAttempts = Math.Max(1, options.ConnectAttempts),
                ConnectionTimeout = TimeSpan.FromSeconds(options.ConnectTimeoutSeconds)
            }
        };

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

        try
        {
            Console.WriteLine($"Connected to {connectionDescription}");

            if (device is not DaqifiStreamingDevice streamingDevice)
            {
                Console.Error.WriteLine("Diagnostics require a streaming device.");
                return 1;
            }

            await streamingDevice.InitializeAsync();

            // Each step is isolated: one unsupported/failed query is reported but
            // does not abort the rest, so a single run surfaces the full picture.
            var failures = 0;
            async Task Step(string label, Func<Task> action)
            {
                Console.WriteLine();
                Console.WriteLine($"== {label} ==");
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"  FAILED: {FormatException(ex)}");
                }
            }

            await Step("Error queue depth (SYSTem:ERRor:COUNt?)", async () =>
            {
                var count = await streamingDevice.GetSystemErrorCountAsync();
                Console.WriteLine($"  Queued SCPI errors: {count}");
            });

            await Step("Memory diagnostics (SYSTem:MEMory:FREE?)", async () =>
            {
                var mem = await streamingDevice.GetMemoryDiagnosticsAsync();
                Console.WriteLine($"  HeapFree={mem.HeapFree} / HeapTotal={mem.HeapTotal} " +
                                  $"(HeapUsed={mem.HeapUsed}, MinEverFree={mem.HeapMinEverFree})");
                Console.WriteLine($"  SamplePool: count={mem.SamplePoolCount} inUse={mem.SamplePoolInUse} maxUsed={mem.SamplePoolMaxUsed}");
                Console.WriteLine($"  ({mem.Values.Count} fields total)");
            });

            await Step("Stream stats (SYSTem:STReam:STATS?)", async () =>
            {
                var stats = await streamingDevice.GetStreamStatsAsync();
                Console.WriteLine($"  TotalSamplesStreamed={stats.TotalSamplesStreamed} TotalBytesStreamed={stats.TotalBytesStreamed}");
                Console.WriteLine($"  QueueDroppedSamples={stats.QueueDroppedSamples} TimerISRCalls={stats.TimerISRCalls}");
                Console.WriteLine($"  ({stats.Values.Count} counters total)");
            });

            await Step("Inject test log messages (SYSTem:LOG:TEST)", async () =>
            {
                await streamingDevice.TestSystemLogAsync();
                Console.WriteLine("  Test messages injected.");
            });

            await Step("Read system log (SYSTem:LOG?)", async () =>
            {
                var log = await streamingDevice.GetSystemLogAsync();
                Console.WriteLine($"  {log.Count} entries:");
                foreach (var entry in log.Take(20))
                {
                    Console.WriteLine($"    {entry.Message}");
                }
                if (log.Count > 20)
                {
                    Console.WriteLine($"    ... ({log.Count - 20} more)");
                }
            });

            await Step("Command history (SYSTem:LOG:CMDHistory?)", async () =>
            {
                var history = await streamingDevice.GetCommandHistoryAsync();
                Console.WriteLine($"  {history.Count} commands:");
                foreach (var command in history.Take(20))
                {
                    Console.WriteLine($"    {command}");
                }
            });

            await Step("Set log level (SYSTem:LOG:LEVel STREAM,2)", async () =>
            {
                var applied = await streamingDevice.SetLogLevelAsync("STREAM", 2);
                Console.WriteLine($"  {applied.Module}: level={applied.Level} (ceiling {applied.Ceiling})");
            });

            await Step("Clear system log (SYSTem:LOG:CLEar)", async () =>
            {
                await streamingDevice.ClearSystemLogAsync();
                Console.WriteLine("  Log cleared.");
            });

            Console.WriteLine();
            if (failures == 0)
            {
                Console.WriteLine("All diagnostics queries succeeded.");
                return 0;
            }

            Console.Error.WriteLine($"{failures} diagnostics step(s) failed.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {FormatException(ex)}");
            return 1;
        }
        finally
        {
            try { device.Disconnect(); }
            catch (Exception ex) { Console.Error.WriteLine($"Disconnect error: {FormatException(ex)}"); }
        }
    }

    private static bool IsStreamLikeMessage(DaqifiOutMessage message)
    {
        return message.AnalogInData.Count > 0 ||
               message.AnalogInDataFloat.Count > 0 ||
               message.DigitalData.Length > 0;
    }

    private static void WriteMergedSample(
        TextWriter writer,
        DaqifiOutMessage analogMessage,
        DaqifiOutMessage? digitalMessage,
        OutputFormat format,
        ref int messageCount,
        int messageLimit,
        CancellationTokenSource stopCts)
    {
        var currentCount = Interlocked.Increment(ref messageCount);
        if (messageLimit > 0 && currentCount > messageLimit)
        {
            return;
        }

        switch (format)
        {
            case OutputFormat.Jsonl:
                writer.WriteLine(ToJsonLine(analogMessage, digitalMessage));
                break;
            case OutputFormat.Csv:
                writer.WriteLine(ToCsvLine(analogMessage, digitalMessage));
                break;
            default:
                writer.WriteLine(ToTextLine(analogMessage, digitalMessage));
                break;
        }

        if (messageLimit > 0 && currentCount >= messageLimit)
        {
            stopCts.Cancel();
        }
    }

    /// <summary>
    /// Prints the device's reported channel counts, firmware revision and serial number from the
    /// metadata that connect/initialization already parsed.
    /// </summary>
    private static void WriteStatusSummary(DeviceMetadata metadata)
    {
        WriteStatusSummary(
            metadata.Capabilities.AnalogInputChannels,
            metadata.Capabilities.DigitalChannels,
            metadata.FirmwareVersion,
            metadata.SerialNumber);
    }

    /// <summary>
    /// Prints the same summary for a status message that arrives while a handler is subscribed.
    /// </summary>
    private static void WriteStatusSummary(DaqifiOutMessage message)
    {
        WriteStatusSummary(
            (int)message.AnalogInPortNum,
            (int)message.DigitalPortNum,
            message.DeviceFwRev,
            message.DeviceSn == 0 ? null : message.DeviceSn.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteStatusSummary(int analogInPorts, int digitalPorts, string? firmware, string? serialNumber)
    {
        // Written to the console rather than the sample output writer: this is diagnostic output,
        // and mixing it into a --output CSV/JSONL file would corrupt the data.
        Console.WriteLine(
            $"Status: analogIn={analogInPorts} digital={digitalPorts} " +
            $"fw={(string.IsNullOrWhiteSpace(firmware) ? "unknown" : firmware)} " +
            $"sn={(string.IsNullOrWhiteSpace(serialNumber) ? "unknown" : serialNumber)}");
    }

    private static string ToTextLine(DaqifiOutMessage analogMsg, DaqifiOutMessage? digitalMsg)
    {
        var builder = new StringBuilder();
        if (analogMsg.MsgTimeStamp != 0)
        {
            builder.Append("ts=");
            builder.Append(analogMsg.MsgTimeStamp.ToString(CultureInfo.InvariantCulture));
            builder.Append(' ');
        }

        var analogValues = analogMsg.AnalogInDataFloat.Count > 0
            ? analogMsg.AnalogInDataFloat.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)).ToList()
            : analogMsg.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

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

        // Prefer the paired digital message; fall back to analog message's own digital data
        var digitalSource = digitalMsg ?? analogMsg;
        if (digitalSource.DigitalData.Length > 0)
        {
            var digital = BitConverter.ToString(digitalSource.DigitalData.ToByteArray());
            builder.Append(" digital=");
            builder.Append(digital);
        }

        return builder.ToString();
    }

    private static string ToCsvLine(DaqifiOutMessage analogMsg, DaqifiOutMessage? digitalMsg)
    {
        var analogValues = analogMsg.AnalogInDataFloat.Count > 0
            ? analogMsg.AnalogInDataFloat.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)).ToList()
            : analogMsg.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

        var timestamp = analogMsg.MsgTimeStamp.ToString(CultureInfo.InvariantCulture);
        var analog = string.Join(",", analogValues);

        var digitalSource = digitalMsg ?? analogMsg;
        var digital = digitalSource.DigitalData.Length > 0
            ? BitConverter.ToString(digitalSource.DigitalData.ToByteArray())
            : string.Empty;

        return $"{timestamp},{analog},{digital}";
    }

    private static string ToJsonLine(DaqifiOutMessage analogMsg, DaqifiOutMessage? digitalMsg)
    {
        var analogValues = analogMsg.AnalogInDataFloat.Count > 0
            ? analogMsg.AnalogInDataFloat.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)).ToList()
            : analogMsg.AnalogInData.Select(value => value.ToString(CultureInfo.InvariantCulture)).ToList();

        var digitalSource = digitalMsg ?? analogMsg;
        var digitalBytes = digitalSource.DigitalData.Length > 0
            ? BitConverter.ToString(digitalSource.DigitalData.ToByteArray())
            : string.Empty;

        return "{" +
               $"\"ts\":{analogMsg.MsgTimeStamp.ToString(CultureInfo.InvariantCulture)}," +
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

    private static string GetLogFormatLabel(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".bin" => "Protobuf",
            ".json" => "JSON",
            ".csv" => "CSV",
            _ => "Unknown"
        };
    }

    private static string GetLogFormatLabel(SdCardLogFormat format)
    {
        return format switch
        {
            SdCardLogFormat.Protobuf => "Protobuf",
            SdCardLogFormat.Json => "JSON",
            SdCardLogFormat.Csv => "CSV",
            _ => "Unknown"
        };
    }

    private static bool IsValidChannelMask(string channelMask)
    {
        return uint.TryParse(channelMask, out _);
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
        Console.WriteLine("  --watch                  Continuously watch for WiFi devices (live +/- updates), bounded by --duration.");
        Console.WriteLine("  --watch-serial           Continuously watch for serial devices (live +/- updates), bounded by --duration.");
        Console.WriteLine();
        Console.WriteLine("Streaming Options:");
        Console.WriteLine($"  --rate <hz>              Streaming rate in Hz (default: {DefaultRate}).");
        Console.WriteLine($"  --duration <seconds>     Duration to stream (default: {DefaultDurationSeconds}).");
        Console.WriteLine("  --channels <mask>        Enable ADC channels with a decimal bitmask (e.g. 7 = ch 0,1,2).");
        Console.WriteLine("  --limit <count>          Stop after N stream messages.");
        Console.WriteLine("  --min-samples <count>    Require at least N stream messages (exit code 2 on failure).");
        Console.WriteLine();
        Console.WriteLine("Output Options:");
        Console.WriteLine("  --format <text|csv|jsonl> Output format for stream samples (default: text).");
        Console.WriteLine("  --output <path>          Write samples to file instead of stdout.");
        Console.WriteLine("  --show-status            Print the device status summary after connecting.");
        Console.WriteLine();
        Console.WriteLine("SD Card Options:");
        Console.WriteLine("  --sd-list                List files on the SD card (USB/serial only).");
        Console.WriteLine("  --sd-storage             Show SD card free/used/total space (USB/serial only).");
        Console.WriteLine("  --sd-log-start           Start SD card logging (use --duration to auto-stop).");
        Console.WriteLine("  --sd-log-format <fmt>    Log format: protobuf (default), json, csv.");
        Console.WriteLine("  --sd-log-stop            Stop SD card logging.");
        Console.WriteLine("  --sd-delete <filename>   Delete a file from the SD card.");
        Console.WriteLine("  --sd-download <filename> Download a file from the SD card, saved as ./<filename>.");
        Console.WriteLine("  --sd-download-to <path>  Destination for --sd-download: a file path, or a directory to");
        Console.WriteLine("                           save under the source file name.");
        Console.WriteLine("  --overwrite              Allow --sd-download to replace an existing destination file.");
        Console.WriteLine("  --sd-format              Format the SD card (erases all data).");
        Console.WriteLine("  --sd-parse <path>        Parse a .bin log file from the SD card.");
        Console.WriteLine("  --sd-export-csv <path>   With --sd-parse, write samples as CSV to <path> using Daqifi.Core's CsvExporter.");
        Console.WriteLine("  --sd-capture-parse <p>   Capture live stream to file, then parse it.");
        Console.WriteLine();
        Console.WriteLine("Firmware Download Options:");
        Console.WriteLine("  --fw-download-latest <d> Download latest PIC32 firmware HEX into directory <d>.");
        Console.WriteLine("  --fw-download-tag <t> <d> Download PIC32 firmware HEX for tag <t> into directory <d>.");
        Console.WriteLine();
        Console.WriteLine("Firmware Update Options:");
        Console.WriteLine("  --fw-update-hex <path>   Run PIC32 firmware update from a local Intel HEX file.");
        Console.WriteLine("  --fw-update-latest <d>   Download latest PIC32 firmware HEX to <d>, then update.");
        Console.WriteLine();
        Console.WriteLine("Device Info Options:");
        Console.WriteLine("  --lan-chip-info          Query the WiFi module chip ID, firmware version, and build date.");
        Console.WriteLine("  --diagnostics            Run all IDeviceDiagnostics queries (log, levels, command");
        Console.WriteLine("                           history, error-queue depth, stream/memory counters).");
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
        public bool Watch { get; private set; }
        public bool WatchSerial { get; private set; }
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
        public bool SdList { get; private set; }
        public bool SdLogStart { get; private set; }
        public bool SdLogStop { get; private set; }
        public SdCardLogFormat SdLogFormat { get; private set; } = SdCardLogFormat.Protobuf;
        public string? SdDeleteFileName { get; private set; }
        public string? SdDownloadFileName { get; private set; }
        public string? SdDownloadDestination { get; private set; }
        public bool Overwrite { get; private set; }
        public bool SdFormat { get; private set; }
        public bool SdStorage { get; private set; }
        public string? SdParsePath { get; set; }
        public string? SdExportCsvPath { get; private set; }
        public string? CaptureAndParsePath { get; private set; }
        public string? FirmwareDownloadLatestDirectory { get; private set; }
        public string? FirmwareDownloadTag { get; private set; }
        public string? FirmwareDownloadTagDirectory { get; private set; }
        public string? FirmwareHexPath { get; private set; }
        public string? FirmwareUpdateLatestDirectory { get; private set; }
        public bool LanChipInfo { get; private set; }
        public bool Diagnostics { get; private set; }
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
                    case "--watch":
                        options.Watch = true;
                        break;
                    case "--watch-serial":
                        options.WatchSerial = true;
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
                    case "--sd-list":
                        options.SdList = true;
                        break;
                    case "--sd-log-start":
                        options.SdLogStart = true;
                        break;
                    case "--sd-log-format":
                        options.SdLogFormat = ParseSdLogFormat(GetValue(args, ref i, arg, options.Errors), options.Errors);
                        break;
                    case "--sd-log-stop":
                        options.SdLogStop = true;
                        break;
                    case "--sd-delete":
                        options.SdDeleteFileName = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--sd-download":
                        options.SdDownloadFileName = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--sd-download-to":
                        options.SdDownloadDestination = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--overwrite":
                        options.Overwrite = true;
                        break;
                    case "--sd-format":
                        options.SdFormat = true;
                        break;
                    case "--sd-storage":
                        options.SdStorage = true;
                        break;
                    case "--sd-parse":
                        options.SdParsePath = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--sd-export-csv":
                        options.SdExportCsvPath = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--sd-capture-parse":
                        options.CaptureAndParsePath = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--fw-download-latest":
                        options.FirmwareDownloadLatestDirectory = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--fw-download-tag":
                    {
                        options.FirmwareDownloadTag = GetValue(args, ref i, arg, options.Errors);
                        if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadTag))
                        {
                            options.FirmwareDownloadTagDirectory = GetValue(args, ref i, arg, options.Errors);
                        }

                        break;
                    }
                    case "--fw-update-hex":
                        options.FirmwareHexPath = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--fw-update-latest":
                        options.FirmwareUpdateLatestDirectory = GetValue(args, ref i, arg, options.Errors);
                        break;
                    case "--lan-chip-info":
                        options.LanChipInfo = true;
                        break;
                    case "--diagnostics":
                        options.Diagnostics = true;
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

            var firmwareCommandCount = 0;
            if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadLatestDirectory))
            {
                firmwareCommandCount++;
            }

            if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadTag))
            {
                firmwareCommandCount++;
            }

            if (!string.IsNullOrWhiteSpace(options.FirmwareHexPath))
            {
                firmwareCommandCount++;
            }

            if (!string.IsNullOrWhiteSpace(options.FirmwareUpdateLatestDirectory))
            {
                firmwareCommandCount++;
            }

            if (firmwareCommandCount > 1)
            {
                options.Errors.Add("Specify only one firmware command at a time.");
            }

            // SD card device operations are dispatched via a single if/else-if chain in
            // RunSdCardOperationAsync, so only one can actually run. Reject conflicting flags
            // up front instead of silently ignoring all but the first.
            var sdCommandCount = 0;
            if (options.SdStorage)
            {
                sdCommandCount++;
            }

            if (options.SdList)
            {
                sdCommandCount++;
            }

            if (options.SdLogStart)
            {
                sdCommandCount++;
            }

            if (options.SdLogStop)
            {
                sdCommandCount++;
            }

            if (options.SdFormat)
            {
                sdCommandCount++;
            }

            if (options.SdDeleteFileName != null)
            {
                sdCommandCount++;
            }

            if (options.SdDownloadFileName != null)
            {
                sdCommandCount++;
            }

            if (sdCommandCount > 1)
            {
                options.Errors.Add("Specify only one SD card command at a time.");
            }

            if (!string.IsNullOrWhiteSpace(options.FirmwareDownloadTag) &&
                string.IsNullOrWhiteSpace(options.FirmwareDownloadTagDirectory))
            {
                options.Errors.Add("Missing destination directory for --fw-download-tag.");
            }

            if (!string.IsNullOrWhiteSpace(options.SdExportCsvPath) &&
                string.IsNullOrWhiteSpace(options.SdParsePath))
            {
                options.Errors.Add("--sd-export-csv requires --sd-parse <path>.");
            }

            if (!string.IsNullOrWhiteSpace(options.SdDownloadDestination) &&
                string.IsNullOrWhiteSpace(options.SdDownloadFileName))
            {
                options.Errors.Add("--sd-download-to requires --sd-download <filename>.");
            }

            if (options.Overwrite && string.IsNullOrWhiteSpace(options.SdDownloadFileName))
            {
                options.Errors.Add("--overwrite requires --sd-download <filename>.");
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

        private static SdCardLogFormat ParseSdLogFormat(string? value, List<string> errors)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "protobuf" or "bin" => SdCardLogFormat.Protobuf,
                "json" => SdCardLogFormat.Json,
                "csv" => SdCardLogFormat.Csv,
                _ => AddSdLogFormatError(errors, value)
            };
        }

        private static SdCardLogFormat AddSdLogFormatError(List<string> errors, string? value)
        {
            errors.Add($"Invalid SD log format: {value}. Use protobuf, json, or csv.");
            return SdCardLogFormat.Protobuf;
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
