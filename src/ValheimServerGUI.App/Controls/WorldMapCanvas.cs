using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.App.Controls;

/// <summary>
/// An interactive Valheim world map: draws a <see cref="WorldMapRenderer"/>'s terrain with markers and an optional
/// fog over unexplored zones. Wheel or the +/− buttons zoom around the pointer, dragging pans, double-click zooms in.
/// The whole world is drawn once in the background; when zoomed in past its resolution the visible area is redrawn
/// sharp, also in the background, so the UI thread only ever composes bitmaps.
/// </summary>
public class WorldMapCanvas : Control
{
    public static readonly StyledProperty<WorldMapRenderer?> RendererProperty =
        AvaloniaProperty.Register<WorldMapCanvas, WorldMapRenderer?>(nameof(Renderer));

    public static readonly StyledProperty<IReadOnlyList<WorldMapMarker>?> MarkersProperty =
        AvaloniaProperty.Register<WorldMapCanvas, IReadOnlyList<WorldMapMarker>?>(nameof(Markers));

    public static readonly StyledProperty<WorldMapExploration?> ExplorationProperty =
        AvaloniaProperty.Register<WorldMapCanvas, WorldMapExploration?>(nameof(Exploration));

    public static readonly StyledProperty<bool> ShowFogProperty =
        AvaloniaProperty.Register<WorldMapCanvas, bool>(nameof(ShowFog));

