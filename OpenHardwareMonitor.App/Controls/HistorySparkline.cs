using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenHardwareMonitor.Core;
using Windows.Foundation;

namespace OpenHardwareMonitor.App.Controls;

public sealed class HistorySparkline : Canvas
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable<DataPoint>), typeof(HistorySparkline), new PropertyMetadata(null, OnPointsChanged));

    public HistorySparkline()
    {
        Height = 56;
        SizeChanged += (_, _) => Redraw();
    }

    public IEnumerable<DataPoint>? Points
    {
        get => (IEnumerable<DataPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    private static void OnPointsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((HistorySparkline)sender).Redraw();

    private void Redraw()
    {
        Children.Clear();
        var points = Points?.ToArray() ?? Array.Empty<DataPoint>();
        if (points.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var minimum = points.Min(point => point.Value);
        var maximum = points.Max(point => point.Value);
        var span = Math.Max(maximum - minimum, 0.001);
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            Points = new PointCollection()
        };
        for (var index = 0; index < points.Length; index++)
        {
            var x = index * ActualWidth / (points.Length - 1);
            var y = ActualHeight - ((points[index].Value - minimum) / span * (ActualHeight - 6)) - 3;
            line.Points.Add(new Point(x, y));
        }
        Children.Add(line);
    }
}
