using System;
using Microsoft.UI.Xaml;

namespace FluentCode.Services.Appearance;

/// <summary>Native-element-owned baselines survive recreation of WinUI's managed wrappers.</summary>
public sealed class AppTextSizeRefreshPlan
{
    private static readonly DependencyProperty TextMetricProperty = DependencyProperty.RegisterAttached(
        "TextMetric", typeof(object), typeof(AppTextSizeRefreshPlan), new PropertyMetadata(null));
    private static readonly DependencyProperty LineMetricProperty = DependencyProperty.RegisterAttached(
        "LineMetric", typeof(object), typeof(AppTextSizeRefreshPlan), new PropertyMetadata(null));
    private readonly DependencyProperty _metricProperty;

    public AppTextSizeRefreshPlan(bool lineHeight = false)
    {
        _metricProperty = lineHeight ? LineMetricProperty : TextMetricProperty;
    }

    public double Resolve(DependencyObject consumer, double current, double scale)
    {
        // VisualTreeHelper can return a new managed wrapper for the same native element after GC.
        // A ConditionalWeakTable loses its entry in that case and scales the already-scaled value
        // again on every subsequent wrapper recreation. Store the state in the native DP store.
        if (consumer.GetValue(_metricProperty) is not Metric metric)
        {
            metric = new Metric(current);
            consumer.SetValue(_metricProperty, metric);
        }
        // A renderer can replace an authored size on an existing element.
        if (Math.Abs(current - metric.Last) > 0.001) metric.Baseline = current;
        metric.Last = metric.Baseline * scale;
        return metric.Last;
    }

    private sealed class Metric(double baseline)
    {
        public double Baseline = baseline;
        public double Last = baseline;
    }
}
