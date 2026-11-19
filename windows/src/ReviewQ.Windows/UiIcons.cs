using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ReviewQ.Windows;

public static class UiIcons
{
    private static readonly IReadOnlyDictionary<string, BitmapSource> Artwork =
        new[] { "edge", "scout", "copilot" }.ToDictionary(name => name, name =>
        {
            var image = new BitmapImage(new Uri($"pack://application:,,,/ReviewQ;component/Assets/{name}.png"));
            image.Freeze();
            return (BitmapSource)image;
        });

    public static FrameworkElement Create(string name, double size = 20, bool accent = false)
    {
        if (!SystemParameters.HighContrast && name is "archive" or "restore")
            return FolderTile(size, name == "restore");
        if (!SystemParameters.HighContrast && Artwork.TryGetValue(name, out var image))
        {
            var icon = new Image { Source = image, Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            return icon;
        }
        var geometry = name switch
        {
            "repository" => "M6,8 L6,18 M18,8 L18,10 C18,14 6,12 6,16 M3,5 A3,3 0 1 0 9,5 A3,3 0 1 0 3,5 M15,5 A3,3 0 1 0 21,5 A3,3 0 1 0 15,5 M3,21 A3,3 0 1 0 9,21 A3,3 0 1 0 3,21",
            "people" => "M4,7 A3,3 0 1 0 10,7 A3,3 0 1 0 4,7 M15,7 A3,3 0 1 0 21,7 A3,3 0 1 0 15,7 M1,21 C1,12 13,12 13,21 Z M14,15 C19,12 24,16 23,21 L16,21",
            "author" => "M8,6 A4,4 0 1 0 16,6 A4,4 0 1 0 8,6 M3,23 C3,12 21,12 21,23 Z",
            "diagnostics" => "M0,12 L4,12 L6,6 L9,20 L12,1 L15,23 L18,7 L20,12 L24,12",
            "logs" => "M5,1 L15,1 L21,7 L21,23 L5,23 Z M15,1 L15,7 L21,7 M8,11 L17,11 M8,15 L17,15 M8,19 L15,19",
            "export" => "M12,1 L12,16 M6,10 L12,16 L18,10 M3,16 L3,23 L21,23 L21,16",
            "folder" => "M2,6 L2,3 L10,3 L13,6 L22,6 L22,21 L2,21 Z",
            "archive" => "M2,6 L2,3 L10,3 L13,6 L22,6 L22,21 L2,21 Z",
            "restore" => "M2,8 L7,3 L12,8 M7,3 L7,16 M14,7 L22,7 L22,22 L2,22 L2,14",
            "browser" => "M1,12 A11,11 0 1 0 23,12 A11,11 0 1 0 1,12 M1,12 L23,12 M12,1 C3,5 3,19 12,23 C21,19 21,5 12,1",
            "trash" => "M3,5 L21,5 M8,5 L8,1 L16,1 L16,5 M5,5 L6,23 L18,23 L19,5 M10,9 L10,19 M14,9 L14,19",
            "edge" => "M1,12 A11,11 0 1 0 23,12 A11,11 0 1 0 1,12 M1,12 L23,12 M12,1 C3,5 3,19 12,23 C21,19 21,5 12,1",
            "copilot" => "M5,7 C5,1 19,1 19,7 M3,7 L21,7 L23,16 L18,22 L6,22 L1,16 Z M4,10 L10,10 L10,15 L4,15 Z M14,10 L20,10 L20,15 L14,15 Z M9,19 L15,19",
            "scout" => "M12,1 C12,8 8,12 1,12 C8,12 12,16 12,23 C12,16 16,12 23,12 C16,12 12,8 12,1 Z",
            "code" => "M8,5 L1,12 L8,19 M16,5 L23,12 L16,19 M14,2 L10,22",
            "sources" => "M3,23 L3,2 L14,2 L14,23 M14,9 L21,9 L21,23 M7,6 L7,8 M10,6 L10,8 M7,12 L7,14 M10,12 L10,14 M7,18 L7,20 M10,18 L10,20 M17,12 L18,12 M17,17 L18,17 M1,23 L23,23",
            "key" => "M8,1 A6,6 0 1 0 8,13 A6,6 0 1 0 8,1 M8,13 L8,23 L12,23 L12,19 L15,19 L15,15 L12,15 M6,6 L6,7",
            "bell" => "M5,16 L5,9 C5,1 19,1 19,9 L19,16 L22,19 L2,19 Z M9,22 C10,24 14,24 15,22 M12,1 L12,3",
            "remove" => "M1,12 A11,11 0 1 0 23,12 A11,11 0 1 0 1,12 M6,12 L18,12",
            "refresh" => "M20,8 A9,9 0 1 0 21,16 M20,1 L20,8 L13,8",
            "app" => "M7,23 L7,2 M3,6 L7,2 L11,6 M7,10 C7,15 11,18 18,19 M14,15 L18,19 L14,22",
            "settings" => "M9,1 L15,1 L16,5 L20,5 L23,10 L20,13 L22,17 L18,22 L14,20 L11,23 L6,21 L6,17 L2,15 L2,9 L6,7 L6,3 Z M8,12 A4,4 0 1 0 16,12 A4,4 0 1 0 8,12",
            "power" => "M12,1 L12,12 M6,4 A10,10 0 1 0 18,4",
            "success" => "M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 M6,12 L10,16 L18,8",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var path = new Path
        {
            Data = Geometry.Parse(geometry), StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform, Width = size, Height = size, IsHitTestVisible = false
        };
        if (name == "success" && !SystemParameters.HighContrast) path.Stroke = Brushes.SeaGreen;
        else if (accent && !SystemParameters.HighContrast) path.Stroke = Brushes.DodgerBlue;
        else path.SetBinding(Shape.StrokeProperty, new Binding
        {
            Path = new PropertyPath(TextElement.ForegroundProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
        });
        return path;
    }

    public static StackPanel Label(string icon, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Create(icon, 17));
        panel.Children.Add(new TextBlock { Text = text, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
    private static FrameworkElement FolderTile(double size, bool restore)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRoundedRectangle(Brushes.White, null, new Rect(0, 0, 32, 32), 7, 7);
            context.DrawGeometry(Brushes.SteelBlue, null, Geometry.Parse("M5,12 V9 Q5,7 7,7 H12 L15,10 H25 Q27,10 27,12 V23 Q27,25 25,25 H7 Q5,25 5,23 Z"));
            context.DrawRoundedRectangle(Brushes.WhiteSmoke, new Pen(Brushes.LightSteelBlue, 0.6), new Rect(12, 6, 11, 16), 1, 1);
            context.DrawGeometry(new LinearGradientBrush(Colors.LightSkyBlue, Colors.DodgerBlue, 90), null,
                Geometry.Parse("M5,14 Q5,12 7,12 H12 L15,15 H25 Q27,15 27,17 V23 Q27,25 25,25 H7 Q5,25 5,23 Z"));
            if (restore)
                context.DrawGeometry(null, new Pen(Brushes.DarkSlateBlue, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                    Geometry.Parse("M12,19 L16,15 L20,19 M16,15 L16,23"));
        }
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return new Image { Source = image, Width = size, Height = size, IsHitTestVisible = false };
    }
}
