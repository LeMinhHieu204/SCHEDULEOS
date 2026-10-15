using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.Controls;

public sealed record BoardColumn(string Key, string Title, string Subtitle, string? Initials, string? ColorHex, DateTime Date, int? StaffId, int? ResourceId, bool IsToday);

public sealed class BoardItem
{
    public required int AppointmentId { get; init; }
    public required string Reference { get; init; }
    public required int Version { get; init; }
    public int SortOrder { get; init; }
    public required int Column { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required DateTime AppointmentStart { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string? Detail { get; init; }
    public AppointmentStatus Status { get; init; }
    public string AccentHex { get; init; } = "#008C95";
    public bool HasConflict { get; init; }
    public bool CanMove { get; init; }
    public bool CanResize { get; init; }
    public bool IsSegment { get; init; }
    public string Tooltip { get; init; } = "";
}

public sealed record BoardBlock(int Column, DateTime Start, DateTime End, string Label, CalendarBlockKind Kind);

public sealed class BoardModel
{
    public IReadOnlyList<BoardColumn> Columns { get; init; } = [];
    public IReadOnlyList<BoardItem> Items { get; init; } = [];
    public IReadOnlyList<BoardBlock> Blocks { get; init; } = [];
    public int StartHour { get; init; } = 8;
    public int EndHour { get; init; } = 20;
    public int SlotMinutes { get; init; } = 15;
    public DateTime Now { get; init; } = DateTime.Now;
}

public sealed record SlotRequest(BoardColumn Column, DateTime Start);

public sealed record MoveRequest(BoardItem Item, BoardColumn TargetColumn, DateTime NewStart, int? NewDurationMinutes);

public sealed record ItemAction(string Action, BoardItem Item);

/// <summary>
/// Scheduling grid: time axis × columns (dates, staff or rooms). Supports click-to-create, click-to-open,
/// drag to reschedule, edge-drag to resize, live validation preview and keyboard alternatives
/// (Ctrl+Arrows move, Ctrl+Shift+Up/Down resize, Enter opens).
/// </summary>
public sealed class ScheduleBoard : Grid
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(BoardModel), typeof(ScheduleBoard),
        new PropertyMetadata(null, (d, _) => ((ScheduleBoard)d).Rebuild()));
    public static readonly DependencyProperty SlotCommandProperty = DependencyProperty.Register(nameof(SlotCommand), typeof(ICommand), typeof(ScheduleBoard));
    public static readonly DependencyProperty OpenCommandProperty = DependencyProperty.Register(nameof(OpenCommand), typeof(ICommand), typeof(ScheduleBoard));
    public static readonly DependencyProperty MoveCommandProperty = DependencyProperty.Register(nameof(MoveCommand), typeof(ICommand), typeof(ScheduleBoard));
    public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(nameof(ActionCommand), typeof(ICommand), typeof(ScheduleBoard));
    public static readonly DependencyProperty PreviewProviderProperty = DependencyProperty.Register(nameof(PreviewProvider), typeof(Func<MoveRequest, Task<string?>>), typeof(ScheduleBoard));
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double), typeof(ScheduleBoard),
        new PropertyMetadata(118.0, (d, _) => ((ScheduleBoard)d).Rebuild()));
    public static readonly DependencyProperty HourHeightProperty = DependencyProperty.Register(nameof(HourHeight), typeof(double), typeof(ScheduleBoard),
        new PropertyMetadata(68.0, (d, _) => ((ScheduleBoard)d).Rebuild()));

    private const double Gutter = 64;

    private const double HeaderHeight = 58;

    private readonly ScrollViewer _headerScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly ScrollViewer _bodyScroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Focusable = false };
    private readonly Canvas _header = new();
    private readonly Canvas _body = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly GridLines _lines = new();
    private readonly Dictionary<FrameworkElement, BoardItem> _itemOf = [];

    private double _colWidth = 118;
    private DragState? _drag;
    private bool _scrolledToNow;

    public ScheduleBoard()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _headerScroll.Content = _header;
        var bodyHost = new Grid();
        bodyHost.Children.Add(_lines);
        bodyHost.Children.Add(_body);
        bodyHost.Children.Add(_overlay);
        _bodyScroll.Content = bodyHost;
        SetRow(_headerScroll, 0);
        SetRow(_bodyScroll, 1);
        Children.Add(_headerScroll);
        Children.Add(_bodyScroll);
        _bodyScroll.ScrollChanged += (_, e) => _headerScroll.ScrollToHorizontalOffset(e.HorizontalOffset);
        _body.MouseLeftButtonUp += OnBodyMouseUp;
        _body.MouseMove += OnBodyMouseMove;
        _body.LostMouseCapture += (_, _) => CancelDrag();
        SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.PreviousSize.Width - e.NewSize.Width) > 1) Rebuild();
        };
        Focusable = false;
        AutomationProperties.SetAutomationId(this, "ScheduleBoard");
    }

    public BoardModel? Model { get => (BoardModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public ICommand? SlotCommand { get => (ICommand?)GetValue(SlotCommandProperty); set => SetValue(SlotCommandProperty, value); }
    public ICommand? OpenCommand { get => (ICommand?)GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }
    public ICommand? MoveCommand { get => (ICommand?)GetValue(MoveCommandProperty); set => SetValue(MoveCommandProperty, value); }
    public ICommand? ActionCommand { get => (ICommand?)GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
    public Func<MoveRequest, Task<string?>>? PreviewProvider { get => (Func<MoveRequest, Task<string?>>?)GetValue(PreviewProviderProperty); set => SetValue(PreviewProviderProperty, value); }
    public double HourHeight { get => (double)GetValue(HourHeightProperty); set => SetValue(HourHeightProperty, value); }
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    private double PxPerMinute => HourHeight / 60.0;

    private Brush Res(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    // ------------------------------------------------------------------ build

    private void Rebuild()
    {
        _header.Children.Clear();
        _body.Children.Clear();
        _overlay.Children.Clear();
        _itemOf.Clear();
        _drag = null;
        var m = Model;
        if (m is null || m.Columns.Count == 0) return;

        var available = Math.Max(0, ActualWidth - Gutter - 12);
        _colWidth = Math.Max(MinColumnWidth, m.Columns.Count == 0 ? MinColumnWidth : available / m.Columns.Count);
        var hours = Math.Max(1, m.EndHour - m.StartHour);
        var height = hours * HourHeight;
        var width = Gutter + _colWidth * m.Columns.Count;
        _body.Width = _overlay.Width = width;
        _body.Height = _overlay.Height = height;
        _header.Width = width + 12;
        _header.Height = HeaderHeight;
        _lines.Configure(m, _colWidth, Gutter, HourHeight, Res("Brush.GridLine"), Res("Brush.GridLineHour"), Res("Brush.TextSecondary"), Res("Brush.PrimarySoft"));

        BuildHeader(m);
        foreach (var b in m.Blocks) AddBlock(m, b);
        foreach (var group in m.Items.GroupBy(i => i.Column)) LayoutColumn(m, group.OrderBy(i => i.Start).ThenByDescending(i => i.End).ToList());
        AddNowLine(m);

        if (!_scrolledToNow)
        {
            _scrolledToNow = true;
            var target = m.Columns.Any(c => c.IsToday) ? m.Now : m.Now.Date.AddHours(9);
            var y = Math.Max(0, (target.TimeOfDay.TotalMinutes - m.StartHour * 60 - 60) * PxPerMinute);
            Dispatcher.BeginInvoke(() => _bodyScroll.ScrollToVerticalOffset(y), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void BuildHeader(BoardModel m)
    {
        for (var i = 0; i < m.Columns.Count; i++)
        {
            var c = m.Columns[i];
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (c.Initials is not null)
            {
                var color = Palette.Parse(c.ColorHex);
                panel.Children.Add(new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, 0, 9, 0),
                    Background = Palette.Frozen(Palette.Tint(color, 0.82)),
                    Child = new TextBlock { Text = c.Initials, FontWeight = FontWeights.SemiBold, FontSize = 11.5, Foreground = Palette.Frozen(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            }

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = c.Title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = c.IsToday ? Res("Brush.Primary") : Res("Brush.Text"), MaxWidth = _colWidth - (c.Initials is null ? 16 : 56),
            });
            texts.Children.Add(new TextBlock
            {
                Text = c.Subtitle, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = c.IsToday ? Res("Brush.Primary") : Res("Brush.TextSecondary"),
                MaxWidth = _colWidth - (c.Initials is null ? 16 : 56),
            });
            panel.Children.Add(texts);
            var cell = new Border
            {
                Width = _colWidth, Height = HeaderHeight, Padding = new Thickness(10, 0, 6, 0), BorderBrush = Res("Brush.Border"),
                BorderThickness = new Thickness(i == 0 ? 1 : 0, 0, 1, c.IsToday ? 0 : 1), Child = panel,
                Background = c.IsToday ? Res("Brush.PrimarySoft") : Brushes.Transparent,
            };
            if (c.Initials is null) panel.HorizontalAlignment = HorizontalAlignment.Center;
            if (c.IsToday)
            {
                var g = new Grid();
                g.Children.Add(cell);
                g.Children.Add(new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = Res("Brush.Primary"), CornerRadius = new CornerRadius(2) });
                Canvas.SetLeft(g, Gutter + i * _colWidth);
                _header.Children.Add(g);
            }
            else
            {
                Canvas.SetLeft(cell, Gutter + i * _colWidth);
                _header.Children.Add(cell);
            }
        }

        var corner = new TextBlock { Text = "Time", FontSize = 11, Foreground = Res("Brush.TextSecondary"), Width = Gutter, TextAlignment = TextAlignment.Center };
        Canvas.SetTop(corner, HeaderHeight / 2 - 8);
        _header.Children.Add(corner);
    }

    private double Y(BoardModel m, DateTime t, DateTime columnDate) =>
        ((t - columnDate.Date).TotalMinutes - m.StartHour * 60) * PxPerMinute;

    private void AddBlock(BoardModel m, BoardBlock b)
    {
        if (b.Column < 0 || b.Column >= m.Columns.Count) return;
        var col = m.Columns[b.Column];
        var top = Math.Max(0, Y(m, b.Start, col.Date));
        var bottom = Math.Min(_body.Height, Y(m, b.End, col.Date));
        if (bottom - top < 2) return;
        var danger = b.Kind is CalendarBlockKind.Maintenance or CalendarBlockKind.Closed or CalendarBlockKind.TimeOff;
        var showLabel = b.Kind != CalendarBlockKind.OffShift && bottom - top >= 22;
        var block = new Border
        {
            Width = _colWidth - 6, Height = bottom - top, CornerRadius = new CornerRadius(6),
            Background = b.Kind == CalendarBlockKind.OffShift ? Res("Brush.SurfaceAlt") : danger ? Res("Brush.HatchDanger") : Res("Brush.Hatch"),
            BorderBrush = danger ? Res("Brush.Danger") : Brushes.Transparent, BorderThickness = new Thickness(danger ? 1 : 0),
            Opacity = b.Kind == CalendarBlockKind.OffShift ? 0.75 : 1, IsHitTestVisible = false,
            Child = showLabel
                ? new TextBlock
                {
                    Text = (b.Kind == CalendarBlockKind.Break ? " " : danger ? " " : "") + b.Label, FontSize = 11, Margin = new Thickness(8, 5, 6, 0),
                    TextWrapping = TextWrapping.Wrap, Foreground = danger ? Res("Brush.Danger") : Res("Brush.TextSecondary"),
                    FontFamily = new FontFamily("Segoe UI, Segoe Fluent Icons, Segoe MDL2 Assets"),
                }
                : null,
        };
        Canvas.SetLeft(block, Gutter + b.Column * _colWidth + 3);
        Canvas.SetTop(block, top);
        _body.Children.Add(block);
    }

    private void LayoutColumn(BoardModel m, List<BoardItem> items)
    {
        // Greedy lane assignment for overlapping items, grouped into clusters.
        var cluster = new List<(BoardItem Item, int Lane)>();
        var laneEnds = new List<DateTime>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            var lanes = Math.Max(1, laneEnds.Count);
            foreach (var (item, lane) in cluster) AddItem(m, item, lane, lanes);
            cluster.Clear();
            laneEnds.Clear();
        }

        foreach (var item in items)
        {
            if (cluster.Count > 0 && item.Start >= clusterEnd) Flush();
            var lane = laneEnds.FindIndex(e => e <= item.Start);
            if (lane < 0)
            {
                laneEnds.Add(item.End);
                lane = laneEnds.Count - 1;
            }
            else laneEnds[lane] = item.End;

            cluster.Add((item, lane));
            if (item.End > clusterEnd || cluster.Count == 1) clusterEnd = item.End > clusterEnd ? item.End : clusterEnd;
        }

        Flush();
    }

    private void AddItem(BoardModel m, BoardItem item, int lane, int lanes)
    {
        var col = m.Columns[item.Column];
        var top = Y(m, item.Start, col.Date);
        var bottom = Y(m, item.End, col.Date);
        if (bottom <= 0 || top >= _body.Height) return;
        top = Math.Max(0, top);
        var height = Math.Max(22, Math.Min(_body.Height, bottom) - top - 2);
        var laneWidth = (_colWidth - 8) / lanes;
        var accent = Palette.Parse(item.AccentHex);
        var muted = item.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow;

        var content = new StackPanel { Margin = new Thickness(9, 5, 6, 4) };
        content.Children.Add(new TextBlock
        {
            Text = $"{item.Start:h:mm} – {item.End:h:mm tt}", FontSize = 10.5, Foreground = Palette.Frozen(Darken(accent)), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Res("Brush.Text"), TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = muted ? TextDecorations.Strikethrough : null,
        });
        if (height > 50)
            content.Children.Add(new TextBlock { Text = item.Subtitle, FontSize = 11.5, Foreground = Res("Brush.TextSecondary"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (height > 68 && item.Detail is not null)
            content.Children.Add(new TextBlock { Text = item.Detail, FontSize = 11, Foreground = Res("Brush.TextMuted"), TextTrimming = TextTrimming.CharacterEllipsis });

        var grid = new Grid();
        grid.Children.Add(new Border { Width = 3.5, HorizontalAlignment = HorizontalAlignment.Left, Background = Palette.Frozen(accent), CornerRadius = new CornerRadius(7, 0, 0, 7) });
        grid.Children.Add(content);
        if (item.HasConflict)
            grid.Children.Add(new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Res("Brush.Danger"), HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, -6, 0),
                Child = new TextBlock { Text = "!", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                ToolTip = "Conflict detected — open the appointment or run Conflict Check",
            });
        var statusDot = new Ellipse { Width = 7, Height = 7, Fill = Palette.Frozen(Palette.StatusColor(item.Status)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 8, 0) };
        if (!item.HasConflict) grid.Children.Add(statusDot);

        var card = new Border
        {
            Width = Math.Max(20, laneWidth - 3), Height = height, CornerRadius = new CornerRadius(7),
            Background = Palette.Frozen(Palette.Tint(accent, muted ? 0.94 : 0.86)), BorderBrush = Palette.Frozen(Palette.Tint(accent, 0.6)), BorderThickness = new Thickness(1),
            Child = grid, Cursor = item.CanMove ? Cursors.SizeAll : Cursors.Hand, Focusable = true, FocusVisualStyle = null, Opacity = muted ? 0.75 : 1,
            ToolTip = item.Tooltip, SnapsToDevicePixels = true,
        };
        if (item.Status == AppointmentStatus.Pending) card.BorderBrush = Res("Brush.Warning");
        AutomationProperties.SetName(card, $"{item.Reference} {item.Title} {item.Start:t}");
        AutomationProperties.SetAutomationId(card, "Appt_" + item.Reference + (item.IsSegment ? "_" + item.SortOrder : ""));
        ToolTipService.SetInitialShowDelay(card, 500);
        card.ContextMenu = BuildMenu(item);
        card.MouseLeftButtonDown += OnItemMouseDown;
        card.KeyDown += OnItemKeyDown;
        card.GotKeyboardFocus += (_, _) => card.BorderBrush = Res("Brush.Primary");
        card.LostKeyboardFocus += (_, _) => card.BorderBrush = item.Status == AppointmentStatus.Pending ? Res("Brush.Warning") : Palette.Frozen(Palette.Tint(accent, 0.6));
        _itemOf[card] = item;
        Canvas.SetLeft(card, Gutter + item.Column * _colWidth + 4 + lane * laneWidth);
        Canvas.SetTop(card, top + 1);
        _body.Children.Add(card);
    }

    private ContextMenu BuildMenu(BoardItem item)
    {
        var menu = new ContextMenu();
        void Add(string header, string action, string icon, string? gesture = null)
        {
            var mi = new MenuItem
            {
                Header = header, InputGestureText = gesture ?? "",
                Icon = new TextBlock { Text = icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
            };
            mi.Click += (_, _) => ExecuteAction(action, item);
            menu.Items.Add(mi);
        }

        Add("Open details", "open", "", "Enter");
        if (item.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed)
        {
            Add("Reschedule…", "reschedule", "");
            Add("Move 15 min earlier", "earlier", "", "Ctrl+Up");
            Add("Move 15 min later", "later", "", "Ctrl+Down");
        }

        menu.Items.Add(new Separator());
        if (item.Status == AppointmentStatus.Pending) Add("Confirm", "confirm", "");
        if (item.Status == AppointmentStatus.Confirmed) Add("Check in", "checkin", "");
        if (item.Status == AppointmentStatus.CheckedIn) Add("Start service", "start", "");
        if (item.Status == AppointmentStatus.InProgress) Add("Complete", "complete", "");
        if (item.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed or AppointmentStatus.CheckedIn) Add("Cancel…", "cancel", "");
        Add("Record payment", "payment", "");
        return menu;
    }

    private void ExecuteAction(string action, BoardItem item)
    {
        var m = Model;
        switch (action)
        {
            case "open":
                Execute(OpenCommand, item);
                break;
            case "earlier" or "later" when m is not null:
                var delta = (action == "earlier" ? -1 : 1) * m.SlotMinutes;
                Execute(MoveCommand, new MoveRequest(item, m.Columns[item.Column], item.AppointmentStart.AddMinutes(delta), null));
                break;
            default:
                Execute(ActionCommand, new ItemAction(action, item));
                break;
        }
    }

    private void AddNowLine(BoardModel m)
    {
        for (var i = 0; i < m.Columns.Count; i++)
        {
            if (m.Columns[i].Date.Date != m.Now.Date) continue;
            var y = Y(m, m.Now, m.Columns[i].Date);
            if (y < 0 || y > _body.Height) continue;
            var line = new Rectangle { Width = _colWidth, Height = 2, Fill = Res("Brush.Danger"), IsHitTestVisible = false };
            Canvas.SetLeft(line, Gutter + i * _colWidth);
            Canvas.SetTop(line, y);
            _body.Children.Add(line);
            var dot = new Ellipse { Width = 9, Height = 9, Fill = Res("Brush.Danger"), IsHitTestVisible = false };
            Canvas.SetLeft(dot, Gutter + i * _colWidth - 4.5);
            Canvas.SetTop(dot, y - 3.5);
            _body.Children.Add(dot);
        }
    }

    // ------------------------------------------------------------------ interaction

    private sealed class DragState
    {
        public required FrameworkElement Card { get; init; }
        public required BoardItem Item { get; init; }
        public required Point Origin { get; init; }
        public required bool Resize { get; init; }
        public bool Active { get; set; }
        public Border? Ghost { get; set; }
        public TextBlock? GhostLabel { get; set; }
        public MoveRequest? Pending { get; set; }
        public int PreviewToken { get; set; }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement card || !_itemOf.TryGetValue(card, out var item)) return;
        card.Focus();
        var pos = e.GetPosition(card);
        var resize = item.CanResize && card.ActualHeight - pos.Y <= 8;
        _drag = new DragState { Card = card, Item = item, Origin = e.GetPosition(_body), Resize = resize };
        _body.CaptureMouse();
        e.Handled = true;
    }

    private void OnBodyMouseMove(object sender, MouseEventArgs e)
    {
        var m = Model;
        if (_drag is not { } d || m is null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_body);
        if (!d.Active)
        {
            if ((p - d.Origin).Length < 5) return;
            if (!d.Item.CanMove && !d.Resize) return;
            d.Active = true;
            d.Card.Opacity = 0.35;
            d.GhostLabel = new TextBlock { FontSize = 11, Margin = new Thickness(8, 4, 6, 4), TextWrapping = TextWrapping.Wrap, Foreground = Res("Brush.Text") };
            d.Ghost = new Border
            {
                Width = _colWidth - 8, Height = d.Card.ActualHeight, CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(2),
                BorderBrush = Res("Brush.Primary"), Background = Res("Brush.Surface"), Opacity = 0.95, Child = d.GhostLabel,
            };
            _overlay.Children.Add(d.Ghost);
        }

        var snap = m.SlotMinutes;
        var deltaMinutes = Math.Round((p.Y - d.Origin.Y) / PxPerMinute / snap) * snap;
        var col = d.Resize ? d.Item.Column : Math.Clamp((int)((p.X - Gutter) / _colWidth), 0, m.Columns.Count - 1);
        var target = m.Columns[col];
        MoveRequest request;
        double top, height;
        var source = m.Columns[d.Item.Column];
        if (d.Resize)
        {
            var newMinutes = (int)Math.Max(snap, (d.Item.End - d.Item.Start).TotalMinutes + deltaMinutes);
            request = new MoveRequest(d.Item, target, d.Item.AppointmentStart, newMinutes);
            top = Y(m, d.Item.Start, source.Date);
            height = newMinutes * PxPerMinute;
        }
        else
        {
            var dayShift = target.Date.Date - source.Date.Date;
            var newSegmentStart = d.Item.Start.AddMinutes(deltaMinutes) + dayShift;
            request = new MoveRequest(d.Item, target, d.Item.AppointmentStart.AddMinutes(deltaMinutes) + dayShift, null);
            top = Y(m, newSegmentStart, target.Date);
            height = d.Card.ActualHeight;
        }

        Canvas.SetLeft(d.Ghost!, Gutter + col * _colWidth + 4);
        Canvas.SetTop(d.Ghost!, top);
        d.Ghost!.Height = Math.Max(22, height);
        if (d.Pending is { } prev && prev.TargetColumn == request.TargetColumn && prev.NewStart == request.NewStart && prev.NewDurationMinutes == request.NewDurationMinutes) return;
        d.Pending = request;
        var segStart = d.Resize ? d.Item.Start : d.Item.Start.AddMinutes(deltaMinutes) + (target.Date.Date - source.Date.Date);
        var segEnd = d.Resize ? d.Item.Start.AddMinutes(request.NewDurationMinutes!.Value) : segStart + (d.Item.End - d.Item.Start);
        d.GhostLabel!.Text = $"{segStart:h:mm} – {segEnd:h:mm tt}  ·  checking…";
        _ = PreviewAsync(d, request, segStart, segEnd);
    }

    private async Task PreviewAsync(DragState d, MoveRequest request, DateTime segStart, DateTime segEnd)
    {
        var token = ++d.PreviewToken;
        await Task.Delay(140);
        if (_drag != d || token != d.PreviewToken || PreviewProvider is null) return;
        string? problem;
        try
        {
            problem = await PreviewProvider(request);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (_drag != d || token != d.PreviewToken || d.Ghost is null) return;
        d.Ghost.BorderBrush = problem is null ? Res("Brush.Success") : Res("Brush.Danger");
        d.Ghost.Background = problem is null ? Res("Brush.SuccessLight") : Res("Brush.DangerLight");
        d.GhostLabel!.Text = $"{segStart:h:mm} – {segEnd:h:mm tt}  ·  {(problem is null ? "Available" : problem)}";
    }

    private void OnBodyMouseUp(object sender, MouseButtonEventArgs e)
    {
        var m = Model;
        if (m is null) return;
        if (_drag is { } d)
        {
            _drag = null;
            _body.ReleaseMouseCapture();
            _overlay.Children.Clear();
            d.Card.Opacity = 1;
            if (!d.Active)
            {
                Execute(OpenCommand, d.Item);
            }
            else if (d.Pending is { } req && (req.NewDurationMinutes is not null || req.NewStart != d.Item.AppointmentStart || req.TargetColumn != m.Columns[d.Item.Column]))
            {
                Execute(MoveCommand, req);
            }

            e.Handled = true;
            return;
        }

        // Click on an empty slot → create a booking there.
        var p = e.GetPosition(_body);
        if (p.X < Gutter) return;
        var col = (int)((p.X - Gutter) / _colWidth);
        if (col < 0 || col >= m.Columns.Count) return;
        var minutes = m.StartHour * 60 + Math.Floor(p.Y / PxPerMinute / m.SlotMinutes) * m.SlotMinutes;
        Execute(SlotCommand, new SlotRequest(m.Columns[col], m.Columns[col].Date.Date.AddMinutes(minutes)));
    }

    private void CancelDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        _overlay.Children.Clear();
        d.Card.Opacity = 1;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _drag is not null)
        {
            _body.ReleaseMouseCapture();
            CancelDrag();
            e.Handled = true;
        }

        base.OnPreviewKeyDown(e);
    }

    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement card || !_itemOf.TryGetValue(card, out var item) || Model is not { } m) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Enter:
                Execute(OpenCommand, item);
                e.Handled = true;
                break;
            case Key.Up or Key.Down when ctrl && shift && item.CanResize:
                var minutes = (int)(item.End - item.Start).TotalMinutes + (e.Key == Key.Up ? -m.SlotMinutes : m.SlotMinutes);
                if (minutes >= m.SlotMinutes) Execute(MoveCommand, new MoveRequest(item, m.Columns[item.Column], item.AppointmentStart, minutes));
                e.Handled = true;
                break;
            case Key.Up or Key.Down when ctrl && item.CanMove:
                Execute(MoveCommand, new MoveRequest(item, m.Columns[item.Column], item.AppointmentStart.AddMinutes(e.Key == Key.Up ? -m.SlotMinutes : m.SlotMinutes), null));
                e.Handled = true;
                break;
            case Key.Left or Key.Right when ctrl && item.CanMove:
                var target = Math.Clamp(item.Column + (e.Key == Key.Left ? -1 : 1), 0, m.Columns.Count - 1);
                if (target != item.Column)
                {
                    var shiftDays = m.Columns[target].Date.Date - m.Columns[item.Column].Date.Date;
                    Execute(MoveCommand, new MoveRequest(item, m.Columns[target], item.AppointmentStart + shiftDays, null));
                }

                e.Handled = true;
                break;
        }
    }

    private static void Execute(ICommand? command, object parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.62), (byte)(c.G * 0.62), (byte)(c.B * 0.62));

    /// <summary>Draws the time axis, slot/hour lines and column separators.</summary>
    private sealed class GridLines : FrameworkElement
    {
        private BoardModel? _m;
        private double _col, _gutter, _hour;
        private Brush _slot = Brushes.LightGray, _hourBrush = Brushes.Gray, _text = Brushes.Gray, _today = Brushes.Transparent;

        public GridLines() => IsHitTestVisible = false;

        public void Configure(BoardModel m, double col, double gutter, double hour, Brush slot, Brush hourBrush, Brush text, Brush today)
        {
            (_m, _col, _gutter, _hour, _slot, _hourBrush, _text, _today) = (m, col, gutter, hour, slot, hourBrush, text, today);
            Width = gutter + col * m.Columns.Count;
            Height = (m.EndHour - m.StartHour) * hour;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_m is null) return;
            var slotPen = new Pen(_slot, 1);
            var hourPen = new Pen(_hourBrush, 1);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var face = new Typeface("Segoe UI");
            for (var i = 0; i < _m.Columns.Count; i++)
                if (_m.Columns[i].IsToday) dc.DrawRectangle(_today, null, new Rect(_gutter + i * _col, 0, _col, Height));
            var slotsPerHour = Math.Max(1, 60 / _m.SlotMinutes);
            for (var h = _m.StartHour; h <= _m.EndHour; h++)
            {
                var y = (h - _m.StartHour) * _hour;
                dc.DrawLine(hourPen, new Point(_gutter, y + 0.5), new Point(Width, y + 0.5));
                if (h < _m.EndHour)
                {
                    for (var s = 1; s < slotsPerHour; s++)
                    {
                        var ys = y + _hour * s / slotsPerHour;
                        dc.DrawLine(slotPen, new Point(_gutter, ys + 0.5), new Point(Width, ys + 0.5));
                    }

                    var label = new FormattedText(new DateTime(1, 1, 1, h % 24, 0, 0).ToString("h:mm tt", CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10.5, _text, dpi);
                    dc.DrawText(label, new Point(_gutter - label.Width - 10, y + 3));
                }
            }

            for (var i = 0; i <= _m.Columns.Count; i++)
            {
                var x = _gutter + i * _col;
                dc.DrawLine(hourPen, new Point(x + 0.5, 0), new Point(x + 0.5, Height));
            }
        }
    }
}
