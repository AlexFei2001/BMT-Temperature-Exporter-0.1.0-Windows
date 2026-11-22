"""Desktop bridge to the thesis ThermalRegistrar. Images here are previews."""
import base64
import json
import sys
from pathlib import Path

import cv2
import numpy as np

from modules.registration import ThermalRegistrar
from flir_reader import extract_frame


def decode(blob, mode):
    if len(blob) > 40_000_000:
        raise ValueError("Preview payload too large.")
    image = cv2.imdecode(np.frombuffer(base64.b64decode(blob, validate=True), np.uint8), mode)
    if image is None:
        raise ValueError("Could not decode preview image.")
    return image


def register(request):
    thermal = decode(request["thermal"], cv2.IMREAD_GRAYSCALE)
    rgb = decode(request["rgb"], cv2.IMREAD_COLOR)
    src, dst = request["thermalPoints"], request["rgbPoints"]
    if len(src) != len(dst) or len(src) > 100:
        raise ValueError("Expected at most 100 paired control points.")
    for points, image in ((src, thermal), (dst, rgb)):
        if len(set(map(tuple, points))) != len(points):
            raise ValueError("Use distinct control points in both images.")
        if any(not (0 <= x < image.shape[1] and 0 <= y < image.shape[0]) for x, y in points):
            raise ValueError("Control points must be inside both images.")
    result = ThermalRegistrar(method="manual").register(
        thermal, cv2.cvtColor(rgb, cv2.COLOR_BGR2RGB), src, dst
    )
    if np.count_nonzero(result.inlier_mask) < 4 or not np.isfinite(result.fit_residuals_px).all():
        raise ValueError("Fewer than four valid inliers or a degenerate homography.")
    h, w = rgb.shape[:2]
    coverage = cv2.warpPerspective(np.full(thermal.shape, 255, np.uint8), result.transform_matrix,
                                   (w, h), flags=cv2.INTER_NEAREST)
    colored = cv2.applyColorMap(result.registered_thermal, cv2.COLORMAP_INFERNO)
    warped = np.dstack((colored, coverage))
    ok, png = cv2.imencode(".png", warped)
    if not ok:
        raise ValueError("Could not encode the registered preview.")
    return dict(homography=result.transform_matrix.tolist(), residuals=result.fit_residuals_px.tolist(),
                inliers=result.inlier_mask.tolist(), rmse=result.rmse,
                overlay=base64.b64encode(png.tobytes()).decode("ascii"))


def execute(request):
    if request.get("operation") == "flir":
        source = Path(request["path"])
        exiftool = Path(request["exiftool"])
        # Stage Unicode paths under a short ASCII filename for Windows ExifTool.
        import os
        import tempfile
        if os.name == "nt" and not str(source).isascii():
            with tempfile.TemporaryDirectory(prefix="studio-flir-") as directory:
                staged = Path(directory) / "source.jpg"
                import shutil
                shutil.copyfile(source, staged)
                order = request["byteOrder"]
                t, _, info = extract_frame(staged, order, [str(exiftool)])
                for key in ("original_parameters", "effective_parameters"):
                    info[key]["SourceFile"] = str(source)
        else:
            t, _, info = extract_frame(source, request["byteOrder"], [str(exiftool)])
        h, w = t.shape
        if not 0 < w * h <= 8_000_000:
            raise ValueError("FLIR temperature dimensions are outside the supported range.")
        return dict(width=w, height=h, metadata=info,
                    temperature=base64.b64encode(t.astype("<f8", copy=False).tobytes()).decode("ascii"))
    return register(request)


if __name__ == "__main__":
    try:
        print(json.dumps(execute(json.load(sys.stdin)), allow_nan=False))
    except Exception as exc:
        print(json.dumps({"error": str(exc)}))
        sys.exit(1)
