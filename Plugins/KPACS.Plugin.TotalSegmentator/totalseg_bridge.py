"""
TotalSegmentator Bridge — wraps the TotalSegmentator Python API for the K-PACS
plugin server.  Handles invocation, multilabel parsing, volume/bounding-box
computation, and structure metadata resolution.
"""

from __future__ import annotations

import logging
import os
import re
from pathlib import Path
from typing import Any

import numpy as np

logger = logging.getLogger("kpacs.plugin.totalsegmentator.bridge")

# ---------------------------------------------------------------------------
#  Lazy imports — TotalSegmentator is heavy (PyTorch, nnU-Net).  Import on
#  first use so the gRPC server boots fast.
# ---------------------------------------------------------------------------
_totalseg_api = None
_class_map: dict[str, dict[int, str]] | None = None


def _ensure_totalseg():
    """Import TotalSegmentator on demand and cache the module handle."""
    global _totalseg_api, _class_map
    if _totalseg_api is not None:
        return

    try:
        from totalsegmentator.python_api import totalsegmentator as _api  # noqa: N811
        _totalseg_api = _api
    except ImportError as exc:
        raise RuntimeError(
            "TotalSegmentator is not installed.  "
            "Run:  pip install TotalSegmentator"
        ) from exc

    try:
        from totalsegmentator.map_to_binary import class_map as _cm
        _class_map = _cm
    except ImportError:
        logger.warning("Could not import class_map — structure labels will use fallback logic.")
        _class_map = {}


# ---------------------------------------------------------------------------
#  Display-name / region metadata
# ---------------------------------------------------------------------------
#  Explicit overrides for the most important structures.  Everything else
#  is derived automatically from the snake_case identifier.
# ---------------------------------------------------------------------------
_DISPLAY_OVERRIDES: dict[str, tuple[str, str]] = {
    # ── Organs ────────────────────────────────────────────────────
    "spleen":                       ("Spleen",                       "Upper Abdomen"),
    "kidney_right":                 ("Right Kidney",                 "Retroperitoneum"),
    "kidney_left":                  ("Left Kidney",                  "Retroperitoneum"),
    "gallbladder":                  ("Gallbladder",                  "Upper Abdomen"),
    "liver":                        ("Liver",                        "Upper Abdomen"),
    "stomach":                      ("Stomach",                      "Upper Abdomen"),
    "pancreas":                     ("Pancreas",                     "Upper Abdomen"),
    "adrenal_gland_right":          ("Right Adrenal Gland",          "Retroperitoneum"),
    "adrenal_gland_left":           ("Left Adrenal Gland",           "Retroperitoneum"),
    "duodenum":                     ("Duodenum",                     "Upper Abdomen"),
    "small_bowel":                  ("Small Bowel",                  "Lower Abdomen"),
    "colon":                        ("Colon",                        "Lower Abdomen"),
    "urinary_bladder":              ("Urinary Bladder",              "Pelvis"),
    "prostate":                     ("Prostate",                     "Pelvis"),
    "kidney_cyst_left":             ("Left Kidney Cyst",             "Retroperitoneum"),
    "kidney_cyst_right":            ("Right Kidney Cyst",            "Retroperitoneum"),
    "esophagus":                    ("Esophagus",                    "Thorax"),
    "trachea":                      ("Trachea",                      "Thorax"),
    "thyroid_gland":                ("Thyroid Gland",                "Head & Neck"),
    "brain":                        ("Brain",                        "Head & Neck"),
    "skull":                        ("Skull",                        "Head & Neck"),
    # ── Heart & great vessels ─────────────────────────────────────
    "heart":                        ("Heart",                        "Thorax"),
    "aorta":                        ("Aorta",                        "Thorax"),
    "pulmonary_vein":               ("Pulmonary Vein",               "Thorax"),
    "brachiocephalic_trunk":        ("Brachiocephalic Trunk",        "Thorax"),
    "subclavian_artery_right":      ("Right Subclavian Artery",      "Thorax"),
    "subclavian_artery_left":       ("Left Subclavian Artery",       "Thorax"),
    "common_carotid_artery_right":  ("Right Common Carotid Artery",  "Head & Neck"),
    "common_carotid_artery_left":   ("Left Common Carotid Artery",   "Head & Neck"),
    "brachiocephalic_vein_left":    ("Left Brachiocephalic Vein",    "Thorax"),
    "brachiocephalic_vein_right":   ("Right Brachiocephalic Vein",   "Thorax"),
    "atrial_appendage_left":        ("Left Atrial Appendage",        "Thorax"),
    "superior_vena_cava":           ("Superior Vena Cava",           "Thorax"),
    "inferior_vena_cava":           ("Inferior Vena Cava",           "Upper Abdomen"),
    "portal_vein_and_splenic_vein": ("Portal & Splenic Vein",        "Upper Abdomen"),
    "iliac_artery_left":            ("Left Iliac Artery",            "Pelvis"),
    "iliac_artery_right":           ("Right Iliac Artery",           "Pelvis"),
    "iliac_vena_left":              ("Left Iliac Vein",              "Pelvis"),
    "iliac_vena_right":             ("Right Iliac Vein",             "Pelvis"),
    # ── Spinal structures ────────────────────────────────────────
    "sacrum":                       ("Sacrum",                       "Spine"),
    "spinal_cord":                  ("Spinal Cord",                  "Spine"),
    # ── Musculoskeletal ──────────────────────────────────────────
    "humerus_left":                 ("Left Humerus",                 "Extremities"),
    "humerus_right":                ("Right Humerus",                "Extremities"),
    "scapula_left":                 ("Left Scapula",                 "Thorax"),
    "scapula_right":                ("Right Scapula",                "Thorax"),
    "clavicula_left":               ("Left Clavicle",                "Thorax"),
    "clavicula_right":              ("Right Clavicle",               "Thorax"),
    "femur_left":                   ("Left Femur",                   "Extremities"),
    "femur_right":                  ("Right Femur",                  "Extremities"),
    "hip_left":                     ("Left Hip",                     "Pelvis"),
    "hip_right":                    ("Right Hip",                    "Pelvis"),
    # ── Muscles ──────────────────────────────────────────────────
    "gluteus_maximus_left":         ("Left Gluteus Maximus",         "Pelvis"),
    "gluteus_maximus_right":        ("Right Gluteus Maximus",        "Pelvis"),
    "gluteus_medius_left":          ("Left Gluteus Medius",          "Pelvis"),
    "gluteus_medius_right":         ("Right Gluteus Medius",         "Pelvis"),
    "gluteus_minimus_left":         ("Left Gluteus Minimus",         "Pelvis"),
    "gluteus_minimus_right":        ("Right Gluteus Minimus",        "Pelvis"),
    "autochthon_left":              ("Left Autochthonous Back M.",   "Spine"),
    "autochthon_right":             ("Right Autochthonous Back M.",  "Spine"),
    "iliopsoas_left":               ("Left Iliopsoas",               "Pelvis"),
    "iliopsoas_right":              ("Right Iliopsoas",              "Pelvis"),
}

