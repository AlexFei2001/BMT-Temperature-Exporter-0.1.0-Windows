# BMT Studio 0.4.0 development report

Windows Release was built with .NET SDK 8.0.416 with zero warnings and errors. Run `BmtExporter.exe --update-qa OUTPUT_DIR [SESSION_JSON ...]` with the bundled registration worker available to reproduce these automated tests. Use optional session files alongside any archived `temperatures.csv` for numerical comparison.

## U01 — Registration point editing

Implemented. The original thesis `ThermalRegistrar`/RANSAC fit and worker are reused. Either point can be moved independently with dragging, arrow keys, or integer X/Y entry. Selection highlights both sides. Points and pairs can be deleted, an incomplete pair can be replaced, and edits support undo and redo. Fitting recomputes on completion of the edit. Duplicate and collinear points, insufficient pairs, and invalid fits produce diagnostics. The result shows original RGB-pixel residuals, inlier mean, RMSE and maximum, and overall maximum.

Verified the live window's edit operations, ID and counterpart retention, coordinate entry, single-point removal and replacement, undo/redo, invalid geometry rejection and refitting after an edit. An exact four-pair translation produced inlier RMSE of about 1.68 × 10⁻¹⁴ px. This synthetic value measures fit residual only. Independent checkpoint accuracy was not measured. Keyboard and pointer gestures are wired to the tested operations. The automated harness exercises the edit operations rather than injecting physical input.

The worker retains its existing 100-control-pair limit, separate from the uncapped monitoring ROI collection. The preview refreshes after release, and edits are briefly gated while the worker computes.

## U02 — Registration products

Implemented. Reuses the fitted matrix, decoded RGB, original temperature accessor and common NPY/CSV writer. RGB target pixel centers are inverse-mapped to the original thermal matrix and sampled by nearest neighbor. Each output has the RGB dimensions. NPY preserves the source precision and uses NaN on invalid cells, CSV writes blank cells, and the PNG mask uses 0 and 255. The color-mapped thermal view is transparent outside valid coverage. Fusion and checkerboard keep RGB there. Palette, display range, fusion opacity, checkerboard cell size and optional overlays are recorded in JSON.

The synthetic translated 96 × 72 fixture produced 6,348 valid pixels. Tests decoded the PNG and TIFF exports, checked native dimensions, verified blank/NaN behavior and mask arithmetic, matched fusion pixels to the selected opacity, verified checkerboard tile selection and confirmed that source temperatures are unchanged. The optional color bar requires at least a 75 × 65 target. Annotation options overlay pair locations/IDs and the color scale, not arbitrary freehand annotations.

## U03 — ROI zoom and editing

Implemented. Reuses the shared `PixelView` and original pixel-center masks. User controls include cursor zoom, middle-button panning, ± zoom, Fit, Reset, physical-pixel 100%, zoom percentage and Focus ROI. Shift-drag moves an existing ROI as a group, and handles resize it. Session geometry persists in original pixel coordinates.

Tested screen-to-native hit results at 1×, 2× and 4×, including panned views, native-scale mapping, translation and handle-equivalent resize. Unchanged geometry produced identical count, mean, median, min, max and population SD at each scale. The test covers the WPF coordinate transform and geometry operations. It does not replay mouse input.

## U04 — Time, baseline and events

Implemented. Reuses camera timestamps, frame ordering and ROI statistics. Experimental time subtracts the selected origin from elapsed seconds and can be negative. The separate temperature baseline subtracts the same ROI's selected frame value. UI supports seconds, minutes and hours, a frame or numeric time origin, a frame baseline, event markers and phase selection. Difference/rate charts have a horizontal zero line. Absolute charts keep automatic scaling. The original timestamps and effective time source are exported.

Tested a 10-second origin with a −10-second first point, a separately selected baseline, missing baselines, missing and duplicate timestamps, reconstruction, event/session persistence and PNG rendering. In partial clock sessions only unique, monotonic camera times in filename order are plotted on the timed axis. A user can explicitly reconstruct in filename order when complete camera timing is unavailable. Reconstructed intervals are assumptions, and the BMT camera clock's absolute time zone remains unverified.

## U05 — Thermal contrasts

Implemented. Extends `MonitorMetrics` using the same per-frame masks and the chosen mean, median, minimum or maximum as the signal. Local contrast uses the target's mapped reference or the explicit session default. Ambient contrast subtracts an external constant or interpolated external measurements. Material contrast compares all eligible material ROIs to the reference or isolates a chosen pair. Temporal contrast shares the U04 baseline implementation.

