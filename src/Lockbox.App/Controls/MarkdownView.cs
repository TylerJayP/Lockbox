using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Lockbox.Core.Tools;

namespace Lockbox.App.Controls;

/// <summary>Shows release notes written in Markdown (see <see cref="MarkdownLite"/>). Read-only and never clickable.</summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public MarkdownView() => Spacing = 6;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty) Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var blocks = MarkdownLite.Parse(Markdown);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            bool afterList = i > 0 && blocks[i - 1].Kind is MarkdownLite.BlockKind.Bullet or MarkdownLite.BlockKind.Numbered;
            Children.Add(b.Kind switch
            {
                MarkdownLite.BlockKind.Heading => Text(b.Spans, size: b.Level <= 2 ? 16 : 14.5, weight: FontWeight.SemiBold,
                                                       margin: new Thickness(0, i == 0 ? 0 : 8, 0, 0)),
                MarkdownLite.BlockKind.Bullet => ListItem("•", b, afterList),
                MarkdownLite.BlockKind.Numbered => ListItem(b.Marker, b, afterList),
                MarkdownLite.BlockKind.Quote => new Border
                {
                    BorderBrush = Brush("Line"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2),
                    Child = Text(b.Spans, dim: true),
                },
                MarkdownLite.BlockKind.Code => new Border
                {
                    Background = Brush("Surface2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8),
                    Child = new TextBlock { Text = b.PlainText, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap },
                },
                MarkdownLite.BlockKind.Rule => new Border { Height = 1, Background = Brush("Line"), Margin = new Thickness(0, 6) },
                _ => Text(b.Spans, dim: true),
            });
        }
    }

    private Control ListItem(string marker, MarkdownLite.Block b, bool afterList)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Margin = new Thickness(4 + 18 * b.Level, afterList ? -2 : 0, 0, 0),
        };
        var dot = new TextBlock
        {
            Text = marker, Foreground = Brush("AccentBrush"), Margin = new Thickness(0, 0, 8, 0),
            MinWidth = marker.Length > 1 ? 18 : 0, VerticalAlignment = VerticalAlignment.Top,
        };
        var body = Text(b.Spans, dim: true);
        Grid.SetColumn(body, 1);
        grid.Children.Add(dot);
        grid.Children.Add(body);
        return grid;
    }

    private TextBlock Text(IReadOnlyList<MarkdownLite.Span> spans, double size = 14, FontWeight? weight = null, bool dim = false, Thickness margin = default)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = size * 1.5, Margin = margin,
            FontWeight = weight ?? FontWeight.Normal,
        };
        if (dim) tb.Foreground = Brush("TextDim");
        tb.Inlines ??= [];
        foreach (var s in spans)
        {
            var run = new Run(s.Text);
            if (s.Bold) { run.FontWeight = FontWeight.SemiBold; run.Foreground = Brush("TextMain"); }
            if (s.Italic) run.FontStyle = FontStyle.Italic;
            if (s.Code) { run.FontFamily = Mono; run.FontSize = size - 1; run.Foreground = Brush("TextMain"); }
            tb.Inlines.Add(run);
        }
        return tb;
    }

    private IBrush? Brush(string key) =>
        this.TryFindResource(key, out var v) && v is IBrush b ? b
        : Application.Current?.TryFindResource(key, out var a) == true && a is IBrush ab ? ab : null;
}
