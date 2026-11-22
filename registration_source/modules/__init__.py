from modules._types import (
  ValidationLevel,
  CrackDetection,
  ThermalFeatureMaps,
  ThermalEvidenceMetrics,
  ValidatedCrack,
  RegistrationResult,
  CrackROI,
  OverlapMetrics,
  AcquisitionConditions,
  FeatureReliability,
  TEIResult,
)

from importlib import import_module

_LAZY = {
  "CrackDetector": "detection", "ThermalRegistrar": "registration",
  "ThermalFeatureExtractor": "thermal_features",
  "ThermalAnomalySegmenter": "anomaly_segmentation", "ROIGenerator": "roi_generator",
  "OverlapAnalyzer": "overlap_analysis", "TEICalculator": "tei",
  "DiagnosticConfidence": "diagnostic_confidence", "PathologyVisualizer": "visualization",
  "ThermalQualityAssessor": "thermal_quality",
}


def __getattr__(name):
  if name not in _LAZY:
    raise AttributeError(name)
  value = getattr(import_module(f"{__name__}.{_LAZY[name]}"), name)
  globals()[name] = value
  return value

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
  "CrackDetector",
  "ThermalRegistrar",
  "ThermalFeatureExtractor",
  "ThermalAnomalySegmenter",
  "ROIGenerator",
  "OverlapAnalyzer",
  "TEICalculator",
  "DiagnosticConfidence",
  "PathologyVisualizer",
  "ThermalQualityAssessor",
]