# Regex patterns for region inference when no explicit override exists.
_REGION_PATTERNS: list[tuple[re.Pattern[str], str]] = [
    (re.compile(r"vertebra|spinal|sacrum|autochthon", re.I),           "Spine"),
    (re.compile(r"rib|lung|heart|aort|pulmon|trachea|esophag|scapula|clavicul|brachiocephalic|subclavian|vena_cava_sup|atrial", re.I), "Thorax"),
    (re.compile(r"brain|skull|thyroid|carotid", re.I),                 "Head & Neck"),
    (re.compile(r"liver|gallbladder|spleen|pancreas|stomach|duodenum|adrenal|kidney|inferior_vena|portal_vein", re.I), "Upper Abdomen"),
    (re.compile(r"bowel|colon|cecum|sigmoid|rectum|appendix", re.I),   "Lower Abdomen"),
    (re.compile(r"bladder|prostate|uterus|iliac|hip|gluteus|iliopsoas|sacrum|pelvi", re.I), "Pelvis"),
    (re.compile(r"humerus|femur|tibia|fibula|patella|radius|ulna", re.I), "Extremities"),
]


def _infer_display_name(structure_id: str) -> str:
    """Convert a snake_case structure id to a human-readable display name."""
    parts = structure_id.split("_")

    # Handle laterality (e.g. "kidney_left" → "Left Kidney")
    if parts[-1] in ("left", "right"):
        side = "Left" if parts[-1] == "left" else "Right"
        body = " ".join(p.capitalize() for p in parts[:-1])
        return f"{side} {body}"

    # Vertebrae shorthand  (e.g. "vertebrae_T12" → "T12")
    if len(parts) == 2 and parts[0] == "vertebrae":
        return parts[1].upper()

    # Rib naming (e.g. "rib_left_4" → "Left Rib 4")
    if parts[0] == "rib" and len(parts) >= 3:
        side = "Left" if parts[1] == "left" else "Right"
        number = parts[2]
        return f"{side} Rib {number}"

    return " ".join(p.capitalize() for p in parts)


