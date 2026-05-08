---
name: daqifi-hil-validation
description: Run DAQiFi hardware-in-the-loop validation for daqifi-core releases using the daqifi-core-example-app CLI, with the CLI exit codes and explicit thresholds as the pass/fail authority.
---

# DAQiFi HIL Validation

Use this skill when validating local `daqifi-core` changes against a real DAQiFi device before a release. The CLI in this repository is the only pass/fail source of truth. The agent's job is to build the CLI against the local core project, run the configured smoke checks, preserve artifacts, and summarize the result.

Do not build a separate runner or duplicate DAQiFi device logic. Do not add CI. Do not run destructive operations unless the operator explicitly opted in through the environment variables below and the current task asks for them.

## Configuration

Set variables in the shell, or place exports in a local ignored file named `.daqifi-hil.env` and source it before running validation:

```bash
set -a
source ./.daqifi-hil.env
set +a
```

Required for validating local `daqifi-core` sources:

- `DAQIFI_CORE_PATH`: path to `Daqifi.Core.csproj`. Use `/Users/tylerkron/projects/daqifi/daqifi-core/src/Daqifi.Core/Daqifi.Core.csproj` unless the user provides a different checkout.

Optional:

- `DAQIFI_CLI_APP_PATH`: path to this CLI repo. Defaults to the current repository root.
- `DAQIFI_HIL_IP`: DAQiFi WiFi/TCP address. Enables WiFi discovery and streaming checks when set.
- `DAQIFI_HIL_PORT`: DAQiFi TCP port. Defaults to `9760`.
- `DAQIFI_HIL_SERIAL`: serial port, for example `/dev/cu.usbmodem101`. Enables serial discovery, serial streaming, LAN chip info, and SD list checks when set.
- `DAQIFI_HIL_BAUD`: serial baud rate. Defaults to `115200` for this validation workflow.
- `DAQIFI_HIL_RATE`: stream sampling rate in Hz. Defaults to `10`.
- `DAQIFI_HIL_CHANNELS`: decimal ADC channel bitmask. Defaults to `3`.
- `DAQIFI_HIL_MIN_SAMPLES`: required minimum stream samples. Defaults to `5`.
- `DAQIFI_HIL_ALLOW_SD_MUTATION`: must be `true` before running SD logging, delete, or format commands. Defaults to `false`.
- `DAQIFI_HIL_ALLOW_FIRMWARE_FLASH`: must be `true` before running firmware update commands. Defaults to `false`.

Example `.daqifi-hil.env`:

```bash
export DAQIFI_CORE_PATH=/Users/tylerkron/projects/daqifi/daqifi-core/src/Daqifi.Core/Daqifi.Core.csproj
export DAQIFI_CLI_APP_PATH=/Users/tylerkron/projects/daqifi/core-and-desktop/daqifi-core-example-app
export DAQIFI_HIL_IP=192.168.1.44
export DAQIFI_HIL_PORT=9760
export DAQIFI_HIL_SERIAL=/dev/cu.usbmodem101
export DAQIFI_HIL_BAUD=115200
export DAQIFI_HIL_RATE=10
export DAQIFI_HIL_CHANNELS=3
export DAQIFI_HIL_MIN_SAMPLES=5
export DAQIFI_HIL_ALLOW_SD_MUTATION=false
export DAQIFI_HIL_ALLOW_FIRMWARE_FLASH=false
```

## Artifact Rules

Create one timestamped artifact directory per run:

```bash
CLI_DIR="${DAQIFI_CLI_APP_PATH:-$PWD}"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
ARTIFACT_DIR="$CLI_DIR/artifacts/daqifi-hil/$STAMP"
mkdir -p "$ARTIFACT_DIR"
cd "$CLI_DIR"
```

For every command, save:

- raw stdout/stderr in a named or numbered `.log` file
- the exact exit code in `exit-codes.tsv`
- any JSONL stream output in the same artifact directory
- a final `summary.md`

Record skipped checks explicitly in the summary. Never hide a non-zero exit code because another check passed.

## Exit Codes

Interpret CLI exit codes exactly:

- `0`: pass
- `1`: command, connection, or runtime error
- `2`: validation threshold failed, such as `--min-samples` not being met

`dotnet build` uses standard .NET exit codes; a non-zero build result fails the validation run before hardware confidence can be claimed.

