using System.Text;

namespace ZoneTune.Core;

/// <summary>
/// Comment-preserving INI reader/writer. Unreal-style configs mix repeated keys,
/// inline comments and mixed casing, so edits are applied line-by-line rather than
/// by re-serialising the whole document.
/// </summary>
public sealed class IniDocument
{
    private enum LineKind { Blank, Comment, Section, KeyValue, Raw }

    private sealed class Line
    {
        public LineKind Kind;
        public string Raw = "";
        public string Section = "";
        public string Key = "";
        public string Value = "";
        public string Indent = "";
        public string InlineComment = "";
    }

    private readonly List<Line> _lines = new();
    public Encoding Encoding { get; private set; } = new UTF8Encoding(false);
    public bool HadBom { get; private set; }
    public string NewLine { get; private set; } = "\r\n";
    public string? SourcePath { get; private set; }

    public static IniDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            var empty = new IniDocument { SourcePath = path };
            return empty;
        }
        var bytes = File.ReadAllBytes(path);
        var (text, enc, bom) = Decode(bytes);
        var doc = Parse(text);
        doc.SourcePath = path;
        doc.Encoding = enc;
        doc.HadBom = bom;
        return doc;
    }

    public static IniDocument Parse(string text)
    {
        var doc = new IniDocument();
        if (text.Contains("\r\n", StringComparison.Ordinal)) doc.NewLine = "\r\n";
        else if (text.Contains('\n')) doc.NewLine = "\n";
        else doc.NewLine = "\r\n";

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var segments = normalized.Split('\n');
        string currentSection = "";

        foreach (var segment in segments)
        {
            var line = new Line { Raw = segment };
            var trimmed = segment.Trim();

            if (trimmed.Length == 0) line.Kind = LineKind.Blank;
            else if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) line.Kind = LineKind.Comment;
            else if (trimmed.StartsWith('[') && trimmed.Contains(']'))
            {
                var end = trimmed.IndexOf(']');
                currentSection = trimmed[1..end].Trim();
                line.Kind = LineKind.Section;
                line.Section = currentSection;
            }
            else
            {
                var eq = segment.IndexOf('=');
                if (eq > 0)
                {
                    line.Kind = LineKind.KeyValue;
                    line.Section = currentSection;
                    line.Indent = segment[..(segment.Length - segment.TrimStart().Length)];
                    var rawKey = segment[..eq].Trim();
                    line.Key = rawKey;
                    var rawValue = segment[(eq + 1)..];
                    var (value, comment) = SplitInlineComment(rawValue);
                    line.Value = value.Trim();
                    line.InlineComment = comment;
                }
                else
                {
                    line.Kind = LineKind.Raw;
                }
            }
            doc._lines.Add(line);
        }

        // A trailing newline produces one empty segment; keep exactly one sentinel out of
        // the model so Save can decide the ending deterministically.
        if (doc._lines.Count > 0 && doc._lines[^1].Kind == LineKind.Blank &&
            normalized.EndsWith('\n'))
        {
            doc._lines.RemoveAt(doc._lines.Count - 1);
        }
        return doc;
    }

    private static (string value, string comment) SplitInlineComment(string value)
    {
        var inQuotes = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '"') inQuotes = !inQuotes;
            else if (!inQuotes && (c == ';' || c == '#'))
            {
                // Only treat as comment when preceded by whitespace; paths can contain '#'
                if (i == 0 || char.IsWhiteSpace(value[i - 1]))
                    return (value[..i], value[i..]);
            }
        }
        return (value, "");
    }

    private static (string text, Encoding enc, bool bom) Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(false), true);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, true);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, true);

        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), new UTF8Encoding(false), false);
        }
        catch
        {
            return (Encoding.Latin1.GetString(bytes), Encoding.Latin1, false);
        }
    }

    public IReadOnlyList<string> Sections => _lines.Where(l => l.Kind == LineKind.Section).Select(l => l.Section).ToList();

    public bool HasSection(string section) => _lines.Any(l => l.Kind == LineKind.Section &&
        string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));

    public string? Get(string section, string key)
    {
        var line = _lines.LastOrDefault(l => l.Kind == LineKind.KeyValue
            && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
        return line?.Value;
    }

    public bool TryGetInt(string section, string key, out int value)
    {
        value = 0;
        var raw = Get(section, key);
        if (raw == null) return false;
        return int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetDouble(string section, string key, out double value)
    {
        value = 0;
        var raw = Get(section, key);
        if (raw == null) return false;
        return double.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    public bool TryGetBool(string section, string key, out bool value)
    {
        value = false;
        var raw = Get(section, key);
        if (raw == null) return false;
        var t = raw.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1") { value = true; return true; }
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase) || t == "0") { value = false; return true; }
        return bool.TryParse(t, out value);
    }

    /// <summary>Sets an existing key (first occurrence) or appends to the section, creating it if needed.</summary>
    public bool Set(string section, string key, string value, bool createSection = true)
    {
        var existing = _lines.FirstOrDefault(l => l.Kind == LineKind.KeyValue
            && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var changed = !string.Equals(existing.Value, value, StringComparison.Ordinal);
            if (changed) existing.Value = value;
            return changed;
        }

        var sectionIndex = _lines.FindLastIndex(l => l.Kind == LineKind.Section &&
            string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex < 0)
        {
            if (!createSection) return false;
            if (_lines.Count > 0 && _lines[^1].Kind != LineKind.Blank) _lines.Add(new Line { Kind = LineKind.Blank });
            _lines.Add(new Line { Kind = LineKind.Section, Section = section, Raw = $"[{section}]" });
            _lines.Add(new Line
            {
                Kind = LineKind.KeyValue,
                Section = section,
                Key = key,
                Value = value,
                Raw = $"{key}={value}"
            });
            return true;
        }

        // Insert after the last key/value in that section (before trailing blank lines).
        var insertAt = sectionIndex + 1;
        for (var i = sectionIndex + 1; i < _lines.Count; i++)
        {
            if (_lines[i].Kind == LineKind.Section) break;
            if (_lines[i].Kind is LineKind.KeyValue or LineKind.Comment) insertAt = i + 1;
        }
        _lines.Insert(insertAt, new Line
        {
            Kind = LineKind.KeyValue,
            Section = section,
            Key = key,
            Value = value,
            Raw = $"{key}={value}"
        });
        return true;
    }

    public bool Remove(string section, string key)
    {
        var idx = _lines.FindIndex(l => l.Kind == LineKind.KeyValue
            && string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return false;
        _lines.RemoveAt(idx);
        return true;
    }

    public Dictionary<string, string> GetSection(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in _lines.Where(l => l.Kind == LineKind.KeyValue &&
                     string.Equals(l.Section, section, StringComparison.OrdinalIgnoreCase)))
            result[line.Key] = line.Value;
        return result;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var line in _lines)
        {
            switch (line.Kind)
            {
                case LineKind.KeyValue:
                    sb.Append(line.Indent).Append(line.Key).Append('=').Append(line.Value);
                    if (!string.IsNullOrEmpty(line.InlineComment))
                    {
                        if (line.InlineComment.StartsWith(';') || line.InlineComment.StartsWith('#'))
                            sb.Append(' ');
                        sb.Append(line.InlineComment);
                    }
                    break;
                default:
                    sb.Append(line.Raw);
                    break;
            }
            sb.Append(NewLine);
        }
        return sb.ToString();
    }

    public void Save(string? path = null, bool createBackup = false)
    {
        var target = path ?? SourcePath ?? throw new InvalidOperationException("No path for INI document.");
        if (createBackup && File.Exists(target))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(target, $"{target}.zonetune-{stamp}.bak", overwrite: false);
        }
        SaveAtomic(target);
        SourcePath = target;
    }

    public void SaveAtomic(string target)
    {
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir ?? ".", $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        var preamble = HadBom ? Encoding.GetPreamble() : Array.Empty<byte>();
        using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(preamble);
            var body = Encoding.GetBytes(ToText());
            fs.Write(body);
            fs.Flush(true);
        }

        if (File.Exists(target))
        {
            // File.Replace preserves the destination ACLs where possible and is atomic.
            var backup = temp + ".replaced";
            try
            {
                File.Replace(temp, target, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
            }
            catch
            {
                File.Copy(temp, target, overwrite: true);
                TryDelete(temp);
            }
        }
        else
        {
            File.Move(temp, target);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static void WriteTextAtomic(string path, string text, Encoding? encoding = null)
    {
        var enc = encoding ?? new UTF8Encoding(false);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text, enc);
        if (File.Exists(path))
        {
            var bak = temp + ".replaced";
            try { File.Replace(temp, path, bak, ignoreMetadataErrors: true); TryDelete(bak); }
            catch { File.Copy(temp, path, overwrite: true); TryDelete(temp); }
        }
        else File.Move(temp, path);
    }
}
