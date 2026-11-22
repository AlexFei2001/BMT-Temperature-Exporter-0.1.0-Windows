# Validation on Windows, 22 November 2026

## Independent comparison with Testo IRSoft

Testo IRSoft 5.2.6280.37071 exported Celsius XLSX grids from copies of three genuine BMT files under their stored settings. IR000203 and IR000211 showed emissivity 0.93 and reflected temperature 16°C. The batch export of IR000070 used its stored settings. The comparison matched workbook A1 to zero-based pixel (0,0) and checked each contiguous native cell without resizing, flipping or fitting an offset. A preliminary 0.1°C comparison tolerance was defined in the reference template.

| BMT | Width × height | Pixels | MAE °C | RMSE °C | Maximum absolute error °C | Bias °C |
|---|---:|---:|---:|---:|---:|---:|
| IR000203 | 640 × 480 | 307,200 | 0.000003673 | 0.000004544 | 0.000015259 | +0.000003122 |
| IR000070 | 1280 × 960 | 1,228,800 | 0.000004336 | 0.000005504 | 0.000022888 | +0.000003181 |
| IR000211 | 1280 × 960 | 1,228,800 | 0.000003681 | 0.000004551 | 0.000015259 | +0.000003140 |

The validation script measured 24 distributed positions per file in addition to 2,764,800 full-grid comparisons. An intentionally flipped grid increased MAE to 0.211°C, 0.990°C and 0.269°C, respectively. Extrema agreed within the full-grid error limits. IR000211 contains two temperature records. Its BMP preview header says 640 × 480, while IRSoft identifies it as 1280 × 960 SuperResolution. The final reader uses the temperature record consistent with the stored extrema and the XLSX confirms its values and orientation.

The workbook references and JSON point samples are in `dist/validation` on the build machine, outside both distributable packages. The JSON files can be loaded in the app after selecting the matching BMT. Source and reference SHA-256 values are recorded there. These checks measure agreement with IRSoft for these files and settings. Absolute camera calibration and other Testo models remain outside this evidence.

Testo documents XLS/XLSX temperature export in [the IRSoft manual](https://static.testo.com/image/upload/Instruction-manual-and-Software/Instruction-manuals/testo-irsoft-bedienungsanleitung.pdf).

## Application and packaging QA

- Scanned the 616 BMT files available in the thesis datasets, including the November 20 laboratory batch, and exercised the application QA checks for malformed data, missing visible images, coordinate order, source preservation, file locks, partial writes, duplicates, repeatability, and exact float32 CSV/NPY readback. NumPy independently read the same exported float32 data.
- Opened a real sample in the portable GUI, visually checked the preview and statistics, and checked that hovering returned a native pixel coordinate and temperature.
- Installed the self-contained package into a temporary per-user directory on this Windows machine, ran its QA checks with Python and .NET absent from PATH and DOTNET_ROOT pointing at a nonexistent directory, then removed the test install. A separate clean Windows VM was unavailable.
