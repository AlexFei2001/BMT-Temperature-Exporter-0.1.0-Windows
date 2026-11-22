# Testo IrApi and IRSoft feasibility

| Integration | Evidence and decision |
|---|---|
| A. Open a saved BMT independently | Testo lists the 890 family. The SDK documents opening an image from disk, obtaining width and height and reading a pixel with GetTemperature. Iterating those coordinates is a route to a matrix; neither full-resolution 890-2 output nor SuperResolution behavior was established on this machine with the SDK. |
| B. Observe files saved into a folder | Implementable with normal Windows file access. Folder Sync imports completed BMT files and does not interact with an IRSoft process. |
| C. Read or synchronize the active IRSoft image | No documented public API was found in the SDK or IRSoft material reviewed. |
| D. Embed an export command in IRSoft | No documented public plugin, COM or equivalent extension was found. |

The IrApi manual documents a .NET Framework 4 x86 managed example and a native C interface. The repository lists VC++ 2008 x86, VC++ 2010 x86 and x64, and .NET Framework 4 or newer. The manual's historical OS list starts at XP SP3 and includes Windows 8; modern Windows 10/11 compatibility and native DLL architecture still require direct testing. Documented fields include camera name and serial, acquisition date/time, dimensions, field of view, measurement range, emissivity, reflected temperature, humidity, palette/display settings and a visible image if present. The repository carries an Apache-2.0 license. The SDK installer presents an EULA, and the scope of that EULA and individual dependency redistribution rights were not established. The environment rejected the administrative MSI extraction command, so the binary package and the SDK runtime were not exercised.

The separate Testo read_bmt example lists models 865/868/871/872/883 and does not establish support for the 890 series. The thesis GitHub URL returned 404 in the public browser; its local checkout supplied the research parser and sample data. The FLIR exporter supplied the per-source export layout, source fingerprinting and batch reporting pattern.

For this preview choose an independent, limited 890-2 file reader and Folder Sync. The reader recognizes observed binary records, compares against the research parser in local testing and marks extraction as pending independent validation. The application packages only its own code and the .NET runtime. Revisit an official reader backend after SDK installation, license review and parity testing on matching native IRSoft measurements.

- [Testo IrApi repository](https://github.com/testo/IrApi)
- [Testo IrApi manual](https://github.com/testo/IrApi/blob/main/IrApiDocumentation.pdf)
- [Testo read_bmt sample](https://github.com/testo/read_bmt)
- [Testo IRSoft product documentation](https://www.testo.com/en-US/products/thermography-irsoft)
- [FLIR Temperature Extraction Package](https://github.com/AlexFei2001/FLIR-Temperature-Extraction-Package)
