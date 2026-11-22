"""Classical IR-to-RGB registration with explicit geometric-quality metadata.

Fitting residuals are not independent registration accuracy. Use held-out
check points for a defensible uncertainty estimate; an in-sample proxy is
available only when explicitly requested.
"""
from __future__ import annotations

import logging
from typing import Optional

import cv2
import numpy as np

from modules._types import RegistrationResult

logger = logging.getLogger(__name__)


class ThermalRegistrar:
    """Global manual/SIFT/precomputed registration with optional ECC refinement."""

    def __init__(self, method: str = "feature", min_matches: int = 10,
                 refine_method: str = "none", uncertainty_floor_px: float = 0.0,
                 allow_fit_residual_proxy: bool = False,
                 ecc_iterations: int = 100, ecc_epsilon: float = 1e-6) -> None:
        if method not in {"feature", "affine", "manual", "precomputed"}:
            raise ValueError(f"Unsupported global registration method: {method}")
        if refine_method not in {"none", "ecc"}:
            raise ValueError(f"Unsupported refinement method: {refine_method}")
        if min_matches < 4 or uncertainty_floor_px < 0 or ecc_iterations < 1 or ecc_epsilon <= 0:
            raise ValueError("Invalid registration configuration")
        self.method = method
        self.min_matches = min_matches
        self.refine_method = refine_method
        self.uncertainty_floor_px = uncertainty_floor_px
        self.allow_fit_residual_proxy = allow_fit_residual_proxy
        self.ecc_iterations = ecc_iterations
        self.ecc_epsilon = ecc_epsilon
        self._homography: Optional[np.ndarray] = None
        self._last_result: Optional[RegistrationResult] = None

    @staticmethod
    def _points(points, name: str, minimum: int) -> np.ndarray:
        values = np.asarray(points, dtype=np.float64)
        if values.ndim != 2 or values.shape[1] != 2 or len(values) < minimum or not np.isfinite(values).all():
            raise ValueError(f"{name} must contain at least {minimum} finite (x, y) points")
        return values

    @staticmethod
    def _validate_transform(H: np.ndarray) -> np.ndarray:
        H = np.asarray(H, dtype=np.float64)
        if H.shape != (3, 3) or not np.isfinite(H).all() or abs(np.linalg.det(H)) < 1e-12:
            raise ValueError("Registration transform must be a finite, invertible 3x3 matrix")
        if abs(H[2, 2]) < 1e-12:
            raise ValueError("Registration transform has an invalid homogeneous scale")
        return H / H[2, 2]

    @staticmethod
    def _residuals(H: np.ndarray, src: np.ndarray, dst: np.ndarray) -> np.ndarray:
        projected = cv2.perspectiveTransform(src.astype(np.float64).reshape(-1, 1, 2), H).reshape(-1, 2)
        return np.linalg.norm(projected - dst, axis=1)

    @staticmethod
    def _feature_image(image: np.ndarray) -> np.ndarray:
        if image.dtype == np.uint8:
            return image
        return cv2.normalize(image, None, 0, 255, cv2.NORM_MINMAX).astype(np.uint8)

    def _feature_based_homography(self, src: np.ndarray, dst: np.ndarray):
        """Return H and the actual matched point pairs used to fit it."""
        sift = cv2.SIFT_create()
        src_kp, src_desc = sift.detectAndCompute(src, None)
        dst_kp, dst_desc = sift.detectAndCompute(dst, None)
        if src_desc is None or dst_desc is None or len(src_kp) < self.min_matches:
            return None
        matcher = cv2.FlannBasedMatcher(dict(algorithm=1, trees=5), dict(checks=50))
        pairs = matcher.knnMatch(src_desc, dst_desc, k=2)
        good = [m for pair in pairs if len(pair) == 2 for m, n in [pair] if m.distance < 0.75 * n.distance]
        if len(good) < self.min_matches:
            return None
        src_pts = np.float64([src_kp[m.queryIdx].pt for m in good])
        dst_pts = np.float64([dst_kp[m.trainIdx].pt for m in good])
        if self.method == "affine":
            affine, inliers = cv2.estimateAffinePartial2D(src_pts, dst_pts, method=cv2.RANSAC)
            H = np.vstack((affine, [0, 0, 1])) if affine is not None else None
        else:
            H, inliers = cv2.findHomography(src_pts, dst_pts, cv2.RANSAC, 5.0)
        if H is None:
            return None
        return H, src_pts, dst_pts, inliers

    def register(self, thermal: np.ndarray, rgb: np.ndarray,
                 src_points: Optional[np.ndarray] = None,
                 dst_points: Optional[np.ndarray] = None,
                 *, transform_matrix: Optional[np.ndarray] = None,
                 check_src_points: Optional[np.ndarray] = None,
                 check_dst_points: Optional[np.ndarray] = None,
                 rgb_gsd_mm_px: Optional[float] = None,
                 refinement_mask: Optional[np.ndarray] = None) -> RegistrationResult:
        """Register IR to RGB and return image, transform and honest quality data.

        All residuals and uncertainty are measured in destination RGB pixels.
        Held-out check points are preferred. With no check points and default
        settings, spatial uncertainty remains unknown rather than zero.
        """
        if thermal is None or rgb is None or rgb.ndim != 3:
            raise ValueError("Thermal and RGB images are required")
        if rgb_gsd_mm_px is not None and rgb_gsd_mm_px <= 0:
            raise ValueError("RGB GSD must be positive mm per pixel")
        thermal_gray = cv2.cvtColor(thermal, cv2.COLOR_RGB2GRAY) if thermal.ndim == 3 else np.asarray(thermal)
        if thermal_gray.ndim != 2:
            raise ValueError("Thermal image must be 2D or RGB")
        rgb_gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
        src, dst, inliers = None, None, None
        if self.method == "manual":
            if src_points is None or dst_points is None:
                raise ValueError("Manual registration needs paired control points")
            src = self._points(src_points, "src_points", 4)
            dst = self._points(dst_points, "dst_points", 4)
            if src.shape != dst.shape:
                raise ValueError("Control-point arrays must have the same shape")
            H, inliers = cv2.findHomography(src, dst, cv2.RANSAC, 5.0)
        elif self.method == "precomputed":
            if transform_matrix is None:
                raise ValueError("Precomputed registration needs transform_matrix")
            H = transform_matrix
            if src_points is not None or dst_points is not None:
                src = self._points(src_points, "src_points", 4)
                dst = self._points(dst_points, "dst_points", 4)
                if src.shape != dst.shape:
                    raise ValueError("Control-point arrays must have the same shape")
                inliers = np.ones((len(src), 1), dtype=np.uint8)
        else:
            result = self._feature_based_homography(self._feature_image(thermal_gray),
                                                    self._feature_image(rgb_gray))
            if result is None:
                raise RuntimeError("Registration failed: insufficient cross-modal matches")
            H, src, dst, inliers = result
        if H is None:
            raise RuntimeError("Registration transform estimation failed")
        H = self._validate_transform(H)
        check_src, check_dst = None, None
        if check_src_points is not None or check_dst_points is not None:
            if check_src_points is None or check_dst_points is None:
                raise ValueError("Both sets of independent check points are required")
            check_src = self._points(check_src_points, "check_src_points", 1)
            check_dst = self._points(check_dst_points, "check_dst_points", 1)
            if check_src.shape != check_dst.shape:
                raise ValueError("Check-point arrays must have the same shape")

        height, width = rgb.shape[:2]
        refinement_status = "not_requested"
        if self.refine_method == "ecc":
            global_warp = cv2.warpPerspective(thermal_gray, H, (width, height))
            reg_float = self._feature_image(global_warp).astype(np.float32) / 255.0
            rgb_float = self._feature_image(rgb_gray).astype(np.float32) / 255.0
            mask = None
            if refinement_mask is not None:
                if refinement_mask.shape != (height, width):
                    raise ValueError("Refinement mask must match the RGB grid")
                mask = (np.asarray(refinement_mask, dtype=bool) * 255).astype(np.uint8)
            try:
                _, correction = cv2.findTransformECC(
                    rgb_float, reg_float, np.eye(3, dtype=np.float32),
                    cv2.MOTION_HOMOGRAPHY,
                    (cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT,
                     self.ecc_iterations, self.ecc_epsilon), mask,
                )
                candidate_H = self._validate_transform(np.linalg.inv(correction) @ H)
                if check_src is not None:
                    before = self._residuals(H, check_src, check_dst)
                    after = self._residuals(candidate_H, check_src, check_dst)
                    if np.mean(after) > np.mean(before):
                        refinement_status = "rejected_by_check_points"
                    else:
                        H, refinement_status = candidate_H, "ecc_applied"
                else:
                    H, refinement_status = candidate_H, "ecc_applied_unverified"
            except (cv2.error, ValueError, np.linalg.LinAlgError) as exc:
                refinement_status = f"ecc_failed: {type(exc).__name__}"
                logger.warning("ECC refinement failed; using global registration: %s", exc)

        registered = cv2.warpPerspective(thermal_gray, H, (width, height))
        fit_residuals = self._residuals(H, src, dst) if src is not None else None
        inlier_mask = np.asarray(inliers, dtype=bool).ravel() if inliers is not None else None
        fit_used = fit_residuals[inlier_mask] if fit_residuals is not None and inlier_mask is not None else fit_residuals
        if fit_used is not None and len(fit_used) == 0:
            fit_used = None
        check_residuals = self._residuals(H, check_src, check_dst) if check_src is not None else None
        report_residuals = check_residuals if check_residuals is not None else fit_used
        uncertainty, source = None, "unavailable"
        if check_residuals is not None and len(check_residuals) >= 3:
            uncertainty = max(float(np.percentile(check_residuals, 95)), self.uncertainty_floor_px)
            source = "independent_check_points"
        elif self.allow_fit_residual_proxy and fit_used is not None and len(fit_used) >= 5:
            uncertainty = max(float(np.percentile(fit_used, 95)), self.uncertainty_floor_px)
            source = "fit_residual_proxy_optimistic"
        self._homography = H
        local_linear = H[:2, :2]
        transform_diagnostics = {
            "linear_determinant": float(np.linalg.det(local_linear)),
            "local_scale_singular_values": np.linalg.svd(local_linear, compute_uv=False).tolist(),
            "local_rotation_deg": float(np.degrees(np.arctan2(H[1, 0], H[0, 0]))),
            "translation_px_at_origin": H[:2, 2].tolist(),
            "perspective_terms": H[2, :2].tolist(),
        }
        self._last_result = RegistrationResult(
            registered_thermal=registered,
            transform_matrix=H,
            method=f"{self.method}+ecc" if refinement_status.startswith("ecc_applied") else self.method,
            rmse=float(np.sqrt(np.mean(report_residuals ** 2))) if report_residuals is not None else None,
            median_residual=float(np.median(report_residuals)) if report_residuals is not None else None,
            max_residual=float(np.max(report_residuals)) if report_residuals is not None else None,
            inlier_ratio=float(np.mean(inlier_mask)) if inlier_mask is not None else None,
            number_of_matches=len(src) if src is not None else None,
            spatial_uncertainty_px=uncertainty,
            spatial_uncertainty_mm=uncertainty * rgb_gsd_mm_px if uncertainty is not None and rgb_gsd_mm_px is not None else None,
            quality_score=None,  # a calibrated registration quality function is not available
            uncertainty_source=source,
            residuals_px=report_residuals,
            fit_residuals_px=fit_residuals,
            check_residuals_px=check_residuals,
            inlier_mask=inlier_mask,
            transform_diagnostics=transform_diagnostics,
            refinement_status=refinement_status,
        )
        return self._last_result

    def register_image(self, *args, **kwargs) -> np.ndarray:
        """Compatibility helper for callers that only need the warped image."""
        return self.register(*args, **kwargs).registered_thermal

    @property
    def homography(self) -> Optional[np.ndarray]:
        return self._homography

    @property
    def last_result(self) -> Optional[RegistrationResult]:
        return self._last_result
