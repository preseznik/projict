using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ProJict.App.Services;

namespace ProJict.App.Views;

/// <summary>
/// Shows an attached picture inside ProJict and lets you mark it up: pen, highlighter, arrows, rectangles, ellipses.
/// "Save copy" stores the marked-up picture as a new attachment beside the original, which is never changed.
/// </summary>
public partial class ImageViewerWindow : Window
{
    public enum Tool { View, Pen, Highlighter, Arrow, Rectangle, Ellipse }

    public sealed record Swatch(string Name, Color Color)
    {
        public Brush Brush { get; } = new SolidColorBrush(Color);
        public Brush Ring { get; set; } = Brushes.Transparent;
    }

    private static readonly double[] Thicknesses = [2, 4, 8];

    private readonly string _path;
    private readonly string _fileName;
    private readonly Func<byte[], string, string> _saveCopy;
    private readonly BitmapImage _bitmap;
    private readonly List<object> _undo = [];
    private readonly List<Swatch> _swatches =
    [
        new("Red", Color.FromRgb(0xE5, 0x39, 0x35)), new("Yellow", Color.FromRgb(0xFF, 0xD6, 0x00)), new("Green", Color.FromRgb(0x2E, 0xA0, 0x43)),
        new("Blue", Color.FromRgb(0x1E, 0x88, 0xE5)), new("Black", Colors.Black), new("White", Colors.White),
    ];
    private Swatch _color;
    private Tool _tool = Tool.View;
    private Path? _drawing;
    private Point _start;

    /// <param name="saveCopy">Stores the PNG bytes under the suggested name next to the original; returns the name used.</param>
    public ImageViewerWindow(string path, string fileName, Func<byte[], string, string> saveCopy)
    {
        InitializeComponent();
        _path = path;
        _fileName = fileName;
        _saveCopy = saveCopy;
        Title = fileName;

        _bitmap = new BitmapImage();
        _bitmap.BeginInit();
        _bitmap.UriSource = new Uri(path);
        _bitmap.CacheOption = BitmapCacheOption.OnLoad; // doesn't keep the file open
        _bitmap.EndInit();
        _bitmap.Freeze();
        Picture.Source = _bitmap;
        // One picture pixel per unit, so the saved copy has the picture's own size whatever the zoom.
        Board.Width = Picture.Width = Ink.Width = _bitmap.PixelWidth;
        Board.Height = Picture.Height = Ink.Height = _bitmap.PixelHeight;

        _color = _swatches[0];
        Swatches.ItemsSource = _swatches;
        SelectSwatch(_swatches[0]);
        Status.Text = $"{fileName} · {_bitmap.PixelWidth} × {_bitmap.PixelHeight}";
        Loaded += (_, _) => FitToWindow();
    }

    public int MarkupCount => Ink.Strokes.Count + Ink.Children.Count;

