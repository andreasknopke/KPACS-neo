#!/usr/bin/env python3
"""
K-PACS Plugin · TotalSegmentator
─────────────────────────────────
gRPC server implementing the K-PACS plugin protocol for TotalSegmentator.

Usage (typically launched by K-PACS PluginManager):
    python server.py --port 0

The server prints ``KPACS_PLUGIN_PORT=<port>`` to stdout so the host knows
which port the gRPC channel should connect to.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sys
import time
from concurrent import futures
from pathlib import Path

import grpc

# Generated stubs — run ``python generate_proto.py`` to refresh them.
import plugin_service_pb2 as pb2
import plugin_service_pb2_grpc as pb2_grpc

from totalseg_bridge import TotalSegBridge

logger = logging.getLogger("kpacs.plugin.totalsegmentator")

# PluginCapability flag values (must match KPACS.SDK.PluginCapability enum).
_CAP_SEGMENTATION = 1
_CAP_IMAGE_PROCESSING = 2
_CAP_DICOM_ANALYSIS = 4
_CAP_DICOM_COMMUNICATION = 8

_CAPABILITY_MAP: dict[str, int] = {
    "Segmentation": _CAP_SEGMENTATION,
    "ImageProcessing": _CAP_IMAGE_PROCESSING,
    "DicomAnalysis": _CAP_DICOM_ANALYSIS,
    "DicomCommunication": _CAP_DICOM_COMMUNICATION,
}


def _parse_capabilities(value: str | list[str]) -> int:
    """Turn the manifest *capabilities* field into a bitmask."""
    flags = 0
    if isinstance(value, str):
        # Comma- or pipe-separated, or a single value
        tokens = [t.strip() for t in value.replace("|", ",").split(",")]
    elif isinstance(value, list):
        tokens = value
    else:
        return 0

    for token in tokens:
        flags |= _CAPABILITY_MAP.get(token, 0)
    return flags


# ═══════════════════════════════════════════════════════════════════
#  gRPC Servicer
# ═══════════════════════════════════════════════════════════════════

class TotalSegmentatorServicer(pb2_grpc.PluginServiceServicer):
    """Implements the K-PACS PluginService gRPC protocol."""

    def __init__(self, manifest_path: str):
        with open(manifest_path, encoding="utf-8") as fh:
            self._manifest: dict = json.load(fh)

        self._bridge = TotalSegBridge()
        self._initialized = False
        self._scratch_dir: str | None = None
        self._data_dir: str | None = None

    # ── Lifecycle ────────────────────────────────────────────────

    def GetManifest(self, request, context):
        m = self._manifest
        return pb2.PluginManifestMsg(
            id=m["id"],
            name=m["name"],
            version=m["version"],
            author=m.get("author", ""),
            description=m.get("description", ""),
            license=m.get("license", ""),
            capabilities=_parse_capabilities(m.get("capabilities", "")),
        )

    def Initialize(self, request, context):
        self._scratch_dir = request.scratch_directory
        self._data_dir = request.data_directory
        self._initialized = True

        # Log GPU availability so we can diagnose device issues early.
        try:
            import torch
            if torch.cuda.is_available():
                gpu_name = torch.cuda.get_device_name(0)
                vram_gb = torch.cuda.get_device_properties(0).total_mem / (1024**3)
                logger.info("PyTorch %s — CUDA device: %s (%.1f GB VRAM)", torch.__version__, gpu_name, vram_gb)
            else:
                logger.warning("PyTorch %s — NO CUDA available, inference will run on CPU!", torch.__version__)
        except Exception:
            pass

        logger.info(
            "Initialized — scratch=%s  data=%s  host=%s",
            self._scratch_dir,
            self._data_dir,
            request.host_version,
        )
        return pb2.InitializeResponse(ok=True)

    def Shutdown(self, request, context):
        logger.info("Shutdown requested — cleaning up.")
        self._initialized = False
        return pb2.ShutdownResponse()

    # ── Segmentation ─────────────────────────────────────────────

    def RunSegmentation(self, request, context):
        """
        Main entry point.  Runs TotalSegmentator on the provided volume
        and streams ``SegmentationEvent`` messages back to the host:

        1. Progress updates  (step / total / percent / status)
        2. Per-structure results  (label, id, display name, volume, bbox)
        3. Completion event  (multilabel path, elapsed time)

        On error, a single ``SegError`` event is yielded.
        """
        if not self._initialized:
            yield pb2.SegmentationEvent(
                error=pb2.SegError(message="Plugin not initialized — call Initialize first.")
            )
            return

        task_id = request.task_id
        volume_path = request.volume.file_path
        output_dir = request.output_directory
        device = request.device or "gpu"
        multilabel = request.produce_multilabel
        roi_subset = list(request.roi_subset) or None

        total_steps = 4

        # ── Step 0: Preparing ────────────────────────────────────
        yield pb2.SegmentationEvent(
            progress=pb2.SegProgressUpdate(
                step=0,
                total_steps=total_steps,
                percent_complete=0,
                status_message="Preparing TotalSegmentator…",
            )
        )

        start_time = time.monotonic()

        try:
            # ── Step 1: Inference ────────────────────────────────
            yield pb2.SegmentationEvent(
                progress=pb2.SegProgressUpdate(
                    step=1,
                    total_steps=total_steps,
                    percent_complete=10,
                    status_message=f"Running '{task_id}' segmentation on {device}…",
                )
            )

            result = self._bridge.run_segmentation(
                input_path=volume_path,
                output_dir=output_dir,
                task=task_id,
                device=device,
                multilabel=multilabel,
                roi_subset=roi_subset,
            )

            # ── Step 2: Parsing ──────────────────────────────────
            yield pb2.SegmentationEvent(
                progress=pb2.SegProgressUpdate(
                    step=2,
                    total_steps=total_steps,
                    percent_complete=70,
                    status_message="Analyzing segmentation output…",
                )
            )

            structures = self._bridge.parse_results(
                output_dir=output_dir,
                task=task_id,
                multilabel_path=result.get("multilabel_path"),
            )

            # ── Step 3: Streaming structures ─────────────────────
            yield pb2.SegmentationEvent(
                progress=pb2.SegProgressUpdate(
                    step=3,
                    total_steps=total_steps,
                    percent_complete=85,
                    status_message=f"Streaming {len(structures)} structure(s)…",
                )
            )

            for s in structures:
                if context.is_active():
                    yield pb2.SegmentationEvent(
                        structure=pb2.SegStructureResult(
                            label=s["label"],
                            id=s["id"],
                            display_name=s.get("display_name", ""),
                            region=s.get("region", ""),
                            mask_path=s.get("mask_path", ""),
                            volume_mm3=s.get("volume_mm3", -1.0),
                            bounding_box_voxels=s.get("bounding_box", []),
                        )
                    )
                else:
                    logger.warning("Client cancelled — aborting structure stream.")
                    return

            # ── Complete ─────────────────────────────────────────
            elapsed = time.monotonic() - start_time

            yield pb2.SegmentationEvent(
                complete=pb2.SegComplete(
                    multilabel_path=result.get("multilabel_path", ""),
                    elapsed_seconds=elapsed,
                )
            )

            logger.info(
                "Segmentation '%s' completed in %.1f s — %d structures.",
                task_id,
                elapsed,
                len(structures),
            )

        except Exception as exc:
            import traceback as _tb
            tb_text = _tb.format_exc()
            logger.exception("Segmentation failed for task '%s'", task_id)
            yield pb2.SegmentationEvent(
                error=pb2.SegError(message=f"{exc}\n\n{tb_text}")
            )

    def GetSegmentationTasks(self, request, context):
        tasks = self._bridge.get_task_catalog()
        return pb2.GetSegTasksResponse(
            tasks=[
                pb2.SegTaskInfo(
                    id=t["id"],
                    name=t["name"],
                    description=t.get("description", ""),
                    modalities=t.get("modalities", []),
                    structure_count=t.get("structure_count", 0),
                    requires_license=t.get("requires_license", False),
                    structures=[
                        pb2.SegStructureEntry(
                            label=s["label"],
                            id=s["id"],
                            display_name=s.get("display_name", ""),
                            region=s.get("region", ""),
                        )
                        for s in t.get("structures", [])
                    ],
                )
                for t in tasks
            ]
        )

    # ── Unimplemented capabilities ───────────────────────────────

    def ProcessImage(self, request, context):
        context.set_code(grpc.StatusCode.UNIMPLEMENTED)
        context.set_details("TotalSegmentator does not support image processing.")
        return pb2.ImageProcessResponse()

    def GetImageOperations(self, request, context):
        return pb2.GetImageOpsResponse(operations=[])

    def AnalyzeDicom(self, request, context):
        context.set_code(grpc.StatusCode.UNIMPLEMENTED)
        context.set_details("TotalSegmentator does not support DICOM analysis.")
        return pb2.DicomAnalysisResponse()

    def GetDicomAnalyses(self, request, context):
        return pb2.GetDicomAnalysesResponse(analyses=[])


# ═══════════════════════════════════════════════════════════════════
#  Server bootstrap
# ═══════════════════════════════════════════════════════════════════

def serve(port: int, manifest_path: str) -> None:
    """Start the gRPC server and block until termination."""
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
    pb2_grpc.add_PluginServiceServicer_to_server(
        TotalSegmentatorServicer(manifest_path), server
    )

    actual_port: int = server.add_insecure_port(f"[::]:{port}")
    server.start()

    # ── Critical: announce port to the K-PACS host via stdout ────
    # ProcessPluginHost.StartAsync() watches for this line.
    print(f"KPACS_PLUGIN_PORT={actual_port}", flush=True)

    logger.info("TotalSegmentator plugin gRPC server listening on port %d", actual_port)

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        logger.info("Interrupted — stopping server.")
        server.stop(grace=5)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="K-PACS TotalSegmentator Plugin — gRPC server"
    )
    parser.add_argument(
        "--port",
        type=int,
        default=0,
        help="gRPC listening port (0 = let the OS assign a free port)",
    )
    parser.add_argument(
        "--manifest",
        type=str,
        default=None,
        help="Path to plugin.json (default: alongside this script)",
    )
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(name)s] %(levelname)s: %(message)s",
        stream=sys.stderr,  # keep stdout clean for the port announcement
    )

    manifest_path = args.manifest or str(Path(__file__).parent / "plugin.json")
    if not Path(manifest_path).exists():
        logger.error("Manifest not found: %s", manifest_path)
        sys.exit(1)

    serve(args.port, manifest_path)


if __name__ == "__main__":
    main()
