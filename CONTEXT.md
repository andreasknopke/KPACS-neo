# K-PACS.neo — Domain Context

Shared vocabulary for the K-PACS.neo DICOM imaging workstation. Names here are
load-bearing: use them exactly in code, reviews, and design discussion.

## ROI / auto-outline (the draft pipeline)

The path from a user click on a slice to a finalized 3D segmentation mask.

- **`OutlineEngine`** — the stateful ROI-draft orchestrator. Owns the current
  `VolumeRoiDraft`, exposes the transitions (`AddContour`, `ToggleAdditive`,
  `ChangeSensitivity`, `TryComplete`, `RestoreDraft`), and produces a
  `SegmentationMask3D` on finalize. Avalonia-free: it never touches `Dispatcher`;
  the panel marshals results back to the UI thread. Lives in
  `KPACS.Viewer.Avalonia/RoiDraft/`.
- **`VolumeRoiDraft`** — the in-progress 3D ROI the user is drawing: a set of
  per-slice `VolumeRoiDraftContour`s, additive-mode state, sensitivity, and the
  carried `SegmentationMask3D`. Owned by `OutlineEngine`; the panel reaches it
  only through the engine (including undo/redo, via `RestoreDraft`).
- **`VolumeRoiDraftContour`** — one closed contour on one slice, with its plane
  geometry (`PlaneOrigin`/`RowDirection`/`ColumnDirection`/`Normal`) and anchors.
- **`AutoOutlineMath`** — the internal static grid→contour seam (marching squares,
  flood-fill, tolerance, seed-connected retention, slice-propagation geometry).
  Pure: a `bool[,]` / voxel grid in, contours out. Tested at the finest grain.
- **`AutoOutlineSliceSource`** — the voxel-read seam: a decoded slice
  (`Width`, `Height`, `Pixels`, `Modality`). The engine consumes only this; the
  panel's bit-depth pixel reader is a factory that produces it. Replaces the old
  duplicate `(int seedX, int seedY)` overloads.

## Outputs the ROI pipeline produces

- **`SegmentationMask3D`** — a finalized 3D mask: `VolumeGridGeometry` + bit-packed
  `SegmentationMaskStorage` + metadata. The shared representation between the
  auto-outline path, the TotalSegmentator plugin, and the ROI→contour converter.
- **`VolumeRoiContour`** — a per-slice output contour (`MeasurementAnchor[]` +
  slice key), the form the viewer overlays and persists.
