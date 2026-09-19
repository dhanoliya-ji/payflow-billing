using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace PayFlow.Web.Diagnostics;

/// <summary>
/// Accumulates the PayFlow meter's measurements and renders them in the Prometheus text
/// exposition format at <c>/metrics</c>.
/// <para>
/// Written by hand rather than pulled in as a dependency because the OpenTelemetry
/// Prometheus exporter is still pre-release; this is a hundred lines against an exposition
/// format that has been stable for a decade, and it swaps out cleanly once that package
/// ships. It listens to the PayFlow meter only, so runtime instrumentation does not leak
/// in uninvited.
/// </para>
/// </summary>
public sealed class PrometheusExporter : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentDictionary<string, InstrumentState> _state = new(StringComparer.Ordinal);

    public PrometheusExporter()
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PayFlowMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags));

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            Record(instrument, measurement, tags));

        _listener.Start();
    }

    public string Render()
    {
        var builder = new StringBuilder();

        foreach (var (name, state) in _state.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var metricName = Sanitize(name);

            builder.Append("# HELP ").Append(metricName).Append(' ').AppendLine(state.Description);
            builder.Append("# TYPE ").Append(metricName).AppendLine(state.IsHistogram ? " summary" : " counter");

            foreach (var (labels, value) in state.Series.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                builder.Append(metricName);

                if (labels.Length > 0)
                {
                    builder.Append('{').Append(labels).Append('}');
                }

                builder.Append(' ')
                    .Append(value.Sum.ToString("G17", CultureInfo.InvariantCulture))
                    .AppendLine();

                if (state.IsHistogram)
                {
                    builder.Append(metricName).Append("_count");
                    if (labels.Length > 0)
                    {
                        builder.Append('{').Append(labels).Append('}');
                    }

                    builder.Append(' ').Append(value.Count).AppendLine();
                }
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    public void Dispose()
    {
        _listener.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Record<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var value = Convert.ToDouble(measurement, CultureInfo.InvariantCulture);

        var state = _state.GetOrAdd(
            instrument.Name,
            _ => new InstrumentState(instrument.Description ?? instrument.Name, instrument is Histogram<double>));

        var labels = FormatLabels(tags);

        state.Series.AddOrUpdate(
            labels,
            _ => new SeriesValue(value, 1),
            (_, existing) => new SeriesValue(existing.Sum + value, existing.Count + 1));
    }

    private static string FormatLabels(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return "";
        }

        var parts = new List<string>(tags.Length);
        foreach (var tag in tags)
        {
            var value = tag.Value?.ToString()?.Replace("\"", "\\\"", StringComparison.Ordinal) ?? "";
            parts.Add($"{Sanitize(tag.Key)}=\"{value}\"");
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>Prometheus names allow only letters, digits and underscores.</summary>
    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return builder.ToString();
    }

    private sealed record SeriesValue(double Sum, long Count);

    private sealed class InstrumentState(string description, bool isHistogram)
    {
        public string Description { get; } = description;

        public bool IsHistogram { get; } = isHistogram;

        public ConcurrentDictionary<string, SeriesValue> Series { get; } = new(StringComparer.Ordinal);
    }
}