## Default Smoke Sequence

Run commands from `DAQIFI_CLI_APP_PATH` if set; otherwise run from this repository root. Always pass `-p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH"` to validate local `daqifi-core` sources.

Before hardware commands, require `DAQIFI_CORE_PATH` to be set and create the artifact directory.

1. Build the CLI against local `daqifi-core`.

```bash
dotnet build -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH"
```

2. If `DAQIFI_HIL_IP` is set, run WiFi discovery.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- --discover --discover-timeout 5
```

3. If `DAQIFI_HIL_SERIAL` is set, run serial discovery.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- --discover-serial --discover-timeout 30
```

4. If `DAQIFI_HIL_IP` is set, stream over WiFi to JSONL with the configured threshold.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- \
  --ip "$DAQIFI_HIL_IP" \
  --port "${DAQIFI_HIL_PORT:-9760}" \
  --rate "${DAQIFI_HIL_RATE:-10}" \
  --channels "${DAQIFI_HIL_CHANNELS:-3}" \
  --duration 10 \
  --min-samples "${DAQIFI_HIL_MIN_SAMPLES:-5}" \
  --format jsonl \
  --output "$ARTIFACT_DIR/wifi-stream.jsonl"
```

5. If `DAQIFI_HIL_SERIAL` is set, stream over serial to JSONL with the configured threshold.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- \
  --serial "$DAQIFI_HIL_SERIAL" \
  --baud "${DAQIFI_HIL_BAUD:-115200}" \
  --rate "${DAQIFI_HIL_RATE:-10}" \
  --channels "${DAQIFI_HIL_CHANNELS:-3}" \
  --duration 10 \
  --min-samples "${DAQIFI_HIL_MIN_SAMPLES:-5}" \
  --format jsonl \
  --output "$ARTIFACT_DIR/serial-stream.jsonl"
```

6. If `DAQIFI_HIL_SERIAL` is set, query LAN chip info over serial.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- \
  --serial "$DAQIFI_HIL_SERIAL" \
  --baud "${DAQIFI_HIL_BAUD:-115200}" \
  --lan-chip-info
```

7. If `DAQIFI_HIL_SERIAL` is set, list SD card files over serial. This is read-only.

```bash
dotnet run -p:DaqifiCoreProjectPath="$DAQIFI_CORE_PATH" -- \
  --serial "$DAQIFI_HIL_SERIAL" \
  --baud "${DAQIFI_HIL_BAUD:-115200}" \
  --sd-list
```

8. Optional SD mutation: only run SD logging, delete, or format commands when `DAQIFI_HIL_ALLOW_SD_MUTATION=true` and the user explicitly requested that coverage for the current run. Never run `--sd-delete` or `--sd-format` in the default smoke sequence.

9. Optional firmware flashing: only run `--fw-update-hex` or `--fw-update-latest` when `DAQIFI_HIL_ALLOW_FIRMWARE_FLASH=true` and the user explicitly requested firmware-flash validation for the current run.

## Agent Procedure

Use the smallest amount of shell glue needed to preserve artifacts. A typical manual pattern is:

```bash
run_step() {
  name="$1"
  shift
  log="$ARTIFACT_DIR/$name.log"
  printf '$ %s\n' "$*" > "$log"
  "$@" >> "$log" 2>&1
  code=$?
  printf '%s\t%s\t%s\n' "$name" "$code" "$log" >> "$ARTIFACT_DIR/exit-codes.tsv"
  return "$code"
}
```

The agent may continue after a non-zero hardware check to collect independent evidence, but the final summary must report the overall run as failed if any required check returned non-zero.

## Final Summary

Write `summary.md` in the artifact directory and include:

- repo path, local core path, UTC timestamp, and device env vars used with secrets omitted
- each check name, command log path, exit code, and pass/fail/skipped status
- JSONL artifact paths and sample counts if available
- destructive permissions observed (`DAQIFI_HIL_ALLOW_SD_MUTATION`, `DAQIFI_HIL_ALLOW_FIRMWARE_FLASH`)
- final release-readiness result: `PASS`, `FAIL`, or `NOT RUN - hardware not configured`

In the chat response, cite the artifact directory and summarize changed files or validation performed. Do not claim hardware validation passed unless the required hardware env vars were configured and the CLI commands returned exit code `0`.
