using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BmtExporter;

sealed class PixelView : FrameworkElement
{
    private BitmapSource? image;
    private double zoom = 1;
    private Vector pan;
    private Point? pressed;
    private Vector startPan;
    private bool dragged;
    public BitmapSource? Image { get => image; set { image = value; FitView(); } }
    public Action<DrawingContext>? Overlay { get; set; }
    public event Action<Pixel>? PixelClicked;
    public Rect ImageBounds => Image is null ? Rect.Empty : ZoomedBounds(
        Fit(ActualWidth, ActualHeight, Image.PixelWidth, Image.PixelHeight), RenderSize, zoom, pan);

    public void EnableNavigation()
    {
        ClipToBounds = true;
        MouseWheel += (_, e) =>
        {
            if (Image is null || Hit(e) is null || pressed is not null) return;
            var cursor = e.GetPosition(this);
            Rect b = ImageBounds;
            double target = Math.Clamp(zoom * Math.Pow(1.25, e.Delta / 120.0), 1, 10);
            if (target == zoom) return;
            var ratio = target / zoom;
            zoom = target;
            Rect centered = BaseZoomedBounds();
            pan = new(cursor.X - (cursor.X - b.X) * ratio - centered.X,
                cursor.Y - (cursor.Y - b.Y) * ratio - centered.Y);
            RefreshView();
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (Image is null || !CaptureMouse()) return;
            pressed = e.GetPosition(this); startPan = pan; dragged = false;
            e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (pressed is not Point from) return;
            var delta = e.GetPosition(this) - from;
            dragged |= delta.LengthSquared >= 25;
            if (dragged) { pan = startPan + delta; RefreshView(); }
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (pressed is not Point from) return;
            bool click = !dragged && (e.GetPosition(this) - from).LengthSquared < 25;
            pressed = null; ReleaseMouseCapture();
            if (click && Hit(e) is Pixel p) PixelClicked?.Invoke(p);
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => pressed = null;
        SizeChanged += (_, _) => RefreshView();
    }

    public void FitView()
    {
        zoom = 1; pan = default; InvalidateVisual();
    }

    public void FocusPixel(Pixel point)
    {
        if (Image is null) return;
        zoom = Math.Max(zoom, 3);
        Rect b = BaseZoomedBounds();
        pan = new(ActualWidth / 2 - b.X - (point.X + .5) * b.Width / Image.PixelWidth,
            ActualHeight / 2 - b.Y - (point.Y + .5) * b.Height / Image.PixelHeight);
        RefreshView();
    }

    private Rect BaseZoomedBounds() => ZoomedBounds(
        Fit(ActualWidth, ActualHeight, Image!.PixelWidth, Image.PixelHeight), RenderSize, zoom, default);

    private void RefreshView()
    {
        if (Image is not null)
        {
            var b = BaseZoomedBounds();
            var limitX = Math.Max(0, (b.Width - ActualWidth) / 2);
            var limitY = Math.Max(0, (b.Height - ActualHeight) / 2);
            pan = new(Math.Clamp(pan.X, -limitX, limitX), Math.Clamp(pan.Y, -limitY, limitY));
        }
        InvalidateVisual();
    }

    public static Rect ZoomedBounds(Rect fitted, Size view, double zoom, Vector pan)
    {
        double w = fitted.Width * zoom, h = fitted.Height * zoom;
        return new((view.Width - w) / 2 + pan.X, (view.Height - h) / 2 + pan.Y, w, h);
    }
    public static Rect Fit(double width, double height, int pixelsX, int pixelsY)
    {
        double scale = Math.Min(width / pixelsX, height / pixelsY);
        return new((width - scale * pixelsX) / 2, (height - scale * pixelsY) / 2, scale * pixelsX, scale * pixelsY);
    }
    public static Pixel? Hit(Rect bounds, Point p, int width, int height)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || p.X < bounds.Left || p.Y < bounds.Top ||
            p.X >= bounds.Right || p.Y >= bounds.Bottom) return null;
        return new(Math.Min(width - 1, (int)((p.X - bounds.Left) / bounds.Width * width)),
            Math.Min(height - 1, (int)((p.Y - bounds.Top) / bounds.Height * height)));
    }
    public Pixel? Hit(MouseEventArgs e) => Image is null ? null : Hit(ImageBounds, e.GetPosition(this), Image.PixelWidth, Image.PixelHeight);
    public Point Center(Pixel p) => Center(p.X, p.Y);
    public Point Center(double x, double y)
    {
        Rect b = ImageBounds;
        return new(b.X + (x + .5) * b.Width / Image!.PixelWidth, b.Y + (y + .5) * b.Height / Image.PixelHeight);
    }
    public void Label(DrawingContext dc, string text, Point p, Brush color)
    {
        var label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawRectangle(Brushes.Black, null, new Rect(p, new Size(label.Width + 6, label.Height + 2)));
        dc.DrawText(label, new Point(p.X + 3, p.Y));
    }
    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(23, 42, 49)), null, new Rect(RenderSize));
        if (Image is null) return;
        dc.DrawImage(Image, ImageBounds);
        Overlay?.Invoke(dc);
    }
}
