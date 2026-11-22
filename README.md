# BMT Thermal Analysis Studio

Windows desktop preview for Testo 890-2 BMT files. See the [Studio guide](STUDIO.md) for thermal ROIs, RGB registration, time series and build instructions. The original [exporter user guide](USER-GUIDE.txt), [compatibility notes](COMPATIBILITY.md) and [Testo integration feasibility](FEASIBILITY.md) cover extraction.

Download [Studio 0.2.1 for Windows x64](https://github.com/AlexFei2001/BMT-Temperature-Exporter-0.1.0-Windows/releases/tag/v0.2.1). Run the installer or extract the entire portable ZIP and open `BmtExporter.exe`. The installer is unsigned. The [0.1.0 release](https://github.com/AlexFei2001/BMT-Temperature-Exporter-0.1.0-Windows/releases/tag/v0.1.0) remains available.

Build with the dependencies listed in the Studio guide. Run `build.ps1` with the path to the Inno Setup compiler (`-InnoCompiler`), the build Python (`-Python`) and optionally a specific SDK host (`-Dotnet`). The script publishes a self-contained x64 build, portable ZIP and per-user installer under dist.

The application has a QA mode for developers. Run `BmtExporter.exe --qa "INPUT-BMT-DIRECTORY" "NEW-REPORT-DIRECTORY"` and inspect qa.json. It scans the input recursively and then tests repeatability, file-lock and partial-write recovery, duplicate detection, coordinate access, corrupt fields, batch continuation, source preservation, output hashes and exact float32 CSV/NPY round trips. Tests use only read access to the input dataset. The report references original file paths, so do not share it without reviewing them.

Studio QA adds checks for image-to-pixel mapping, polygon and rectangle masks, point measurements, population statistics, camera-clock ordering, session serialization, dimension and settings checks, CSV consistency, PNG rendering, and the packaged registration worker on a known translation. Run it against the published app to exercise the bundled worker. The local November 20 acquisition batch also exercises a three-frame monitoring run.

See [validation results](VALIDATION.md). The developer-only `compare_irsoft.py` accepts a single image export folder and the matching original IRSoft Celsius XLSX. With NumPy installed, pass `--tolerance` and `--settings` to reproduce the full-grid and sampled statistics. `--reference-json` saves the 24 measured sample points for the app. Verify the matching source and settings before comparing.
