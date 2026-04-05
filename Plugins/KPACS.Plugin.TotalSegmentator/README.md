# K-PACS Plugin: TotalSegmentator
# ────────────────────────────────
# Automatic segmentation of 117+ anatomical structures in CT / MR images.
#
# ## Quick-start
#
#   cd Plugins/KPACS.Plugin.TotalSegmentator
#   python -m venv .venv && .venv\Scripts\activate
#   pip install -r requirements.txt
#   python generate_proto.py      # compile gRPC stubs
#   python server.py --port 0     # runs the plugin
#
# The gRPC server prints `KPACS_PLUGIN_PORT=<port>` to stdout.
# K-PACS PluginManager reads this to connect.
#
# ## Structure
#
# - **server.py**          — gRPC server implementing the K-PACS plugin protocol
# - **totalseg_bridge.py** — TotalSegmentator API wrapper + NIfTI result parser
# - **plugin.json**        — Plugin manifest (read by K-PACS PluginManager)
# - **proto/**             — Copy of plugin_service.proto for Python stub gen
# - **generate_proto.py**  — One-shot script to produce _pb2 / _pb2_grpc stubs
# - **requirements.txt**   — Python dependencies
#
# ## GPU requirements
#
# TotalSegmentator requires PyTorch with CUDA support for GPU inference.
# CPU-only mode is supported but much slower.
#
# ## License
#
# TotalSegmentator core tasks (total, lung_vessels, heartchambers, …) are
# Apache-2.0.  Some specialised tasks require a separate licence key.
# See https://github.com/wasserth/TotalSegmentator for details.
