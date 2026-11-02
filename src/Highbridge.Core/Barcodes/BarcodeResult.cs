namespace Highbridge.Barcodes;

public readonly record struct BarcodePoint(float X, float Y);

/// <summary>One barcode found on a scanned page. Coordinates are pixels of that page's image.</summary>
/// <param name="Page">Index of the page it was found on.</param>
/// <param name="Rotation">
/// The detection pass that found it: the page turned clockwise by 0, 90, 180 or 270 degrees.
/// </param>
/// <param name="Orientation">
/// The barcode's reading direction on the page, in whole degrees clockwise from left-to-right
/// (0 upright, 90 turned right, 180 upside down), measured from its corner points; null when the
/// format's points don't show it.
/// </param>
/// <param name="Points">
/// Corner or end points ZXing reports for the format (2 for 1D codes, 3-4 for 2D), in pixels of the
/// page as the scanner delivered it. A provenance band added on top shifts that image down; these
/// points are not moved.
/// </param>
public sealed record BarcodeResult(
    int Page,
    BarcodeFormat Format,
    string Text,
    int Rotation,
    int? Orientation,
    IReadOnlyList<BarcodePoint> Points);
