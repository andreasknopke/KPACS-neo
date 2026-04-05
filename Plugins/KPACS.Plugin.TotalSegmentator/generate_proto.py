#!/usr/bin/env python3
"""
Generate Python gRPC stubs from the plugin_service.proto definition.

Usage:
    python generate_proto.py

This produces:
    plugin_service_pb2.py
    plugin_service_pb2_grpc.py
"""

import subprocess
import sys
from pathlib import Path

PROTO_DIR = Path(__file__).parent / "proto"
OUT_DIR = Path(__file__).parent
PROTO_FILE = PROTO_DIR / "plugin_service.proto"


def main():
    if not PROTO_FILE.exists():
        print(f"ERROR: Proto file not found at {PROTO_FILE}", file=sys.stderr)
        sys.exit(1)

    cmd = [
        sys.executable, "-m", "grpc_tools.protoc",
        f"--proto_path={PROTO_DIR}",
        f"--python_out={OUT_DIR}",
        f"--grpc_python_out={OUT_DIR}",
        str(PROTO_FILE),
    ]

    print(f"Running: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)

    if result.returncode != 0:
        print(f"protoc failed:\n{result.stderr}", file=sys.stderr)
        sys.exit(1)

    print("✓ Generated plugin_service_pb2.py and plugin_service_pb2_grpc.py")


if __name__ == "__main__":
    main()
