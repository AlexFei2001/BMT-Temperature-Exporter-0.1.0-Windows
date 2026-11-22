# BMT Temperature Exporter

Windows desktop preview for Testo 890-2 BMT files. See [user guide](USER-GUIDE.txt), [compatibility and limitations](COMPATIBILITY.md) and [Testo integration feasibility](FEASIBILITY.md).

Download the [Windows x64 installer or portable ZIP](https://github.com/AlexFei2001/BMT-Temperature-Exporter-0.1.0-Windows/releases/tag/v0.1.0). Run the installer, or extract the entire portable ZIP and open `BmtExporter.exe`. Both builds include the .NET desktop runtime. The installer is unsigned.

Build with .NET 8 SDK on Windows and Inno Setup 6. Run `build.ps1` with the path to the Inno Setup command-line compiler (`-InnoCompiler`) and optionally a specific SDK host (`-Dotnet`). The script publishes a self-contained x64 build, portable ZIP and per-user installer under dist.

The application has a QA mode for developers. Run `BmtExporter.exe --qa "INPUT-BMT-DIRECTORY" "NEW-REPORT-DIRECTORY"` and inspect qa.json. It scans the input recursively and then tests repeatability, file-lock and partial-write recovery, duplicate detection, coordinate access, corrupt fields, batch continuation, source preservation, output hashes and exact float32 CSV/NPY round trips. Tests use only read access to the input dataset. The report references original file paths, so do not share it without reviewing them.

See [validation results](VALIDATION.md). The developer-only `compare_irsoft.py` accepts a single image export folder and the matching original IRSoft Celsius XLSX. With NumPy installed, pass `--tolerance` and `--settings` to reproduce the full-grid and sampled statistics. `--reference-json` saves the 24 measured sample points for the app. Verify the matching source and settings before comparing.
