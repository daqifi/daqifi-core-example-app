# DAQiFi Core Example App (CLI)

This is a lightweight CLI that demonstrates how to use `Daqifi.Core` to discover devices, connect, configure channels, stream data, and stop streaming. It is intended both as a reference for third-party developers and as a validation tool for hardware-in-the-loop testing.

## Requirements

- .NET 8 SDK
- A DAQiFi device on the same network

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
