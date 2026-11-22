from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Optional

import numpy as np


class ValidationLevel(str, Enum):
   """Confidence level assigned after thermal validation."""
   LOW = "Low"
   MEDIUM = "Medium"
   HIGH = "High"


@dataclass
class CrackDetection:
   """A single crack detected by YOLO on the RGB orthophoto.
   Attributes:
       id: Unique identifier for this crack instance.
       mask: Binary mask (H, W) isolating this crack's pixels.
       bbox: Axis-aligned bounding box (x1, y1, x2, y2) in image coordinates.
       confidence: YOLO confidence score in [0, 1].
   """
   id: int
   mask: np.ndarray
   bbox: tuple[int, int, int, int]
   confidence: float


@dataclass
class ThermalFeatureMaps:
   """Global thermal feature maps computed from the registered thermal image.
   Attributes:
       temperature: Raw temperature map (H, W).
       delta_T: Local temperature difference map (H, W).
       gradient: Temperature gradient magnitude map (H, W).
       variance: Local temperature variance map (H, W).
       density: Temperature density distribution map (H, W).
   """
   temperature: np.ndarray
   delta_T: np.ndarray
   gradient: np.ndarray
   variance: np.ndarray
   density: np.ndarray


@dataclass
class ThermalEvidenceMetrics:
   """All thermal evidence collected for a single crack.
   Attributes:
       overlap_ratio: Fraction of crack skeleton overlapping thermal anomaly.
       average_delta_T: Mean temperature difference within the ROI.
       max_gradient: Maximum temperature gradient magnitude in the ROI.
       variance: Local temperature variance in the ROI.
       density_mean: Mean of the density distribution within the ROI.
       density_median: Median of the density distribution within the ROI.
       density_std: Standard deviation of density within the ROI.
   """
   overlap_ratio: float = 0.0
   average_delta_T: float = 0.0
   max_gradient: float = 0.0
   variance: float = 0.0
   density_mean: float = 0.0
   density_median: float = 0.0
   density_std: float = 0.0
   spatial: Optional[OverlapMetrics] = None


@dataclass
class ValidatedCrack:
   """A crack with full diagnostic information after thermal validation.
   Attributes:
       id: Unique identifier.
       geometry: Binary crack mask (H, W).
       roi: Buffered ROI mask (H, W) around the crack.
       yolo_confidence: Detection confidence from YOLO.
       evidence: All thermal evidence metrics for this crack.
       tei: Thermal Evidence Index value in [0, 1].
       diagnostic_confidence: Final validation level.
   """
   id: int
   geometry: np.ndarray
   roi: np.ndarray
   yolo_confidence: float
   evidence: Optional[ThermalEvidenceMetrics] = None
   tei: Optional[float] = None
   diagnostic_confidence: Optional[ValidationLevel] = None
   tei_result: Optional[TEIResult] = None
   roi_info: Optional[CrackROI] = None
   assessment_status: Optional[str] = None


@dataclass
class RegistrationResult:
   """IR-to-RGB transform and quality estimates in destination RGB pixels.

   Fitting residuals are optimistic. Independent check points, when supplied,
   provide the preferred spatial-uncertainty estimate. None means unknown.
   """
   registered_thermal: np.ndarray
   transform_matrix: np.ndarray
   method: str
   rmse: Optional[float] = None
   median_residual: Optional[float] = None
   max_residual: Optional[float] = None
   inlier_ratio: Optional[float] = None
   number_of_matches: Optional[int] = None
   spatial_uncertainty_px: Optional[float] = None
   spatial_uncertainty_mm: Optional[float] = None
   quality_score: Optional[float] = None
   uncertainty_source: str = "unavailable"
   residuals_px: Optional[np.ndarray] = None  # held-out if available, else fit inliers
   fit_residuals_px: Optional[np.ndarray] = None
   check_residuals_px: Optional[np.ndarray] = None
   inlier_mask: Optional[np.ndarray] = None  # refers to fit correspondences only
   transform_diagnostics: dict = field(default_factory=dict)
   refinement_status: str = "not_requested"

   @property
   def spatial_uncertainty(self) -> Optional[float]:
       return self.spatial_uncertainty_px


@dataclass
class CrackROI:
   """Visible core and thermal search mask, both on the registered RGB grid."""
   crack_geometry: np.ndarray
   core_mask: np.ndarray
   search_mask: np.ndarray
   buffer_radius_px: int
   buffer_radius_mm: Optional[float] = None
   registration_uncertainty_px: Optional[float] = None


@dataclass
class OverlapMetrics:
   """Spatial measures; IoU and centerline overlap use different denominators."""
   strict_iou: float
   buffered_iou: float
   centerline_overlap_ratio: float
   buffered_overlap_ratio: float
   mean_distance_to_anomaly: Optional[float]
   registration_uncertainty_px: Optional[float]
   tolerance_px: int


@dataclass
class AcquisitionConditions:
   thermal_gsd: Optional[float] = None  # mm per thermal pixel
   rgb_gsd: Optional[float] = None      # mm per registered RGB pixel
   distance: Optional[float] = None
   registration_rmse: Optional[float] = None
   registration_uncertainty_px: Optional[float] = None
   registration_uncertainty_mm: Optional[float] = None
   registration_inlier_ratio: Optional[float] = None
   netd: Optional[float] = None
   acquisition_time: Optional[str] = None
   heating_condition: Optional[str] = None
   emissivity_uncertainty: Optional[float] = None
   ambient_temperature: Optional[float] = None
   wind: Optional[float] = None
   solar_condition: Optional[str] = None
   material_type: Optional[str] = None


@dataclass
class FeatureReliability:
   """Provisional [0, 1] feature reliabilities; None excludes a feature."""
   delta_t_reliability: Optional[float] = None
   gradient_reliability: Optional[float] = None
   variance_reliability: Optional[float] = None
   density_reliability: Optional[float] = None
   overlap_reliability: Optional[float] = None
   basis: str = "unassessed"


@dataclass
class TEIResult:
   fixed_tei: float
   adaptive_tei: Optional[float]
   tei_reliability: Optional[float]
   feature_scores: dict[str, float] = field(default_factory=dict)
   feature_reliabilities: dict[str, Optional[float]] = field(default_factory=dict)
   dynamic_weights: dict[str, float] = field(default_factory=dict)
   reliability_basis: str = "unassessed"


__all__ = [
   "ValidationLevel",
   "CrackDetection",
   "ThermalFeatureMaps",
   "ThermalEvidenceMetrics",
   "ValidatedCrack",
   "RegistrationResult",
   "CrackROI",
   "OverlapMetrics",
   "AcquisitionConditions",
   "FeatureReliability",
   "TEIResult",
]
