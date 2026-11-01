"""Check SD parse limits without a device.

Build the CLI, then run:
    python3 tests/sd_parse_limit.py bin/Debug/net9.0/Daqifi.Core.Cli.dll
"""

import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile


def check(app, sample_count, limit, expected, output_to_file=False):
    with tempfile.TemporaryDirectory() as directory:
        source = Path(directory) / "log_20260101_120000.json"
        source.write_text("".join(
            json.dumps({"ts": index * 42_000_000, "analog": [index + 0.5], "digital": ""}) + "\n"
            for index in range(sample_count)
        ))
        command = ["dotnet", str(app), "--sd-parse", str(source)]
        if limit is not None:
            command.extend(["--limit", str(limit)])
        destination = Path(directory) / "samples.txt"
        if output_to_file:
            command.extend(["--output", str(destination)])
        result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        assert result.returncode == 0, result.stdout + result.stderr
        sample_output = destination.read_text() if output_to_file else result.stdout
        rows = re.findall(r"\[\d{2}:\d{2}:\d{2}\.\d{3}\] analog=\[([^]]*)\] digital=0x[0-9A-F]+", sample_output, re.MULTILINE)
        totals = re.findall(r"Total samples: (\d+)", result.stdout, re.MULTILINE)
        context = f"source={sample_count}, limit={limit}, file={output_to_file}:\n{result.stdout}"
        assert len(rows) == expected, context
        assert totals == [str(expected)], context
        assert rows == [f"{index + 0.5:.3f}" for index in range(expected)], context


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: python3 tests/sd_parse_limit.py <built CLI .dll>")
    app = Path(sys.argv[1]).resolve()
    cases = [(4, 2, 2), (2, 2, 2), (1, 2, 1), (4, 1, 1), (4, None, 4), (4, 0, 4), (0, 2, 0)]
    for source, limit, expected in cases:
        check(app, source, limit, expected)
    check(app, 4, 2, 2, output_to_file=True)
    print(f"PASS: {len(cases) + 1} SD parse limit checks")


if __name__ == "__main__":
    main()
