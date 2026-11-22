"""Research QA only. Compare an exported NPY against an IRSoft 5.2 Celsius XLSX."""

import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
from zipfile import ZipFile

import numpy as np


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def column_name(index):
    letters = ""
    while index:
        index, digit = divmod(index - 1, 26)
        letters = chr(65 + digit) + letters
    return letters


def read_reference(path, shape):
    with ZipFile(path) as book:
        workbook = ET.fromstring(book.read("xl/workbook.xml"))
        sheets = workbook.findall(".//{*}sheet")
        if len(sheets) != 1 or not sheets[0].get("name", "").endswith("°C"):
            raise ValueError("Expected a single IRSoft Celsius worksheet.")
        data = ET.fromstring(book.read("xl/worksheets/sheet1.xml"))
    rows = data.findall(".//{*}sheetData/{*}row")
    height, width = shape
    if len(rows) != height:
        raise ValueError("IRSoft and export row counts differ.")
    columns = [column_name(i + 1) for i in range(width)]
    matrix = np.empty(shape, np.float64)
    for row_id, row in enumerate(rows, 1):
        if row.get("r") != str(row_id) or len(row) != width:
            raise ValueError(f"Reference grid is incomplete at row {row_id}.")
        for x, cell in enumerate(row):
            if cell.get("r") != f"{columns[x]}{row_id}" or cell.get("t", "n") != "n" or cell.find("{*}f") is not None:
                raise ValueError("Expected contiguous numeric cells in native row order.")
            matrix[row_id - 1, x] = float(cell.findtext("{*}v"))
    if not np.isfinite(matrix).all():
        raise ValueError("Nonfinite reference temperature.")
    return matrix


def metrics(error):
    return dict(mae=float(np.mean(abs(error))), rmse=float(np.sqrt(np.mean(error**2))),
                maximum=float(np.max(abs(error))), bias=float(np.mean(error)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("export", type=Path, help="Single-image export folder containing temperature.npy")
    parser.add_argument("xlsx", type=Path, help="IRSoft Celsius workbook exported from the identical source")
    parser.add_argument("--tolerance", type=float, required=True)
    parser.add_argument("--settings", required=True, help="Verified acquisition/measurement and coordinate context")
    parser.add_argument("--reference-json", type=Path, help="Write 24 real sampled points for use in the GUI")
    args = parser.parse_args()
    if not np.isfinite(args.tolerance) or args.tolerance <= 0:
        parser.error("A positive finite tolerance is required.")
    report = json.loads((args.export / "extraction_report.json").read_text(encoding="utf-8"))
    npy = args.export / "temperature.npy"
    if report["outputSha256"]["temperature.npy"] != sha256(npy):
        raise ValueError("The NPY does not match the extraction report.")
    t = np.load(npy, allow_pickle=False).astype(np.float64)
    r = read_reference(args.xlsx, t.shape)
    error = t - r
    xs = np.linspace(0, t.shape[1] - 1, 6, dtype=int)
    ys = np.linspace(0, t.shape[0] - 1, 4, dtype=int)
    result = dict(sourceSha256=report["sourceSha256"], referenceSha256=sha256(args.xlsx),
                  acquisitionContext=args.settings, shape=t.shape, toleranceC=args.tolerance,
                  fullGrid=metrics(error), sampled24=metrics(error[np.ix_(ys, xs)]),
                  exportExtremaC=[float(t.min()), float(t.max())],
                  referenceExtremaC=[float(r.min()), float(r.max())],
                  flippedMaeC=metrics(t[::-1] - r)["mae"],
                  passed=bool(np.max(abs(error)) <= args.tolerance))
    if args.reference_json:
        ref = dict(sourceSha256=result["sourceSha256"], provenance=f"Testo IRSoft worksheet {args.xlsx.name}, SHA-256 {result['referenceSha256']}",
                   measurementSettings=args.settings, settingsAndCoordinatesConfirmed=True,
                   toleranceC=args.tolerance,
                   points=[dict(x=int(x), y=int(y), celsius=float(r[y, x])) for y in ys for x in xs])
        with args.reference_json.open("x", encoding="utf-8") as stream:
            json.dump(ref, stream, indent=2)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
