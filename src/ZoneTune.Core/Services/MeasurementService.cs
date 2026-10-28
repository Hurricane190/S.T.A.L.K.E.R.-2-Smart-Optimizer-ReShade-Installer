using System.Globalization;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

/// <summary>
/// Imports PresentMon CSV captures and computes frame-time statistics. ZoneTune does not
/// claim an improvement unless the user supplies before/after captures taken on this PC.
/// </summary>
public sealed class MeasurementService
{
    public MeasurementSummary ImportPresentMon(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length < 2) throw new InvalidDataException("CSV is empty.");

        var headerIndex = -1;
        string[] headers = Array.Empty<string>();
        for (var i = 0; i < Math.Min(20, lines.Length); i++)
        {
            var candidate = SplitCsv(lines[i]);
            if (candidate.Any(h => h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase) ||
                                   h.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase)))
            {
                headerIndex = i;
                headers = candidate;
                break;
            }
        }
        if (headerIndex < 0) throw new InvalidDataException("No PresentMon frame-time columns found (MsBetweenPresents / MsBetweenDisplayChange).");

        var ftIndex = Array.FindIndex(headers, h => h.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
        if (ftIndex < 0) ftIndex = Array.FindIndex(headers, h => h.Equals("MsBetweenDisplayChange", StringComparison.OrdinalIgnoreCase));
        var timeIndex = Array.FindIndex(headers, h => h.Equals("TimeInSeconds", StringComparison.OrdinalIgnoreCase));

        var times = new List<double>(capacity: Math.Min(lines.Length, 500_000));
        double firstTime = -1, lastTime = -1;
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var cols = SplitCsv(lines[i]);
            if (ftIndex >= cols.Length) continue;
            if (!double.TryParse(cols[ftIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
            if (ms <= 0 || ms > 1000) continue;
            times.Add(ms);
            if (timeIndex >= 0 && timeIndex < cols.Length &&
                double.TryParse(cols[timeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
            {
                if (firstTime < 0) firstTime = t;
                lastTime = t;
            }
        }
        if (times.Count < 30) throw new InvalidDataException("Fewer than 30 valid frames were found.");

        var sorted = times.OrderBy(v => v).ToList();
        var median = Percentile(sorted, 0.50);
        var p95 = Percentile(sorted, 0.95);
        var p99 = Percentile(sorted, 0.99);
        var worst1 = times.OrderByDescending(v => v).Take(Math.Max(1, times.Count / 100)).Average();
        var worst01 = times.OrderByDescending(v => v).Take(Math.Max(1, times.Count / 1000)).Average();
        var duration = firstTime >= 0 && lastTime > firstTime ? lastTime - firstTime : times.Sum() / 1000.0;

        var summary = new MeasurementSummary
        {
            SourceFile = path,
            Frames = times.Count,
            DurationSeconds = duration,
            AverageFps = 1000.0 / times.Average(),
            OnePercentLowFps = 1000.0 / worst1,
            PointOnePercentLowFps = 1000.0 / worst01,
            MedianFrameMs = median,
            P95FrameMs = p95,
            P99FrameMs = p99,
            MaxFrameMs = times.Max(),
            StutterScore = Math.Max(0, (p99 / Math.Max(0.0001, median) - 1) * 100),
            FrameTimesMs = Downsample(times, 1200)
        };
        Log.Info($"Imported PresentMon capture {Path.GetFileName(path)}: {summary.Frames} frames, avg {summary.AverageFps:0.0} FPS, 1% low {summary.OnePercentLowFps:0.0}");
        return summary;
    }

    public static string Compare(MeasurementSummary before, MeasurementSummary after)
    {
        var lines = new List<string>
        {
            "Before → After (same scene, same settings except the change under test):",
            $"Average FPS: {before.AverageFps:0.0} → {after.AverageFps:0.0} ({(after.AverageFps - before.AverageFps) / Math.Max(0.1, before.AverageFps) * 100:+0.0;-0.0}%)",
            $"1% low FPS: {before.OnePercentLowFps:0.0} → {after.OnePercentLowFps:0.0} ({(after.OnePercentLowFps - before.OnePercentLowFps) / Math.Max(0.1, before.OnePercentLowFps) * 100:+0.0;-0.0}%)",
            $"0.1% low FPS: {before.PointOnePercentLowFps:0.0} → {after.PointOnePercentLowFps:0.0}",
            $"Median frame time: {before.MedianFrameMs:0.00} ms → {after.MedianFrameMs:0.00} ms",
            $"99th percentile frame time: {before.P99FrameMs:0.00} ms → {after.P99FrameMs:0.00} ms",
            $"Stutter index (lower is better): {before.StutterScore:0.0} → {after.StutterScore:0.0}"
        };
        lines.Add("Interpretation is only valid if both captures used the same route, graphics settings, background load and warm-up time.");
        return string.Join(Environment.NewLine, lines);
    }

    public static double Percentile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        var pos = (sorted.Count - 1) * q;
        var low = (int)Math.Floor(pos);
        var high = (int)Math.Ceiling(pos);
        if (low == high) return sorted[low];
        return sorted[low] + (sorted[high] - sorted[low]) * (pos - low);
    }

    private static List<double> Downsample(List<double> values, int max)
    {
        if (values.Count <= max) return values;
        var step = values.Count / (double)max;
        var result = new List<double>(max);
        for (var i = 0; i < max; i++) result.Add(values[(int)(i * step)]);
        return result;
    }

    private static string[] SplitCsv(string line)
    {
        // PresentMon does not quote commas in normal captures, but handle simple quotes.
        if (line.Contains('"'))
        {
            var result = new List<string>();
            var current = new System.Text.StringBuilder();
            var quoted = false;
            foreach (var c in line)
            {
                if (c == '"') quoted = !quoted;
                else if (c == ',' && !quoted) { result.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
            result.Add(current.ToString());
            return result.ToArray();
        }
        return line.Split(',');
    }
}
