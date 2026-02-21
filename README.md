# DAQiFi Core Example App (CLI)

This is a lightweight CLI that demonstrates how to use `Daqifi.Core` to discover devices, connect, configure channels, stream data, and stop streaming. It is intended both as a reference for third-party developers and as a validation tool for hardware-in-the-loop testing.

## Requirements

- .NET 8 SDK
- A DAQiFi device reachable via WiFi or USB/Serial
- SD card operations require a USB/Serial connection (not available over WiFi)

## Quick Start

Discover devices:

```bash
dotnet run -- --discover
```

Connect and stream:

```bash
dotnet run -- \
  --ip 192.168.1.44 \
  --port 9760 \
  --rate 10 \
  --channels 0000000011 \
  --duration 10
```

## Options

- `--discover` discover devices over UDP
- `--ip <address>` device IP address
- `--port <number>` TCP port (default 9760)
- `--rate <hz>` sampling rate in Hz (default 100)
- `--duration <seconds>` streaming duration (default 10)
- `--channels <mask>` ADC channel enable mask (0/1 string)
- `--limit <count>` stop after N stream messages
- `--min-samples <count>` require at least N stream messages (exit code 2 on failure)
- `--format <text|csv|jsonl>` output format for samples
- `--output <path>` write samples to file instead of stdout
- `--connect-timeout <seconds>` TCP connect timeout (default 5)
- `--connect-attempts <n>` total connect attempts (default 1)
- `--keep-connected` keep connection open after streaming stops
- `--show-status` print protobuf status messages
- `--discover-timeout <seconds>` discovery timeout (default 5)
- `--sd-list` list files on the SD card (USB/Serial only)
- `--sd-log-start` start SD card logging (USB/Serial only)
- `--sd-log-stop` stop SD card logging (USB/Serial only)
- `--sd-download <filename>` download a file from the SD card and parse it (USB/Serial only)
- `--sd-delete <filename>` delete a file from the SD card (USB/Serial only)
- `--sd-format` format the SD card (USB/Serial only)
- `--fw-update-hex <path>` run PIC32 firmware update from a local Intel HEX file (USB/Serial only)

## SD Card

List files:

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-list
```

Start logging (auto-stop after 5 seconds):

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-log-start --duration 5
```

Stop logging:

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-log-stop
```

Download and parse a file:

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-download log_20260206_101851.bin
```

Delete a file:

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-delete log_20260206_101851.bin
```

Format the SD card:

```bash
dotnet run -- --serial /dev/cu.usbmodem2101 --sd-format
```

## Firmware Update

Run a PIC32 firmware update from a local HEX file:

```bash
dotnet run -p:DaqifiCoreProjectPath=/path/to/daqifi-core/src/Daqifi.Core/Daqifi.Core.csproj -- \
  --serial /dev/cu.usbmodem101 \
  --baud 9600 \
  --fw-update-hex /path/to/DAQiFi_Nyquist.hex
```

The command logs firmware update state transitions and progress to stdout.

## Output Formats

- `text` (default): human-readable line per sample
- `csv`: `timestamp,analog_values,digital_hex`
- `jsonl`: one JSON object per line

## Exit Codes

- `0` success
- `1` error during connect/stream
- `2` validation failed (`--min-samples` not met)

## AI Agents: Validation Workflow

This app is designed to validate `daqifi-core` changes against real hardware. AI agents can run this CLI as a hardware-in-the-loop check after modifying `daqifi-core`.

### Use local `daqifi-core` sources

By default the app references the published NuGet package. To validate local changes, point the build to a local `Daqifi.Core.csproj` using the MSBuild property below.

```bash
dotnet run -p:DaqifiCoreProjectPath=/path/to/daqifi-core/src/Daqifi.Core/Daqifi.Core.csproj -- \
  --ip 192.168.1.44 \
  --port 9760 \
  --rate 10 \
  --channels 0000000011 \
  --duration 10 \
  --min-samples 5
```

### Recommended validation command

```bash
dotnet run -p:DaqifiCoreProjectPath=/path/to/daqifi-core/src/Daqifi.Core/Daqifi.Core.csproj -- \
  --ip 192.168.1.44 \
  --port 9760 \
  --rate 10 \
  --channels 0000000011 \
  --duration 10 \
  --min-samples 5 \
  --format jsonl \
  --output /tmp/daqifi-samples.jsonl
```

The command exits non-zero if it fails to connect/stream or if `--min-samples` is not met.

## Troubleshooting

- If connect times out, confirm the device IP/port and that LAN is enabled.
- If no samples appear, verify channel mask and sampling rate.