Tested different local references in one frame, selected material pairs, external constant, linear interpolation, out-of-range samples, missing references, legacy sessions and complete CSV. At elapsed 25 s, Brick 24°C minus Mortar 20°C gave 4°C; Mortar 20°C minus its own background 17°C gave 3°C. The external air value interpolated to 18.5°C, yielding Brick minus air = 5.5°C. Missing values remain blank and reported. Sensor import expects elapsed seconds and Celsius on the stated two-column schema. Convert wall-clock sensor logs to the unshifted elapsed origin before importing.

## U06 — Rates and indicators

Methods A and B implemented. The adjacent difference divides by elapsed seconds, with the rate assigned to the right-hand sample. Missing/duplicate times or temperatures invalidate an affected pair. The user may set an upper bound on acceptable gaps. The trailing OLS slope uses actual elapsed times and at least three contiguous valid samples. Unsmoothed finite differences are retained in the CSV and available alongside regression on comparison charts.

Indicators are exported per ROI for the full session and each phase. A phase extends from its event up to the next event, exclusive. Total change is last minus first valid phase observation. Peak cooling is the most negative rate, peak heating the most positive. The means are arithmetic means of the negative/positive rates respectively. Peak times use the experimental origin. Indicator rates use finite differences wholly within the phase. A displayed regression is calculated on the full contiguous signal and then cropped to the selected phase.

| Elapsed time (s) | Brick (°C) | Finite difference (°C/s) | Display rate (°C/min) |
| ---: | ---: | ---: | ---: |
| 0 | 20 | undefined | undefined |
| 10 | 21 | 0.10 | 6 |
| 25 | 24 | 0.20 | 12 |
| 40 | 22.5 | −0.10 | −6 |

The 0, 10 and 25 s samples give an OLS slope of 155/950 ≈ 0.1631578947°C/s. The local contrast rate at 25 s is 2/15°C/s. Verified per-hour conversion independent of axis units, unfilled gaps, maximum-gap editing, phase boundaries, peaks and chart export. Optional Savitzky–Golay and dedicated ambient/material derivatives are not implemented.

## U07 preservation and reproducibility

The 1, 5, 6, 10, 20, 50 and 101 ROI cases passed. Hidden and overlapping curves, duplicate IDs, removed references, invalid geometry and missing samples remained covered. Visible-only chart exports produced the same complete CSV as all-curves exports. Both November 24 real BMT sessions had 38 frames × 7 ROIs. The 266 output rows in each matched the first 12 columns of their archived CSV, including the original temperature statistics. No ROI count limit was introduced. See [ROI regression report](ROI-REGRESSION.md).

The existing Studio QA passed on 40 BMT files, including its embedded RGB, registration worker and export checks. Four FLIR T1020 samples decoded with `swap16` had float64 NPY temperature payloads identical to their archived standalone exports.

## Screenshots and test artifacts

The automated run captures actual WPF window content with synthetic fixtures. Published examples include [control points](docs/screenshots/registration-correspondences.png), [fusion preview](docs/screenshots/registration-preview.png), [ROI editor](docs/screenshots/roi-editor.png), [rate chart and events](docs/screenshots/monitoring-interface.png) and [analysis settings](docs/screenshots/analysis-settings.png). A local run also writes machine-readable `qa.json` and product/monitoring exports to the chosen output folder.

## Modified source summary

- **Registration:** `RegistrationWindow.cs` holds point editing, fit lifecycle and the interface. `RegistrationProducts.cs` handles target-grid sampling, visualization and export. `Export.cs` shares native matrix writing.
- **ROI and viewport:** `PixelView.cs`, `RegionEditor.cs` and `Regions.cs` cover display transforms, native geometry editing and median statistics.
- **Analysis:** `MonitorSetup.cs` and `MonitorSetupWindow.cs` cover clocks and reproducible inputs. `Monitoring.cs`, `MonitorMetrics.cs` and `MonitorIndicators.cs` calculate samples, contrasts, rates and phase summaries. `MonitorPlot.cs`, `MonitorWindow.cs` and `MonitorExport.cs` display and export them.
- **Validation and build:** `UpdateQa.cs`, `MonitorQa.cs` and `App.xaml.cs` provide test execution. `BmtExporter.csproj`, `build.ps1` and `installer.iss` carry the 0.4.0 version. [User guide](STUDIO.md) and [release notes](RELEASE-NOTES.md) describe the workflow.