    public static readonly StyledProperty<WorldMapFocus?> FocusProperty =
        AvaloniaProperty.Register<WorldMapCanvas, WorldMapFocus?>(nameof(Focus));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<WorldMapCanvas, bool>(nameof(IsExpanded), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> PlaceholderTextProperty =
        AvaloniaProperty.Register<WorldMapCanvas, string?>(nameof(PlaceholderText));

    public WorldMapRenderer? Renderer { get => GetValue(RendererProperty); set => SetValue(RendererProperty, value); }
    public IReadOnlyList<WorldMapMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    /// <summary>The discovered area; with <see cref="ShowFog"/> on, everything else is covered.</summary>
    public WorldMapExploration? Exploration { get => GetValue(ExplorationProperty); set => SetValue(ExplorationProperty, value); }
    public bool ShowFog { get => GetValue(ShowFogProperty); set => SetValue(ShowFogProperty, value); }
    public WorldMapFocus? Focus { get => GetValue(FocusProperty); set => SetValue(FocusProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    /// <summary>Text shown in the middle while there is no map (e.g. "generating").</summary>
    public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }

    private const int PreviewPixels = 512;
    private const int BasePixels = 2048;
    private const int MaxDetailPixels = 2400;
    private const double MaxScale = 6; // DIPs per metre
    private const double MarkerHitRadius = 9;
    private const double ButtonSize = 26;
    private const double LabelScale = 0.12; // DIPs per metre above which important markers are labelled

    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x16, 0x0F));
    private static readonly IBrush Gold = new SolidColorBrush(Color.FromRgb(0xD4, 0xA6, 0x3A));
    private static readonly IBrush Parchment = new SolidColorBrush(Color.FromRgb(0xF0, 0xD9, 0xA0));
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.FromArgb(0xD8, 0x1F, 0x16, 0x0F));
    private static readonly IBrush PanelHoverBrush = new SolidColorBrush(Color.FromArgb(0xF0, 0x3F, 0x2E, 0x1F));
    private static readonly IPen GoldPen = new Pen(Gold, 1.5);
    private static readonly IPen InkPen = new Pen(Ink, 1.5);
    private static readonly IPen FocusPen = new Pen(Brushes.White, 2);
    private static readonly IPen ButtonGlyphPen = new Pen(Gold, 2, lineCap: PenLineCap.Round);
    private static readonly Typeface LabelTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly IBrush SpawnBrush = Brushes.White;
    private static readonly IBrush BossBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush TraderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB9, 0x3C));
    private static readonly IBrush DungeonBrush = new SolidColorBrush(Color.FromRgb(0x9B, 0x7B, 0xD0));
    private static readonly IBrush CampBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x84, 0x2E));

    private enum MapButton { None, ZoomIn, ZoomOut, Reset, Expand }

    // View: the world point at the control's centre, and DIPs per metre.
    private double _centerX;
    private double _centerZ;
    private double _scale;
    private bool _viewInitialised;

    private WriteableBitmap? _baseBitmap;
    private WorldMapRegion _baseRegion;
    private WriteableBitmap? _detailBitmap;
    private WorldMapRegion _detailRegion;
    private int _detailWidth;
    private int _detailHeight;
    private WriteableBitmap? _fogBitmap;
    private WorldMapExploration? _fogExploration;

    private CancellationTokenSource? _baseCts;
    private CancellationTokenSource? _detailCts;
    private readonly DispatcherTimer _detailTimer;

    private Point? _dragStart;
    private (double X, double Z) _dragStartCenter;
    private bool _dragged;
    private Point? _pointer;
    private WorldMapMarker? _hovered;
    private MapButton _hoveredButton;
    private Cursor? _handCursor;

    static WorldMapCanvas()
    {
        AffectsRender<WorldMapCanvas>(MarkersProperty, ShowFogProperty, PlaceholderTextProperty, IsExpandedProperty);
        FocusableProperty.OverrideDefaultValue<WorldMapCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<WorldMapCanvas>(true);
    }

    public WorldMapCanvas()
    {
        _detailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _detailTimer.Tick += (_, _) =>
        {
            _detailTimer.Stop();
            StartDetailRender();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == RendererProperty)
        {
            OnRendererChanged();
        }
        else if (change.Property == ExplorationProperty)
        {
            BuildFog();
            if (Bounds.Width > 0) ResetView();
            else InvalidateVisual();
        }
        else if (change.Property == ShowFogProperty && Bounds.Width > 0)
        {
            ResetView();
        }
        else if (change.Property == FocusProperty && Focus is { } focus)
        {
            _centerX = focus.X;
            _centerZ = focus.Z;
            _scale = Math.Max(_scale, 0.2);
            OnViewChanged();
        }
        else if (change.Property == MarkersProperty)
        {
            _hovered = null;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_viewInitialised && Bounds.Width > 0 && Bounds.Height > 0)
        {
            ResetView();
        }
        else
        {
            _scale = Math.Max(_scale, FitScale * 0.8);
            OnViewChanged();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _detailTimer.Stop();
        _baseCts?.Cancel();
        _detailCts?.Cancel();
    }

    // ===== View =====

    private double FitScale => Math.Max(1e-6, Math.Min(Bounds.Width, Bounds.Height) / (2 * WorldMapRenderer.MapRadius));

    /// <summary>
    /// Frames the discovered area when only that is shown, else the whole world.
    /// </summary>
    private void ResetView()
    {
        _centerX = 0;
        _centerZ = 0;
        _scale = FitScale;
        if (ShowFog && _fogExploration?.ExploredBounds() is { } b)
        {
            const double margin = 1.25;
            var width = Math.Max(b.East - b.West, 400) * margin;
            var height = Math.Max(b.North - b.South, 400) * margin;
            _centerX = (b.West + b.East) / 2;
            _centerZ = (b.South + b.North) / 2;
            _scale = Math.Clamp(Math.Min(Bounds.Width / width, Bounds.Height / height), FitScale, MaxScale);
        }
        _viewInitialised = Bounds.Width > 0;
        OnViewChanged();
    }

    private void ZoomAt(Point anchor, double factor)
    {
        var (wx, wz) = ToWorld(anchor);
        _scale = Math.Clamp(_scale * factor, FitScale * 0.8, MaxScale);
        // Keep the world point under the anchor where it was.
        _centerX = wx - (anchor.X - Bounds.Width / 2) / _scale;
        _centerZ = wz + (anchor.Y - Bounds.Height / 2) / _scale;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        var limit = WorldMapRenderer.MapRadius;
        _centerX = Math.Clamp(_centerX, -limit, limit);
        _centerZ = Math.Clamp(_centerZ, -limit, limit);
        InvalidateVisual();
        _detailTimer.Stop();
        _detailTimer.Start();
    }

    private (double X, double Z) ToWorld(Point p)
        => (_centerX + (p.X - Bounds.Width / 2) / _scale, _centerZ - (p.Y - Bounds.Height / 2) / _scale);

    private Point ToScreen(double x, double z)
        => new(Bounds.Width / 2 + (x - _centerX) * _scale, Bounds.Height / 2 - (z - _centerZ) * _scale);

    private Rect ToScreen(WorldMapRegion region, int width, int height)
    {
        var topLeft = ToScreen(region.Left, region.Top);
        return new Rect(topLeft.X, topLeft.Y, width * region.MetersPerPixel * _scale, height * region.MetersPerPixel * _scale);
    }

    // ===== Rendering the terrain off the UI thread =====

    private void OnRendererChanged()
    {
        _baseCts?.Cancel();
        _detailCts?.Cancel();
        _baseBitmap = null;
        _detailBitmap = null;
        InvalidateVisual();

        var renderer = Renderer;
        if (renderer is null) return;

        var cts = new CancellationTokenSource();
        _baseCts = cts;
        _ = RenderBaseAsync(renderer, cts.Token);
    }

    private async Task RenderBaseAsync(WorldMapRenderer renderer, CancellationToken token)
    {
        try
        {
            // A quick coarse pass first, so the map appears at once, then the full-resolution one.
            foreach (var size in new[] { PreviewPixels, BasePixels })
            {
                var region = WorldMapRegion.WholeWorld(size);
                var pixels = await Task.Run(() =>
                {
                    var buffer = new uint[size * size];
                    renderer.Render(region, size, size, buffer, token);
                    return buffer;
                }, token);

                if (token.IsCancellationRequested || !ReferenceEquals(renderer, Renderer)) return;
                _baseBitmap = ToBitmap(pixels, size, size);
                _baseRegion = region;
                InvalidateVisual();
            }

            StartDetailRender();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StartDetailRender()
    {
        _detailCts?.Cancel();
        var renderer = Renderer;
        if (renderer is null || _baseBitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var metersPerDevicePixel = 1 / (_scale * renderScaling);
        if (metersPerDevicePixel > _baseRegion.MetersPerPixel * 0.75)
        {
            // The whole-world bitmap is sharp enough at this zoom.
            if (_detailBitmap is not null)
            {
                _detailBitmap = null;
                InvalidateVisual();
            }
            return;
        }

        // The visible part of the world, in device pixels, capped so a huge window cannot ask for a huge render.
        var (left, top) = ToWorld(new Point(0, 0));
        var width = (int)Math.Ceiling(Bounds.Width * renderScaling);
        var height = (int)Math.Ceiling(Bounds.Height * renderScaling);
        var shrink = Math.Max(1.0, Math.Max(width, height) / (double)MaxDetailPixels);
        width = Math.Max(1, (int)(width / shrink));
        height = Math.Max(1, (int)(height / shrink));
        var region = new WorldMapRegion(left, top, metersPerDevicePixel * shrink);

        var cts = new CancellationTokenSource();
        _detailCts = cts;
        _ = RenderDetailAsync(renderer, region, width, height, cts.Token);
    }

    private async Task RenderDetailAsync(WorldMapRenderer renderer, WorldMapRegion region, int width, int height, CancellationToken token)
    {
        try
        {
            var pixels = await Task.Run(() =>
            {
                var buffer = new uint[width * height];
                renderer.Render(region, width, height, buffer, token);
                return buffer;
            }, token);

            if (token.IsCancellationRequested || !ReferenceEquals(renderer, Renderer)) return;
            _detailBitmap = ToBitmap(pixels, width, height);
            _detailRegion = region;
            _detailWidth = width;
            _detailHeight = height;
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static WriteableBitmap ToBitmap(uint[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var source = MemoryMarshal.Cast<uint, byte>(pixels.AsSpan());
        var rowBytes = width * 4;
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(source.Slice(row * rowBytes, rowBytes).ToArray(), 0, frame.Address + row * frame.RowBytes, rowBytes);
        }
        return bitmap;
    }

    /// <summary>
    /// One pixel per exploration cell: clear where explored, opaque parchment elsewhere, like the in-game map. Drawn
    /// scaled up with smoothing, so the edge of the discovered area is soft rather than blocky.
    /// </summary>
    private void BuildFog()
    {
        _fogBitmap = null;
        _fogExploration = Exploration;
        if (_fogExploration is not { } exploration) return;

        const uint fog = 0xFFD8C49A; // parchment
        var size = exploration.Size;
        var pixels = new uint[size * size];
        for (var i = 0; i < pixels.Length; i++)
        {
            if (exploration.Explored[i] == 0) pixels[i] = fog;
        }
        _fogBitmap = ToBitmap(pixels, size, size);
    }

    // ===== Drawing =====

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.Transparent, bounds); // hit-testable everywhere

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            if (_baseBitmap is { } baseBitmap)
            {
                var size = baseBitmap.PixelSize;
                context.DrawImage(baseBitmap, new Rect(0, 0, size.Width, size.Height), ToScreen(_baseRegion, size.Width, size.Height));
            }

            if (_detailBitmap is { } detail)
            {
                context.DrawImage(detail, new Rect(0, 0, _detailWidth, _detailHeight), ToScreen(_detailRegion, _detailWidth, _detailHeight));
            }

            if (ShowFog && _fogBitmap is { } fog && _fogExploration is { } exploration && _baseBitmap is not null)
            {
                var topLeft = ToScreen(exploration.Left, exploration.Top);
                var side = exploration.Size * exploration.MetersPerCell * _scale;
                var radius = WorldMapRenderer.MapRadius * _scale;
                var centre = ToScreen(0, 0);
                using (context.PushGeometryClip(new EllipseGeometry(new Rect(centre.X - radius, centre.Y - radius, 2 * radius, 2 * radius))))
                {
                    var fogSize = fog.PixelSize;
                    context.DrawImage(fog, new Rect(0, 0, fogSize.Width, fogSize.Height), new Rect(topLeft.X, topLeft.Y, side, side));
                }
            }
        }

        if (_baseBitmap is not null) DrawMarkers(context);

        if (_baseBitmap is null && !string.IsNullOrEmpty(PlaceholderText))
        {
            var text = MakeText(PlaceholderText, 14, Parchment);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
        }

        DrawButtons(context);
        if (_baseBitmap is not null)
        {
            DrawCursorReadout(context);
            DrawScaleBar(context);
        }
    }

    private void DrawMarkers(DrawingContext context)
    {
        var markers = Markers;
        if (markers is not null)
        {
            // Lesser markers first, so bosses and traders sit on top.
            for (var pass = 0; pass < 2; pass++)
            {
                foreach (var marker in markers)
                {
                    var important = marker.Kind is WorldLocationKind.Spawn or WorldLocationKind.Boss or WorldLocationKind.Trader;
                    if (important != (pass == 1)) continue;

                    var p = ToScreen(marker.X, marker.Z);
                    if (p.X < -20 || p.Y < -20 || p.X > Bounds.Width + 20 || p.Y > Bounds.Height + 20) continue;
                    DrawMarker(context, marker, p);
                }
            }

            if (_scale >= LabelScale)
            {
                foreach (var marker in markers)
                {
                    if (marker.Kind is not (WorldLocationKind.Spawn or WorldLocationKind.Boss or WorldLocationKind.Trader)) continue;
                    if (ReferenceEquals(marker, _hovered)) continue;
                    DrawLabel(context, marker.Name, ToScreen(marker.X, marker.Z));
                }
            }
        }

        if (Focus is { } focus)
        {
            var p = ToScreen(focus.X, focus.Z);
            context.DrawEllipse(null, FocusPen, p, 13, 13);
        }

        if (_hovered is { } hovered) DrawLabel(context, hovered.Name, ToScreen(hovered.X, hovered.Z));
    }

    private static void DrawMarker(DrawingContext context, WorldMapMarker marker, Point p)
    {
        switch (marker.Kind)
        {
            case WorldLocationKind.Spawn:
                context.DrawGeometry(SpawnBrush, GoldPen, Polygon(p, 8, 4, Math.PI / 2));
                break;
            case WorldLocationKind.Boss:
                context.DrawEllipse(BossBrush, GoldPen, p, 6.5, 6.5);
                context.DrawEllipse(Ink, null, p, 2, 2);
                break;
            case WorldLocationKind.Trader:
                context.DrawEllipse(TraderBrush, InkPen, p, 6, 6);
                break;
            case WorldLocationKind.Dungeon:
                context.DrawRectangle(DungeonBrush, InkPen, new Rect(p.X - 4.5, p.Y - 4.5, 9, 9));
                break;
            case WorldLocationKind.Camp:
                context.DrawGeometry(CampBrush, InkPen, Polygon(p, 6.5, 3, Math.PI / 2));
                break;
        }
    }

    private static StreamGeometry Polygon(Point centre, double radius, int sides, double startAngle)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        for (var i = 0; i < sides; i++)
        {
            var a = startAngle + i * 2 * Math.PI / sides;
            var point = new Point(centre.X + radius * Math.Cos(a), centre.Y - radius * Math.Sin(a));
            if (i == 0) ctx.BeginFigure(point, true);
            else ctx.LineTo(point);
        }
        ctx.EndFigure(true);
        return geometry;
    }

    private static void DrawLabel(DrawingContext context, string name, Point anchor)
    {
        var text = MakeText(name, 12, Parchment);
        var box = new Rect(anchor.X - text.Width / 2 - 4, anchor.Y - 12 - text.Height - 2, text.Width + 8, text.Height + 2);
        context.DrawRectangle(PanelBrush, null, box, 3, 3);
        context.DrawText(text, new Point(box.X + 4, box.Y + 1));
    }

    private void DrawCursorReadout(DrawingContext context)
    {
        if (_pointer is not { } pointer) return;
        var (x, z) = ToWorld(pointer);
        var text = MakeText(string.Format(CultureInfo.CurrentCulture, Strings.Map_Cursor, x, z), 12, Parchment);
        var box = new Rect(8, Bounds.Height - text.Height - 12, text.Width + 12, text.Height + 4);
        context.DrawRectangle(PanelBrush, null, box, 3, 3);
        context.DrawText(text, new Point(box.X + 6, box.Y + 2));
    }

    private void DrawScaleBar(DrawingContext context)
    {
        // The longest round distance that fits in ~140 DIPs.
        double[] steps = { 10, 20, 50, 100, 200, 500, 1000, 2000, 5000 };
        var metres = steps[0];
        foreach (var step in steps)
        {
            if (step * _scale <= 140) metres = step;
        }
        var length = metres * _scale;
        var text = MakeText(string.Format(CultureInfo.CurrentCulture, Strings.Map_Search_Distance, metres), 12, Parchment);
        var box = new Rect(Bounds.Width - length - 24, Bounds.Height - text.Height - 22, length + 16, text.Height + 14);
        context.DrawRectangle(PanelBrush, null, box, 3, 3);
        var y = box.Bottom - 6;
        var x0 = box.X + 8;
        context.DrawLine(GoldPen, new Point(x0, y), new Point(x0 + length, y));
        context.DrawLine(GoldPen, new Point(x0, y - 4), new Point(x0, y));
        context.DrawLine(GoldPen, new Point(x0 + length, y - 4), new Point(x0 + length, y));
        context.DrawText(text, new Point(box.X + (box.Width - text.Width) / 2, box.Y + 2));
    }

    private static FormattedText MakeText(string text, double size, IBrush brush)
        => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelTypeface, size, brush);

    // ===== Overlay buttons (top-right): zoom in, zoom out, reset view, expand =====

    private Rect ButtonRect(MapButton button)
    {
        var index = button switch
        {
            MapButton.ZoomIn => 0,
            MapButton.ZoomOut => 1,
            MapButton.Reset => 2,
            _ => 3,
        };
        return new Rect(Bounds.Width - ButtonSize - 8, 8 + index * (ButtonSize + 4), ButtonSize, ButtonSize);
    }

    private MapButton HitButton(Point p)
    {
        foreach (var button in new[] { MapButton.ZoomIn, MapButton.ZoomOut, MapButton.Reset, MapButton.Expand })
        {
            if (ButtonRect(button).Contains(p)) return button;
        }
        return MapButton.None;
    }

    private void DrawButtons(DrawingContext context)
    {
        foreach (var button in new[] { MapButton.ZoomIn, MapButton.ZoomOut, MapButton.Reset, MapButton.Expand })
        {
            var r = ButtonRect(button);
            context.DrawRectangle(button == _hoveredButton ? PanelHoverBrush : PanelBrush, new Pen(Gold, 1), r, 3, 3);
            var c = r.Center;
            switch (button)
            {
                case MapButton.ZoomIn:
                    context.DrawLine(ButtonGlyphPen, new Point(c.X - 6, c.Y), new Point(c.X + 6, c.Y));
                    context.DrawLine(ButtonGlyphPen, new Point(c.X, c.Y - 6), new Point(c.X, c.Y + 6));
                    break;
                case MapButton.ZoomOut:
                    context.DrawLine(ButtonGlyphPen, new Point(c.X - 6, c.Y), new Point(c.X + 6, c.Y));
                    break;
                case MapButton.Reset:
                    context.DrawEllipse(null, ButtonGlyphPen, c, 6, 6);
                    context.DrawEllipse(Gold, null, c, 1.5, 1.5);
                    break;
                case MapButton.Expand:
                    // Corner brackets pointing out (expand) or in (restore).
                    var d = IsExpanded ? -1 : 1;
                    foreach (var (sx, sy) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) })
                    {
                        var corner = new Point(c.X + sx * (IsExpanded ? 2 : 7), c.Y + sy * (IsExpanded ? 2 : 7));
                        context.DrawLine(ButtonGlyphPen, corner, new Point(corner.X - sx * 4 * d, corner.Y));
                        context.DrawLine(ButtonGlyphPen, corner, new Point(corner.X, corner.Y - sy * 4 * d));
                    }
                    break;
            }
        }
    }

    // ===== Input =====

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Renderer is null) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.25, e.Delta.Y));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        var position = point.Position;
        var button = HitButton(position);
        if (button != MapButton.None)
        {
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
            switch (button)
            {
                case MapButton.ZoomIn: ZoomAt(centre, 2); break;
                case MapButton.ZoomOut: ZoomAt(centre, 0.5); break;
                case MapButton.Reset: ResetView(); break;
                case MapButton.Expand: IsExpanded = !IsExpanded; break;
            }
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2 && Renderer is not null)
        {
            ZoomAt(position, 2);
            e.Handled = true;
            return;
        }

        _dragStart = position;
        _dragStartCenter = (_centerX, _centerZ);
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        _pointer = position;

        if (_dragStart is { } start)
        {
            var dx = position.X - start.X;
            var dy = position.Y - start.Y;
            if (Math.Abs(dx) + Math.Abs(dy) > 2) _dragged = true;
            _centerX = _dragStartCenter.X - dx / _scale;
            _centerZ = _dragStartCenter.Z + dy / _scale;
            OnViewChanged();
            return;
        }

        _hoveredButton = HitButton(position);
        _hovered = _hoveredButton == MapButton.None ? HitMarker(position) : null;
        Cursor = _hovered is not null || _hoveredButton != MapButton.None
            ? _handCursor ??= new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragStart is null) return;

        // A click (not a drag) on a marker centres the map on it.
        if (!_dragged && HitMarker(e.GetPosition(this)) is { } marker)
        {
            Focus = new WorldMapFocus(marker.X, marker.Z);
        }

        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _pointer = null;
        _hovered = null;
        _hoveredButton = MapButton.None;
        InvalidateVisual();
    }

    private WorldMapMarker? HitMarker(Point p)
    {
        if (_baseBitmap is null || Markers is not { } markers) return null;

        WorldMapMarker? best = null;
        var bestDistance = MarkerHitRadius * MarkerHitRadius;
        foreach (var marker in markers)
        {
            var s = ToScreen(marker.X, marker.Z);
            var d = (s.X - p.X) * (s.X - p.X) + (s.Y - p.Y) * (s.Y - p.Y);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = marker;
            }
        }
        return best;
    }
}
