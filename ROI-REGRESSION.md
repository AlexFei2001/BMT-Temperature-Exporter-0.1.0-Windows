# ROI Monitoring regression report

## Root cause and reproduction

The previous mean chart used only the anomaly and reference series when they resolved to two different IDs. In every other case it took the first six ROIs. Its fixed palette was coupled to that restriction. Frame statistics and CSV export already iterated over the full ROI array. Creation and ROI-file validation also imposed a separate 100-ROI limit.

The saved `Test 11.24 / result of analysis / general` session contains seven ROIs across 38 frames. Its anomaly and reference are the same ID. Applying the old chart selection to that session selects six series and omits the seventh, **breaks surface 5cm**. The original CSV contains all seven IDs. The `5cm to bricks` session has a valid pair, which triggers the old two-series branch even though it also defines seven ROIs. These are reproducible chart omissions. The archived evidence does not establish that either session is the exact six-defined chart described in the report. A separate six-ROI test verifies that all six valid definitions produce series. An overlap case verifies that six series can occupy five distinct paths.

## Regression results

Windows Release build with .NET SDK 8.0.416 completed with zero warnings and errors.
The existing Studio QA also passed on 40 real BMT files, including embedded RGB and the bundled registration worker.

| Defined ROIs | Analyzed | Mean chart series | CSV data rows over 3 frames |
| ---: | ---: | ---: | ---: |
| 1 | 1 | 1 | 3 |
| 5 | 5 | 5 | 15 |
| 6 | 6 | 6 | 18 |
| 10 | 10 | 10 | 30 |
| 20 | 20 | 20 | 60 |
| 50 | 50 | 50 | 150 |
| 101 | 101 | 101 | 303 |

For both real sessions, the updated mean chart has seven series and CSV has 266 rows. For every row, the first 12 CSV columns match the archived export, including the frame, ROI identity, sample count, mean, min, max and population standard deviation.

Automated tests cover saved-session round trips, legacy JSON defaults, duplicate names and IDs, deleted references, invalid geometry, partially and entirely missing temperatures, hidden curves, overlapping paths, missing-reference gaps, untimed rates, derived CSV values and WPF PNG encoding for every metric. A partial ROI is counted as analyzed when it has at least one frame with finite data. An excluded ROI remains in the CSV with blank unavailable statistics and a reason.

Run the regression on Windows with `BmtExporter.exe --roi-qa <report.json> [session.json ...]`. Optional sessions run against their original BMT files, and a neighboring `temperatures.csv` is used for comparison when available.

## Source changes

- `Regions.cs` and `RegionEditor.cs` remove the ROI count cap and add role metadata and clearer validation.
- `Monitoring.cs` tracks per-ROI outcomes and exports complete, audited per-frame data.
- `MonitorMetrics.cs` defines the contrast and rate calculations and their reference sources.
- `MonitorPlot.cs` creates all eligible series, generates additional styles and breaks curves across missing values.
- `MonitorWindow.cs` adds curve visibility, roles, renaming, reference controls, completeness counts and complete export.
- `MonitorQa.cs` and `App.xaml.cs` provide the regression command. `STUDIO.md` describes the workflow.
