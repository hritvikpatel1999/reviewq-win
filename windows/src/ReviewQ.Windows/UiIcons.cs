using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ReviewQ.Windows;

public static class UiIcons
{
    public static FrameworkElement Create(string name, double size = 20, bool accent = false)
    {
        var geometry = name switch
        {
            "repository" => "M6,8 L6,18 M18,8 L18,10 C18,14 6,12 6,16 M3,5 A3,3 0 1 0 9,5 A3,3 0 1 0 3,5 M15,5 A3,3 0 1 0 21,5 A3,3 0 1 0 15,5 M3,21 A3,3 0 1 0 9,21 A3,3 0 1 0 3,21",
            "people" => "M4,7 A3,3 0 1 0 10,7 A3,3 0 1 0 4,7 M15,7 A3,3 0 1 0 21,7 A3,3 0 1 0 15,7 M1,21 C1,12 13,12 13,21 Z M14,15 C19,12 24,16 23,21 L16,21",
            "author" => "M8,6 A4,4 0 1 0 16,6 A4,4 0 1 0 8,6 M3,23 C3,12 21,12 21,23 Z",
            "diagnostics" => "M0,12 L4,12 L6,6 L9,20 L12,1 L15,23 L18,7 L20,12 L24,12",
            "logs" => "M5,1 L15,1 L21,7 L21,23 L5,23 Z M15,1 L15,7 L21,7 M8,11 L17,11 M8,15 L17,15 M8,19 L15,19",
            "export" => "M12,1 L12,16 M6,10 L12,16 L18,10 M3,16 L3,23 L21,23 L21,16",
            "folder" => "M2,6 L2,3 L10,3 L13,6 L22,6 L22,21 L2,21 Z",
            "trash" => "M3,5 L21,5 M8,5 L8,1 L16,1 L16,5 M5,5 L6,23 L18,23 L19,5 M10,9 L10,19 M14,9 L14,19",
            "edge" => "M1,12 A11,11 0 1 0 23,12 A11,11 0 1 0 1,12 M1,12 L23,12 M12,1 C3,5 3,19 12,23 C21,19 21,5 12,1",
            "copilot" => "M5,7 C5,1 19,1 19,7 M3,7 L21,7 L23,16 L18,22 L6,22 L1,16 Z M4,10 L10,10 L10,15 L4,15 Z M14,10 L20,10 L20,15 L14,15 Z M9,19 L15,19",
            "scout" => "M12,1 C12,8 8,12 1,12 C8,12 12,16 12,23 C12,16 16,12 23,12 C16,12 12,8 12,1 Z",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        var path = new Path
        {
            Data = Geometry.Parse(geometry), StrokeThickness = 1.65,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform, Width = size, Height = size, IsHitTestVisible = false
        };
        if (accent && !SystemParameters.HighContrast) path.Stroke = Brushes.DodgerBlue;
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
}