def _infer_region(structure_id: str) -> str:
    """Guess the anatomy region from the structure identifier."""
    for pattern, region in _REGION_PATTERNS:
        if pattern.search(structure_id):
            return region
    return "Other"


def _get_metadata(structure_id: str) -> tuple[str, str]:
    """Return (display_name, region) for a structure, using overrides or inference."""
    override = _DISPLAY_OVERRIDES.get(structure_id)
    if override is not None:
        return override
    return _infer_display_name(structure_id), _infer_region(structure_id)


# ---------------------------------------------------------------------------
#  Bridge class
# ---------------------------------------------------------------------------

class TotalSegBridge:
    """Thin wrapper around TotalSegmentator's Python API."""

    def run_segmentation(
        self,
        input_path: str,
        output_dir: str,
        task: str = "total",
        device: str = "gpu",
        multilabel: bool = True,
        roi_subset: list[str] | None = None,
    ) -> dict[str, Any]:
        """
        Run TotalSegmentator on a volume.

        Parameters
        ----------
        input_path : str
            Path to a DICOM directory or a NIfTI file.
        output_dir : str
            Directory where per-structure NIfTI masks will be written.
        task : str
            TotalSegmentator task name (e.g. "total", "total_fast", "lung_vessels").
        device : str
            "gpu", "cpu", or "gpu:N".
        multilabel : bool
            Whether to also write a single multilabel NIfTI volume.
        roi_subset : list[str] | None
            If given, restrict output to these structure names.

        Returns
        -------
        dict with "multilabel_path" (str | None) key.
        """
        _ensure_totalseg()

        os.makedirs(output_dir, exist_ok=True)

        fast = task.endswith("_fast") or task.endswith("_fastest")
        actual_task = task.replace("_fast", "").replace("_fastest", "")

        logger.info(
            "Running TotalSegmentator: input=%s task=%s device=%s fast=%s ml=%s",
            input_path, actual_task, device, fast, multilabel,
        )

        _totalseg_api(
            input=input_path,
            output=output_dir,
            ml=multilabel,
            fast=fast,
            task=actual_task,
            device=device,
            roi_subset=roi_subset,
            quiet=True,
            verbose=False,
        )

        multilabel_path: str | None = None
        if multilabel:
            candidate = Path(output_dir) / "segmentations.nii.gz"
            alt_candidate = Path(output_dir) / f"{actual_task}.nii.gz"
            if candidate.exists():
                multilabel_path = str(candidate)
            elif alt_candidate.exists():
                multilabel_path = str(alt_candidate)

        return {"multilabel_path": multilabel_path}

    def parse_results(
        self,
        output_dir: str,
        task: str,
        multilabel_path: str | None = None,
    ) -> list[dict[str, Any]]:
        """
        Parse TotalSegmentator output into per-structure result dicts.

        If a multilabel NIfTI exists, it is loaded once and per-structure
        volumes + bounding boxes are computed from it.  Otherwise,
        individual per-structure NIfTI files in *output_dir* are used.
        """
        _ensure_totalseg()

        # Build label → name and name → label mappings from TotalSegmentator's class_map.
        # class_map format is: task_id → {label_int: structure_name_str}
        actual_task = task.replace("_fast", "").replace("_fastest", "")
        label_to_name: dict[int, str] = {}
        name_to_label: dict[str, int] = {}

        if _class_map and actual_task in _class_map:
            label_to_name = _class_map[actual_task]
            name_to_label = {name: label for label, name in label_to_name.items()}
        else:
            logger.warning("No class_map entry for task '%s' — scanning output dir.", actual_task)

        structures: list[dict[str, Any]] = []

        if multilabel_path and Path(multilabel_path).exists():
            structures = self._parse_multilabel(multilabel_path, label_to_name)
        else:
            structures = self._parse_individual_masks(output_dir, name_to_label)

        return structures

    def get_task_catalog(self) -> list[dict[str, Any]]:
        """Return a list of task descriptions with full structure catalogues."""
        _ensure_totalseg()

        catalog: list[dict[str, Any]] = []

        if not _class_map:
            return catalog

        # Manually curated task metadata
        task_meta: dict[str, dict[str, Any]] = {
            "total": {
                "name": "Total (104 structures, CT)",
                "description": "Full-body CT segmentation: organs, vertebrae, ribs, muscles, vessels at 1.5 mm resolution.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "total_mr": {
                "name": "Total MR (104 structures)",
                "description": "Full-body MR segmentation at 1.5 mm resolution.",
                "modalities": ["MR"],
                "requires_license": False,
            },
            "lung_vessels": {
                "name": "Lung Vessels",
                "description": "Lung airways, airway walls, arteries, and veins.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "heartchambers_highres": {
                "name": "Heart Chambers (High-Res)",
                "description": "High-resolution cardiac chamber segmentation.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "cerebral_bleed": {
                "name": "Cerebral Bleed",
                "description": "Intracerebral hemorrhage detection and segmentation.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "coronary_arteries": {
                "name": "Coronary Arteries",
                "description": "Coronary artery segmentation from CTA.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "body": {
                "name": "Body Region",
                "description": "Body trunk and extremity segmentation.",
                "modalities": ["CT"],
                "requires_license": False,
            },
            "tissue_types": {
                "name": "Tissue Types",
                "description": "Subcutaneous fat, muscle, visceral fat, bone, etc.",
                "modalities": ["CT"],
                "requires_license": True,
            },
        }

        for task_id, label_to_name in _class_map.items():
            meta = task_meta.get(task_id, {})
            structure_entries: list[dict[str, Any]] = []

            for label_id, struct_name in sorted(label_to_name.items()):
                display_name, region = _get_metadata(struct_name)
                structure_entries.append({
                    "label": label_id,
                    "id": struct_name,
                    "display_name": display_name,
                    "region": region,
                })

            catalog.append({
                "id": task_id,
                "name": meta.get("name", task_id),
                "description": meta.get("description", ""),
                "modalities": meta.get("modalities", ["CT"]),
                "structure_count": len(label_to_name),
                "requires_license": meta.get("requires_license", False),
                "structures": structure_entries,
            })

        return catalog

    # ── Private helpers ─────────────────────────────────────────

    @staticmethod
    def _parse_multilabel(
        multilabel_path: str,
        label_to_name: dict[int, str],
    ) -> list[dict[str, Any]]:
        """Extract per-structure metrics from a single multilabel NIfTI."""
        import nibabel as nib

        nii = nib.load(multilabel_path)
        data: np.ndarray = np.asarray(nii.dataobj, dtype=np.int32)
        zooms = nii.header.get_zooms()
        voxel_vol = float(zooms[0]) * float(zooms[1]) * float(zooms[2])

        present_labels = set(np.unique(data)) - {0}

        structures: list[dict[str, Any]] = []
        for label_id in sorted(present_labels):
            name = label_to_name.get(label_id, f"label_{label_id}")
            display_name, region = _get_metadata(name)

            mask = data == label_id
            volume_mm3 = float(mask.sum()) * voxel_vol

            coords = np.argwhere(mask)
            bbox_min = coords.min(axis=0).tolist()
            bbox_max = coords.max(axis=0).tolist()

            structures.append({
                "label": int(label_id),
                "id": name,
                "display_name": display_name,
                "region": region,
                "mask_path": "",  # no individual mask when using multilabel
                "volume_mm3": volume_mm3,
                "bounding_box": bbox_min + bbox_max,  # [minX, minY, minZ, maxX, maxY, maxZ]
            })

        return structures

    @staticmethod
    def _parse_individual_masks(
        output_dir: str,
        name_to_label: dict[str, int],
    ) -> list[dict[str, Any]]:
        """Parse per-structure NIfTI files from the output directory."""
        import nibabel as nib

        out = Path(output_dir)
        structures: list[dict[str, Any]] = []

        for nii_file in sorted(out.glob("*.nii.gz")):
            name = nii_file.name.replace(".nii.gz", "")
            if name in ("segmentations", "preview"):
                continue  # skip multilabel & preview files

            label_id = name_to_label.get(name, 0)
            display_name, region = _get_metadata(name)

            try:
                nii = nib.load(str(nii_file))
                data: np.ndarray = np.asarray(nii.dataobj, dtype=np.int32)
                zooms = nii.header.get_zooms()
                voxel_vol = float(zooms[0]) * float(zooms[1]) * float(zooms[2])

                mask = data > 0
                if not mask.any():
                    continue

                volume_mm3 = float(mask.sum()) * voxel_vol
                coords = np.argwhere(mask)
                bbox_min = coords.min(axis=0).tolist()
                bbox_max = coords.max(axis=0).tolist()

                structures.append({
                    "label": label_id,
                    "id": name,
                    "display_name": display_name,
                    "region": region,
                    "mask_path": str(nii_file),
                    "volume_mm3": volume_mm3,
                    "bounding_box": bbox_min + bbox_max,
                })
            except Exception:
                logger.exception("Failed to parse mask file: %s", nii_file)
                continue

        return structures
