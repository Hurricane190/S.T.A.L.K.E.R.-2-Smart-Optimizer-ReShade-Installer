using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class ReportService
{
    public sealed class ReportPaths
    {
        public string Html { get; set; } = "";
        public string Markdown { get; set; } = "";
        public string Json { get; set; } = "";
    }

    public ReportPaths Generate(ScanResult scan, MeasurementSummary? measurement = null, OptimizationPlan? plan = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"ZoneTune-Report-{stamp}";
        Directory.CreateDirectory(AppPaths.ReportsDir);
        var html = Path.Combine(AppPaths.ReportsDir, baseName + ".html");
        var md = Path.Combine(AppPaths.ReportsDir, baseName + ".md");
        var json = Path.Combine(AppPaths.ReportsDir, baseName + ".json");

        var report = new DiagnosticReport
        {
            Id = baseName,
            Hardware = scan.Hardware,
            Game = scan.Game,
            Config = scan.Config,
            ReShade = scan.ReShade,
            Mods = scan.Mods,
            ModConflicts = scan.ModConflicts,
            Findings = scan.Findings,
            Measurement = measurement,
            Summary = BuildSummary(scan, measurement)
        };
        report.GeneratedFiles.AddRange(new[] { html, md, json });

        IniDocument.WriteTextAtomic(json, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        IniDocument.WriteTextAtomic(md, BuildMarkdown(scan, measurement, plan));
        IniDocument.WriteTextAtomic(html, BuildHtml(scan, measurement, plan));
        Log.Info($"Report exported: {html}");
        return new ReportPaths { Html = html, Markdown = md, Json = json };
    }

    private static string BuildSummary(ScanResult scan, MeasurementSummary? m)
    {
        var critical = scan.Findings.Count(f => f.Severity == Severity.Critical);
        var warnings = scan.Findings.Count(f => f.Severity == Severity.Warning);
        var text = $"{critical} critical, {warnings} warning(s). {scan.Hardware.GpuName}; {scan.Hardware.CpuName}; " +
                   $"{scan.Hardware.PrimaryResolution} @ {scan.Hardware.PrimaryRefreshHz} Hz. {scan.Mods.Count(m => m.Enabled)} mod components.";
        if (m != null) text += $" Last capture: {m.AverageFps:0.0} FPS average, {m.OnePercentLowFps:0.0} FPS 1% low.";
        return text;
    }

    private static string BuildMarkdown(ScanResult scan, MeasurementSummary? m, OptimizationPlan? plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ZoneTune Diagnostic Report");
        sb.AppendLine();
        sb.AppendLine($"Generated **{DateTime.Now:yyyy-MM-dd HH:mm}** by {AppPaths.ProductFullName} v{AppPaths.Version}.");
        sb.AppendLine();
        sb.AppendLine("> " + BuildSummary(scan, m));
        sb.AppendLine();
        sb.AppendLine("## Hardware");
        sb.AppendLine($"- **CPU:** {scan.Hardware.CpuSummary}");
        sb.AppendLine($"- **GPU:** {scan.Hardware.GpuSummary} ({scan.Hardware.GpuVendor}, driver {scan.Hardware.GpuDriverVersion})");
        sb.AppendLine($"- **RAM:** {scan.Hardware.RamSummary}");
        sb.AppendLine($"- **OS:** {scan.Hardware.WindowsSummary}");
        sb.AppendLine($"- **Display(s):** {string.Join("; ", scan.Hardware.Displays.Select(d => $"{d.MonitorName} {d.Summary}{(d.IsPrimary ? " (primary)" : "")}"))}");
        sb.AppendLine($"- **Game drive:** {scan.Hardware.StorageSummary}");
        sb.AppendLine();
        sb.AppendLine("## Game");
        sb.AppendLine($"- **Install:** `{scan.Game.Root}`");
        sb.AppendLine($"- **Source:** {scan.Game.Source}");
        sb.AppendLine($"- **Shipping exe:** `{scan.Game.ShippingExePath}`");
        sb.AppendLine($"- **Config:** `{scan.Game.ConfigDir}`");
        sb.AppendLine();
        sb.AppendLine("### Configuration snapshot");
        sb.AppendLine($"- Resolution: {scan.Config.ResolutionX}x{scan.Config.ResolutionY}, {scan.Config.FullscreenMode}");
        sb.AppendLine($"- Frame cap: {(scan.Config.FrameRateLimit <= 0 ? "unlimited" : scan.Config.FrameRateLimit.ToString("0.##") + " FPS")}; VSync {(scan.Config.VSync ? "on" : "off")}; dynamic res {(scan.Config.DynamicResolution ? "on" : "off")}");
        sb.AppendLine($"- HDR configured: {(scan.Config.HdrConfigured ? "yes" : "no")}; OptimaxP: {(scan.Config.HasOptimaxConfig ? "yes" : "no")}; S2AE warmup skipped: {(scan.Config.S2aeShaderWarmupSkipped ? "yes" : "no")}");
        sb.AppendLine($"- Scalability: {string.Join(", ", scan.Config.ScalabilityGroups.Select(kv => $"{kv.Key}={kv.Value}"))}");
        sb.AppendLine();
        sb.AppendLine("## ReShade");
        if (!scan.ReShade.Installed)
        {
            sb.AppendLine("Not installed.");
        }
        else
        {
            sb.AppendLine($"- Version: {scan.ReShade.Version} via `{scan.ReShade.ProxyDll}`");
            sb.AppendLine($"- Preset: `{scan.ReShade.PresetName}` ({scan.ReShade.PresetTechniques.Count} techniques)");
            sb.AppendLine($"- Shaders: {scan.ReShade.ShaderCount}; presets: {scan.ReShade.PresetCount}; errors: {scan.ReShade.EffectErrors}");
            sb.AppendLine($"- HDR conflict: {(scan.ReShade.HdrConflict ? "yes" : "no")}");
            if (scan.ReShade.ShaderReferences.Any(s => !s.Present))
                sb.AppendLine($"- Missing shaders: {string.Join(", ", scan.ReShade.ShaderReferences.Where(s => !s.Present).Select(s => s.File).Distinct())}");
        }
        sb.AppendLine();
        sb.AppendLine("## Installed mod components");
        if (scan.Mods.Count == 0) sb.AppendLine("None found.");
        foreach (var mod in scan.Mods.OrderByDescending(m => m.Enabled).ThenBy(m => m.Name))
            sb.AppendLine($"- {(mod.Enabled ? "ON " : "OFF")} **{mod.Name}** · {mod.KindText} · {mod.SizeText}" + (string.IsNullOrEmpty(mod.Notes) ? "" : $" — {mod.Notes}"));
        sb.AppendLine();
        if (scan.ModConflicts.Count > 0)
        {
            sb.AppendLine("### Conflict notes");
            foreach (var c in scan.ModConflicts)
                sb.AppendLine($"- **{c.Title}** ({c.Severity}): {c.Detail} `{string.Join(", ", c.Mods.Take(6))}`");
            sb.AppendLine();
        }
        sb.AppendLine("## Findings");
        foreach (var f in scan.Findings)
        {
            sb.AppendLine($"### [{f.Severity}] {f.Title}");
            sb.AppendLine(f.Detail);
            if (!string.IsNullOrWhiteSpace(f.Evidence)) sb.AppendLine($"- Evidence: `{f.Evidence}`");
            if (!string.IsNullOrWhiteSpace(f.Recommendation)) sb.AppendLine($"- Next: {f.Recommendation}");
            sb.AppendLine();
        }
        if (m != null)
        {
            sb.AppendLine("## Measurement (PresentMon import)");
            sb.AppendLine($"- Frames: {m.Frames}; duration: {m.DurationSeconds:0.0} s");
            sb.AppendLine($"- Average: {m.AverageFps:0.0} FPS; 1% low: {m.OnePercentLowFps:0.0} FPS; 0.1% low: {m.PointOnePercentLowFps:0.0} FPS");
            sb.AppendLine($"- Median frame time: {m.MedianFrameMs:0.00} ms; p95: {m.P95FrameMs:0.00} ms; p99: {m.P99FrameMs:0.00} ms; max: {m.MaxFrameMs:0.00} ms");
            sb.AppendLine($"- Stutter index (p99/median − 1): {m.StutterScore:0.0} (lower is better)");
            sb.AppendLine($"- Source: `{m.SourceFile}`");
            sb.AppendLine();
        }
        if (plan != null)
        {
            sb.AppendLine("## Optimization plan");
            foreach (var a in plan.Actions)
                sb.AppendLine($"- [{(a.Selected ? "x" : " ")}] **{a.Title}** — {a.ChangeText}; {a.EstimatedEffect}");
            sb.AppendLine();
        }
        sb.AppendLine("---");
        sb.AppendLine("Effects are estimates unless a before/after measurement is included. ZoneTune does not promise FPS gains.");
        return sb.ToString();
    }

    private static string BuildHtml(ScanResult scan, MeasurementSummary? m, OptimizationPlan? plan)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine("<title>ZoneTune Diagnostic Report</title>");
        sb.AppendLine("""
<style>
:root{--bg:#0b0f0c;--panel:#121814;--line:#27332a;--text:#dfe7dc;--dim:#93a394;--amber:#e7a33c;--green:#9ed36a;--red:#e06c5f}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:15px/1.55 "Segoe UI",system-ui,sans-serif}
.wrap{max-width:1080px;margin:0 auto;padding:40px 28px 80px}
h1{font-size:30px;letter-spacing:.5px;margin:0 0 4px}h2{font-size:19px;margin:34px 0 12px;color:var(--amber);letter-spacing:.4px;text-transform:uppercase}
h3{font-size:15px;margin:18px 0 6px}.sub{color:var(--dim);margin-bottom:26px}
.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:10px;padding:14px 16px}
.card b{display:block;color:var(--dim);font-size:11px;text-transform:uppercase;letter-spacing:.8px;margin-bottom:4px}
table{width:100%;border-collapse:collapse;font-size:14px}td,th{border-bottom:1px solid var(--line);padding:8px 6px;text-align:left;vertical-align:top}
th{color:var(--dim);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.5px}
.badge{display:inline-block;padding:1px 8px;border-radius:999px;font-size:11px;font-weight:700;letter-spacing:.5px;text-transform:uppercase}
.critical{background:#3a1714;color:var(--red)}.warning{background:#3a2c14;color:var(--amber)}.notice{background:#1c2a1a;color:var(--green)}.info{background:#18231c;color:var(--dim)}
.finding{border-left:3px solid var(--line);padding:6px 0 6px 14px;margin:14px 0}.finding.critical{border-color:var(--red)}.finding.warning{border-color:var(--amber)}.finding.notice{border-color:var(--green)}
.evidence{font-family:Consolas,monospace;font-size:12px;color:var(--dim);background:#0e1410;border:1px solid var(--line);border-radius:6px;padding:8px 10px;margin-top:6px;overflow-wrap:anywhere}
.muted{color:var(--dim)}code{font-family:Consolas,monospace;font-size:12px;color:#cfe0c8}
.note{border:1px dashed var(--line);border-radius:8px;padding:12px 14px;color:var(--dim);font-size:13px;margin-top:20px}
@media print{body{background:#fff;color:#111}.card,.finding{border-color:#ccc}.evidence{background:#f4f4f4;color:#333}h2{color:#111}.badge{border:1px solid #999}}
</style>
""");
        sb.AppendLine("</head><body><div class=\"wrap\">");
        sb.AppendLine("<h1>ZoneTune Diagnostic Report</h1>");
        sb.AppendLine($"<div class=\"sub\">Generated {DateTime.Now:yyyy-MM-dd HH:mm} · {E(AppPaths.ProductFullName)} v{AppPaths.Version} · {E(BuildSummary(scan, m))}</div>");

        sb.AppendLine("<div class=\"grid\">");
        Card(sb, "GPU", $"{scan.Hardware.GpuName}", $"{HardwareInfo.FormatGb(scan.Hardware.VramMb)} VRAM · driver {scan.Hardware.GpuDriverVersion}");
        Card(sb, "CPU", scan.Hardware.CpuName, $"{scan.Hardware.PhysicalCores}C / {scan.Hardware.LogicalCores}T");
        Card(sb, "Memory", scan.Hardware.RamSummary, scan.Hardware.WindowsSummary);
        Card(sb, "Display", scan.Hardware.PrimaryResolution, $"{scan.Hardware.PrimaryRefreshHz} Hz · HDR {(scan.Hardware.HdrEnabled ? "on" : scan.Hardware.AnyHdr ? "capable" : "no")}");
        Card(sb, "Storage", scan.Hardware.StorageSummary, scan.Hardware.GameDriveName);
        Card(sb, "Game", scan.Game.Source.ToString(), scan.Game.Root);
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>Configuration</h2><table><tr><th>Setting</th><th>Value</th></tr>");
        Row(sb, "Resolution", $"{scan.Config.ResolutionX}x{scan.Config.ResolutionY} ({scan.Config.FullscreenMode})");
        Row(sb, "Frame cap", scan.Config.FrameRateLimit <= 0 ? "unlimited" : $"{scan.Config.FrameRateLimit:0.##} FPS");
        Row(sb, "VSync / dynamic resolution", $"{(scan.Config.VSync ? "on" : "off")} / {(scan.Config.DynamicResolution ? "on" : "off")}");
        Row(sb, "HDR", scan.Config.HdrConfigured ? "configured on" : "off");
        Row(sb, "Scalability", string.Join(", ", scan.Config.ScalabilityGroups.Select(kv => $"{kv.Key}={kv.Value}")));
        Row(sb, "OptimaxP / S2AE warmup", $"{(scan.Config.HasOptimaxConfig ? "OptimaxP present" : "no OptimaxP")} / {(scan.Config.S2aeShaderWarmupSkipped ? "warmup skipped" : "warmup normal")}");
        sb.AppendLine("</table>");

        sb.AppendLine("<h2>ReShade</h2>");
        if (!scan.ReShade.Installed) sb.AppendLine("<p class=\"muted\">Not installed.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>Item</th><th>Value</th></tr>");
            Row(sb, "Version / proxy", $"{scan.ReShade.Version} / {scan.ReShade.ProxyDll}");
            Row(sb, "Preset", $"{scan.ReShade.PresetName} ({scan.ReShade.PresetTechniques.Count} techniques)");
            Row(sb, "Shaders / presets", $"{scan.ReShade.ShaderCount} / {scan.ReShade.PresetCount}");
            Row(sb, "Errors / warnings", $"{scan.ReShade.EffectErrors} / {scan.ReShade.EffectWarnings}");
            Row(sb, "HDR conflict", scan.ReShade.HdrConflict ? "yes" : "no");
            sb.AppendLine("</table>");
            if (scan.ReShade.ShaderReferences.Any(s => !s.Present))
                sb.AppendLine($"<p class=\"badge warning\">Missing shaders</p><p>{E(string.Join(", ", scan.ReShade.ShaderReferences.Where(s => !s.Present).Select(s => s.File).Distinct()))}</p>");
        }

        if (m != null)
        {
            sb.AppendLine("<h2>Measurement</h2><div class=\"grid\">");
            Card(sb, "Average FPS", $"{m.AverageFps:0.0}", $"{m.Frames} frames / {m.DurationSeconds:0.0} s");
            Card(sb, "1% low", $"{m.OnePercentLowFps:0.0} FPS", "average of worst 1% of frames");
            Card(sb, "0.1% low", $"{m.PointOnePercentLowFps:0.0} FPS", "average of worst 0.1%");
            Card(sb, "p99 frame time", $"{m.P99FrameMs:0.00} ms", $"median {m.MedianFrameMs:0.00} ms");
            Card(sb, "Stutter index", $"{m.StutterScore:0.0}", "p99/median − 1, lower is better");
            Card(sb, "Max frame", $"{m.MaxFrameMs:0.00} ms", "single worst frame");
            sb.AppendLine("</div>");
            if (m.FrameTimesMs.Count > 1) sb.AppendLine(Sparkline(m.FrameTimesMs, m.MedianFrameMs));
            sb.AppendLine($"<p class=\"muted\">Source: <code>{E(m.SourceFile)}</code></p>");
        }
        else
        {
            sb.AppendLine("<h2>Measurement</h2><div class=\"note\">No PresentMon capture imported. Capture the same route before and after a change, then import the CSV in Diagnostic Reports. Without that, performance effects remain estimates.</div>");
        }

        sb.AppendLine("<h2>Mods</h2>");
        if (scan.Mods.Count == 0) sb.AppendLine("<p class=\"muted\">None found.</p>");
        else
        {
            sb.AppendLine("<table><tr><th>State</th><th>Name</th><th>Type</th><th>Size</th><th>Notes</th></tr>");
            foreach (var mod in scan.Mods.OrderByDescending(x => x.Enabled).ThenBy(x => x.Name))
                sb.AppendLine($"<tr><td>{(mod.Enabled ? "ON" : "OFF")}</td><td>{E(mod.Name)}</td><td>{E(mod.KindText)}</td><td>{E(mod.SizeText)}</td><td class=\"muted\">{E(mod.Notes)}</td></tr>");
            sb.AppendLine("</table>");
        }

        if (scan.ModConflicts.Count > 0)
        {
            sb.AppendLine("<h2>Conflict notes</h2>");
            foreach (var c in scan.ModConflicts)
                sb.AppendLine($"<div class=\"finding warning\"><b>{E(c.Title)}</b><div>{E(c.Detail)}</div><div class=\"evidence\">{E(string.Join(", ", c.Mods.Take(10)))}</div></div>");
        }

        sb.AppendLine("<h2>Findings</h2>");
        foreach (var f in scan.Findings)
        {
            var sev = f.Severity.ToString().ToLowerInvariant();
            sb.AppendLine($"<div class=\"finding {sev}\"><span class=\"badge {sev}\">{E(f.Severity.ToString())}</span> <b>{E(f.Title)}</b>");
            sb.AppendLine($"<div>{E(f.Detail)}</div>");
            if (!string.IsNullOrWhiteSpace(f.Evidence)) sb.AppendLine($"<div class=\"evidence\">{E(f.Evidence)}</div>");
            if (!string.IsNullOrWhiteSpace(f.Recommendation)) sb.AppendLine($"<div class=\"muted\"><b>Next:</b> {E(f.Recommendation)}</div>");
            sb.AppendLine("</div>");
        }

        if (plan != null)
        {
            sb.AppendLine("<h2>Optimization plan</h2><table><tr><th>Action</th><th>Change</th><th>Effect</th></tr>");
            foreach (var a in plan.Actions)
                sb.AppendLine($"<tr><td>{E(a.Title)}</td><td><code>{E(a.ChangeText)}</code></td><td class=\"muted\">{E(a.EstimatedEffect)}</td></tr>");
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<div class=\"note\">Effects are estimates unless a before/after measurement is included. ZoneTune does not promise FPS gains. " +
                      "This report contains local paths and hardware information; review before sharing.</div>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, string value, string sub) =>
        sb.AppendLine($"<div class=\"card\"><b>{WebUtility.HtmlEncode(label)}</b><div>{WebUtility.HtmlEncode(value)}</div><div class=\"muted\" style=\"font-size:12px\">{WebUtility.HtmlEncode(sub)}</div></div>");

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.AppendLine($"<tr><td>{WebUtility.HtmlEncode(label)}</td><td>{WebUtility.HtmlEncode(value)}</td></tr>");

    private static string Sparkline(List<double> values, double median)
    {
        const int w = 1000, h = 180, pad = 18;
        var max = values.Max();
        var min = values.Min();
        var range = Math.Max(0.1, max - min);
        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {w} {h}\" width=\"100%\" height=\"180\" role=\"img\" aria-label=\"Frame time graph\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#0e1410\" stroke=\"#27332a\"/>");
        var points = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            var x = pad + (w - 2 * pad) * i / (double)(values.Count - 1);
            var y = h - pad - (h - 2 * pad) * (values[i] - min) / range;
            points.Append($"{x.ToString("0.0", CultureInfo.InvariantCulture)},{y.ToString("0.0", CultureInfo.InvariantCulture)} ");
        }
        var my = h - pad - (h - 2 * pad) * (median - min) / range;
        sb.Append($"<polyline fill=\"none\" stroke=\"#9ed36a\" stroke-width=\"1.4\" points=\"{points}\"/>");
        sb.Append($"<line x1=\"{pad}\" y1=\"{my:0.0}\" x2=\"{w - pad}\" y2=\"{my:0.0}\" stroke=\"#e7a33c\" stroke-dasharray=\"5 5\" stroke-width=\"1\"/>");
        sb.Append($"<text x=\"{pad + 4}\" y=\"{my - 6:0.0}\" fill=\"#e7a33c\" font-size=\"11\" font-family=\"Consolas\">median {median:0.00} ms</text>");
        sb.Append($"<text x=\"{w - pad - 160}\" y=\"{h - 6}\" fill=\"#93a394\" font-size=\"11\" font-family=\"Consolas\">max {max:0.00} ms</text>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