    // ───────────── Tools ─────────────

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        var tool = sender == PenTool ? Tool.Pen : sender == HighlighterTool ? Tool.Highlighter : sender == ArrowTool ? Tool.Arrow
            : sender == RectangleTool ? Tool.Rectangle : sender == EllipseTool ? Tool.Ellipse : Tool.View;
        SelectTool(tool);
    }

    public void SelectTool(Tool tool)
    {
        _tool = tool;
        foreach (var (button, t) in new[] { (ViewTool, Tool.View), (PenTool, Tool.Pen), (HighlighterTool, Tool.Highlighter), (ArrowTool, Tool.Arrow), (RectangleTool, Tool.Rectangle), (EllipseTool, Tool.Ellipse) })
            button.IsChecked = t == tool;
        Ink.EditingMode = tool is Tool.Pen or Tool.Highlighter ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        Ink.Cursor = tool == Tool.View ? Cursors.Arrow : Cursors.Cross;
        UpdateInk();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Swatch swatch }) SelectSwatch(swatch);
        if (_tool == Tool.View) SelectTool(Tool.Pen);
    }

    private void SelectSwatch(Swatch swatch)
    {
        _color = swatch;
        // The chosen colour gets a strong ring; the others a faint one, so white shows on a light toolbar too.
        foreach (var s in _swatches)
            s.Ring = (Brush)FindResource(s == swatch ? "TextFillColorPrimaryBrush" : "ControlStrongStrokeColorDefaultBrush");
        Swatches.ItemsSource = null;
        Swatches.ItemsSource = _swatches;
        UpdateInk();
    }

    private void Thickness_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateInk();

    private double LineWidth => Thicknesses[Math.Clamp(Thickness?.SelectedIndex ?? 1, 0, 2)];

    private void UpdateInk()
    {
        if (Ink == null) return;
        var highlighter = _tool == Tool.Highlighter;
        Ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _color.Color,
            Width = highlighter ? LineWidth * 4 : LineWidth,
            Height = highlighter ? LineWidth * 4 : LineWidth,
            IsHighlighter = highlighter,
            StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
            FitToCurve = true,
        };
    }

    // ───────────── Shapes ─────────────

    private void Ink_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_tool is not (Tool.Arrow or Tool.Rectangle or Tool.Ellipse)) return;
        _start = e.GetPosition(Ink);
        _drawing = NewShape();
        Ink.Children.Add(_drawing);
        Ink.CaptureMouse();
        e.Handled = true;
    }

    private void Ink_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drawing == null) return;
        _drawing.Data = Geometry(_tool, _start, e.GetPosition(Ink), LineWidth);
    }

    private void Ink_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawing == null) return;
        Ink.ReleaseMouseCapture();
        var end = e.GetPosition(Ink);
        if ((end - _start).Length < 4) Ink.Children.Remove(_drawing); // a click, not a shape
        else Added(_drawing);
        _drawing = null;
    }

    /// <summary>Adds a shape from code (and what a drag does): an arrow, rectangle or ellipse between two picture points.</summary>
    public void AddShape(Tool tool, Point from, Point to)
    {
        _tool = tool;
        var shape = NewShape();
        shape.Data = Geometry(tool, from, to, LineWidth);
        Ink.Children.Add(shape);
        Added(shape);
    }

    private Path NewShape() => new()
    {
        Stroke = new SolidColorBrush(_color.Color),
        StrokeThickness = LineWidth,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        IsHitTestVisible = false,
    };

    private static Geometry Geometry(Tool tool, Point a, Point b, double width)
    {
        if (tool == Tool.Rectangle) return new RectangleGeometry(new Rect(a, b));
        if (tool == Tool.Ellipse) return new EllipseGeometry(new Rect(a, b));
        // Arrow: the line and a head at the end, sized with the line.
        var direction = b - a;
        var length = direction.Length;
        var figure = new PathFigure { StartPoint = a, IsClosed = false };
        figure.Segments.Add(new LineSegment(b, true));
        if (length > 0.1)
        {
            direction /= length;
            var head = Math.Max(12, width * 4);
            var back = b - direction * head;
            var side = new Vector(-direction.Y, direction.X) * head * 0.5;
            var headFigure = new PathFigure { StartPoint = back + side, IsClosed = false };
            headFigure.Segments.Add(new LineSegment(b, true));
            headFigure.Segments.Add(new LineSegment(back - side, true));
            return new PathGeometry([figure, headFigure]);
        }
        return new PathGeometry([figure]);
    }

    // ───────────── Undo ─────────────

    private sealed record Cleared(StrokeCollection Strokes, List<UIElement> Shapes);

    private void Ink_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e) => Added(e.Stroke);

    private void Added(object markup)
    {
        _undo.Add(markup);
        UpdateButtons();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        switch (last)
        {
            case Stroke stroke: Ink.Strokes.Remove(stroke); break;
            case UIElement shape: Ink.Children.Remove(shape); break;
            case Cleared cleared:
                Ink.Strokes.Add(cleared.Strokes);
                foreach (var shape in cleared.Shapes) Ink.Children.Add(shape);
                break;
        }
        UpdateButtons();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MarkupCount == 0) return;
        var cleared = new Cleared(Ink.Strokes.Clone(), Ink.Children.Cast<UIElement>().ToList());
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        Added(cleared);
    }

    private void UpdateButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        ClearButton.IsEnabled = SaveButton.IsEnabled = MarkupCount > 0;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Escape && _drawing == null) { Close(); e.Handled = true; }
    }

    // ───────────── View ─────────────

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void ActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    private void FitToWindow()
    {
        var width = Math.Max(100, Scroller.ActualWidth - 40);
        var height = Math.Max(100, Scroller.ActualHeight - 40);
        SetZoom(Math.Min(1, Math.Min(width / _bitmap.PixelWidth, height / _bitmap.PixelHeight)));
    }

    private void SetZoom(double scale)
    {
        scale = Math.Clamp(scale, 0.05, 8);
        Zoom.ScaleX = Zoom.ScaleY = scale;
        Status.Text = $"{_fileName} · {_bitmap.PixelWidth} × {_bitmap.PixelHeight} · {scale:P0}";
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(Zoom.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { ShellLauncher.OpenFile(_path); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    // ───────────── Save ─────────────

    /// <summary>The picture with the markup on top, at the picture's own size, as PNG.</summary>
    public byte[] RenderMarkedUp()
    {
        int width = _bitmap.PixelWidth, height = _bitmap.PixelHeight;
        Ink.Measure(new Size(width, height));
        Ink.Arrange(new Rect(0, 0, width, height));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(_bitmap, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(Ink) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ClipboardAttachments.EncodePng(bitmap);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = _saveCopy(RenderMarkedUp(), $"{System.IO.Path.GetFileNameWithoutExtension(_fileName)} (marked up).png");
            Status.Text = $"Saved “{name}” next to the original.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not save: " + ex.Message;
        }
    }
}
