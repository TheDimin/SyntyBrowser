import argparse
import importlib.util
import json
import os
import time
from pathlib import Path


def load_renderer():
    path = Path(__file__).with_name("render-preview.py")
    spec = importlib.util.spec_from_file_location("synty_preview_renderer", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def parse_args():
    import sys

    separator = sys.argv.index("--") if "--" in sys.argv else len(sys.argv)
    parser = argparse.ArgumentParser()
    parser.add_argument("--queue-root", required=True)
    parser.add_argument("--idle-seconds", type=int, default=30)
    parser.add_argument("--resolution", type=int, default=96)
    parser.add_argument("--samples", type=int, default=8)
    return parser.parse_args(sys.argv[separator + 1 :])


def pack_index(pack_root, indexes):
    key = os.path.normcase(os.path.abspath(pack_root))
    if key in indexes:
        return indexes[key]
    index = {}
    for directory, _, files in os.walk(pack_root):
        for filename in files:
            path = os.path.join(directory, filename)
            stem = os.path.splitext(filename)[0]
            index.setdefault(filename.casefold(), []).append(path)
            index.setdefault(stem.casefold(), []).append(path)
    indexes[key] = index
    return index


def resolve_bindings(job, indexes):
    if not job.get("bindings"):
        return []
    index = pack_index(job["pack_root"], indexes)
    resolved = []
    for binding in job.get("bindings", []):
        texture_path = None
        hint = binding.get("texture_hint")
        if hint:
            filename = os.path.basename(hint)
            stem = os.path.splitext(filename)[0]
            matches = set(index.get(filename.casefold(), []) + index.get(stem.casefold(), []))
            if len(matches) == 1:
                texture_path = next(iter(matches))
        resolved.append(
            {
                "mesh_name": binding["mesh_name"],
                "slot_name": binding["slot_name"],
                "slot_ordinal": binding["slot_ordinal"],
                "texture_path": texture_path,
            }
        )
    return resolved


def write_atomic(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2), encoding="utf-8")
    os.replace(temporary, path)


def process_request(request_path, result_path, renderer, indexes, resolution, samples):
    jobs = json.loads(request_path.read_text(encoding="utf-8-sig"))
    results = []
    for job in jobs:
        try:
            renderer.render_job(
                job["source_fbx"],
                job["output_png"],
                resolve_bindings(job, indexes),
                resolution,
                samples,
            )
            results.append({"assetId": job["asset_id"], "status": "completed", "error": None})
        except renderer.UnsupportedPreviewError as error:
            results.append({"assetId": job["asset_id"], "status": "skipped", "error": str(error)})
        except Exception as error:
            results.append({"assetId": job["asset_id"], "status": "failed", "error": str(error)})
    write_atomic(result_path, results)
    request_path.unlink(missing_ok=True)


def main():
    args = parse_args()
    queue_root = Path(args.queue_root)
    requests = queue_root / "requests"
    results = queue_root / "results"
    requests.mkdir(parents=True, exist_ok=True)
    results.mkdir(parents=True, exist_ok=True)
    renderer = load_renderer()
    indexes = {}
    last_work = time.monotonic()
    while True:
        pending = sorted(requests.glob("*.json"), key=lambda path: path.stat().st_mtime_ns)
        if not pending:
            if time.monotonic() - last_work >= args.idle_seconds:
                return
            time.sleep(0.1)
            continue
        for request_path in pending:
            result_path = results / request_path.name
            process_request(
                request_path,
                result_path,
                renderer,
                indexes,
                args.resolution,
                args.samples,
            )
            last_work = time.monotonic()


if __name__ == "__main__":
    main()
