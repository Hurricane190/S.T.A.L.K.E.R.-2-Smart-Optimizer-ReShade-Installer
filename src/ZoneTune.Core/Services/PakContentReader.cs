using System.Text;
using System.Text.RegularExpressions;
using ZoneTune.Core.Models;

namespace ZoneTune.Core.Services;

public sealed class PakAssetEntry
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Extension { get; set; } = "";
    public long UncompressedSize { get; set; }
    public string Container { get; set; } = "";
    public bool Heuristic { get; set; }

    public string Key => NormalizeKey(Path);

    public static string NormalizeKey(string path)
    {
        var value = (path ?? "").Replace('\\', '/').Trim();
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        value = Regex.Replace(value, "/{2,}", "/");
        var marker = value.IndexOf("Stalker2/Content/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) value = value[(marker + "Stalker2/Content/".Length)..];
        marker = value.IndexOf("Stalker2/Mods/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0) value = value[(marker + "Stalker2/Mods/".Length)..];
        return value.Trim('/').ToLowerInvariant();
    }
}

public sealed class PakContainerInfo
{
    public string Path { get; set; } = "";
    public string ContainerType { get; set; } = "";
    public long SizeBytes { get; set; }
    public long IndexBytesRead { get; set; }
    public bool Readable { get; set; }
    public bool IndexExact { get; set; }
    public string Error { get; set; } = "";
    public List<PakAssetEntry> Entries { get; set; } = new();
}

/// <summary>
/// Read-only reader for STALKER 2 / Unreal containers.
///
/// It reads only the small container index:
///   - IoStore .utoc: version 8 directory index (mount point, directory/file tables, string table).
///   - legacy .pak: FPakInfo footer + index.
///
/// It never reads .ucas payloads and never writes to the game folder. Corrupt or
/// encrypted indexes are reported as unreadable, never guessed at.
/// </summary>
public sealed class PakContentReader
{
    private const int MaxTocRead = 64 * 1024 * 1024;
    private const int MaxIndexRead = 32 * 1024 * 1024;
    private const int MaxEntries = 200_000;

    private static readonly Regex AssetStringRegex = new(
        @"(?i)(?:[A-Za-z0-9_.\-/\\]+\/)?[A-Za-z0-9_.\-]+\.(?:uasset|uexp|ubulk|umap|ufont|locres|ushaderbytecode|wem|bnk|ini|json|cfg|pak|lua|dll|asi)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uasset", ".uexp", ".ubulk", ".umap", ".ufont", ".locres", ".ushaderbytecode",
        ".wem", ".bnk", ".ini", ".json", ".cfg", ".pak", ".lua", ".dll", ".asi", ".txt", ".xml"
    };

    public PakContainerInfo Read(string path)
    {
        var info = new PakContainerInfo { Path = path, SizeBytes = SafeLength(path) };
        var ext = System.IO.Path.GetExtension(path);
        try
        {
            if (ext.Equals(".utoc", StringComparison.OrdinalIgnoreCase))
                return ReadIoStore(path, info);
            if (ext.Equals(".pak", StringComparison.OrdinalIgnoreCase))
                return ReadLegacyPak(path, info);
            info.Error = "Unsupported container type.";
            return info;
        }
        catch (Exception ex)
        {
            info.Readable = false;
            info.Error = ex.Message;
            Log.Warn($"Container inspection failed for {path}: {ex.Message}");
            return info;
        }
    }

    private PakContainerInfo ReadIoStore(string path, PakContainerInfo info)
    {
        info.ContainerType = "IoStore";
        var length = new FileInfo(path).Length;
        if (length <= 0)
        {
            info.Error = "Empty .utoc file.";
            return info;
        }
        if (length > MaxTocRead)
        {
            info.Error = $".utoc is larger than the {MaxTocRead / 1024 / 1024} MB safety limit; not read.";
            return info;
        }

        var data = File.ReadAllBytes(path);
        info.IndexBytesRead = data.Length;
        if (data.Length < 64 || !IsTocMagic(data))
        {
            info.Error = "Missing IoStore TOC magic.";
            return info;
        }

        var headerSize = ReadU32(data, 20);
        if (headerSize < 32 || headerSize > 4096) headerSize = 144;

        var candidates = new List<int>();
        var dirIndexSize = ReadU32(data, 48);
        if (dirIndexSize > 0 && dirIndexSize < data.Length)
            candidates.Add(data.Length - (int)dirIndexSize);

        // Known UE5 layout: header + TOC entries + compressed blocks + method names + optional arrays.
        var entryCount = ReadU32(data, 24);
        var blockCount = ReadU32(data, 28);
        var blockEntrySize = ReadU32(data, 32);
        var methodCount = ReadU32(data, 36);
        var methodNameLength = ReadU32(data, 40);
        if (entryCount is > 0 and < MaxEntries && blockCount < MaxEntries && blockEntrySize is >= 8 and <= 64 &&
            methodCount < 1024 && methodNameLength is > 0 and <= 256)
        {
            foreach (var entrySize in new[] { 24, 28, 32 })
            {
                var baseOffset = (long)headerSize + (long)entryCount * entrySize +
                                 (long)blockCount * blockEntrySize +
                                 (long)methodCount * methodNameLength;
                foreach (var extra in new[] { 0, 4, 8, 12, 16, 24 })
                {
                    var candidate = baseOffset + extra;
                    if (candidate > 16 && candidate < data.Length - 8) candidates.Add((int)candidate);
                }
            }
        }

        // The mount point is a length-prefixed string. This is robust across TOC versions.
        var ascii = Encoding.Latin1.GetString(data);
        foreach (Match match in Regex.Matches(ascii, @"(?:\.\./)+"))
        {
            var candidate = match.Index - 4;
            if (candidate < 0 || candidate + 8 >= data.Length) continue;
            var declared = ReadU32(data, candidate);
            if (declared is < 3 or > 512) continue;
            if (candidate + 4 + declared > data.Length) continue;
            candidates.Add(candidate);
        }

        var best = new List<PakAssetEntry>();
        var bestError = "IoStore directory index not found.";
        foreach (var candidate in candidates.Distinct().OrderBy(c => c))
        {
            if (TryParseIoStoreDirectory(data, candidate, path, out var entries, out var error))
            {
                if (entries.Count > best.Count)
                {
                    best = entries;
                    bestError = "";
                }
                if (entries.Count > 0) break;
            }
            else if (string.IsNullOrWhiteSpace(bestError)) bestError = error;
        }

        if (best.Count > 0)
        {
            info.Readable = true;
            info.IndexExact = true;
            info.Entries = best;
            return info;
        }

        // Last-resort heuristic: extract plausible asset strings from the TOC. This is
        // still useful for counts/asset categories, but it is explicitly not an exact index.
        var heuristic = ExtractAssetStrings(data, path);
        if (heuristic.Count > 0)
        {
            info.Readable = true;
            info.IndexExact = false;
            info.Entries = heuristic;
            info.Error = "Directory index could not be parsed; asset names were extracted heuristically.";
            return info;
        }

        info.Error = bestError;
        return info;
    }

    private PakContainerInfo ReadLegacyPak(string path, PakContainerInfo info)
    {
        info.ContainerType = "Legacy pak";
        var fileLength = new FileInfo(path).Length;
        if (fileLength < 64)
        {
            info.Error = "Pak file is too small.";
            return info;
        }

        var tailLength = (int)Math.Min(fileLength, 4096);
        var tail = new byte[tailLength];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(fileLength - tailLength, SeekOrigin.Begin);
            ReadExactly(fs, tail, 0, tailLength);
        }

        var magicAt = -1;
        for (var i = tailLength - 4; i >= 0; i--)
        {
            if (tail[i] == 0xE1 && tail[i + 1] == 0x12 && tail[i + 2] == 0x6F && tail[i + 3] == 0x5A)
            {
                magicAt = i;
                break;
            }
        }
        if (magicAt < 0 || magicAt + 24 > tailLength)
        {
            info.Error = "No Unreal pak footer found (pak may be encrypted, compressed or an IoStore stub).";
            return info;
        }

        var version = BitConverter.ToInt32(tail, magicAt + 4);
        var indexOffset = BitConverter.ToInt64(tail, magicAt + 8);
        var indexSize = BitConverter.ToInt64(tail, magicAt + 16);
        if (version is < 1 or > 20)
        {
            info.Error = $"Unsupported pak version {version}.";
            return info;
        }
        if (indexOffset < 0 || indexSize <= 0 || indexSize > MaxIndexRead ||
            indexOffset + indexSize > fileLength)
        {
            info.Error = "Pak index offset/size is outside the file (possibly encrypted or truncated).";
            return info;
        }

        var index = new byte[indexSize];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Seek(indexOffset, SeekOrigin.Begin);
            ReadExactly(fs, index, 0, (int)indexSize);
        }
        info.IndexBytesRead = indexSize;

        if (TryParseLegacyPakIndex(index, version, path, out var entries, out var error))
        {
            info.Readable = true;
            info.IndexExact = true;
            info.Entries = entries;
            return info;
        }

        var heuristic = ExtractAssetStrings(index, path);
        if (heuristic.Count > 0)
        {
            info.Readable = true;
            info.IndexExact = false;
            info.Entries = heuristic;
            info.Error = "Pak index could not be parsed; asset names were extracted heuristically.";
            return info;
        }

        info.Error = error;
        return info;
    }

    private bool TryParseIoStoreDirectory(byte[] data, int offset, string container,
        out List<PakAssetEntry> entries, out string error)
    {
        entries = new List<PakAssetEntry>();
        error = "";
        var position = offset;
        try
        {
            var mount = ReadFString(data, ref position, 1024);
            if (string.IsNullOrWhiteSpace(mount) || !mount.Contains('/') ||
                mount.Any(c => c < 0x20 || c > 0x7E))
            {
                error = "No IoStore mount point at candidate offset.";
                return false;
            }

            var directoryCount = ReadI32(data, ref position);
            if (directoryCount is < 1 or > 200_000) { error = "Directory count out of range."; return false; }
            if ((long)position + (long)directoryCount * 16 + 4 > data.Length) { error = "Directory table exceeds TOC."; return false; }

            var dirs = new (uint Name, uint FirstChild, uint NextSibling, uint FirstFile)[directoryCount];
            for (var i = 0; i < directoryCount; i++)
            {
                dirs[i] = (ReadU32(data, position), ReadU32(data, position + 4),
                           ReadU32(data, position + 8), ReadU32(data, position + 12));
                position += 16;
            }

            var fileCount = ReadI32(data, ref position);
            if (fileCount is < 0 or > 2_000_000) { error = "File count out of range."; return false; }
            if ((long)position + (long)fileCount * 12 + 4 > data.Length) { error = "File table exceeds TOC."; return false; }

            var files = new (uint Name, uint Next, uint UserData)[fileCount];
            for (var i = 0; i < fileCount; i++)
            {
                files[i] = (ReadU32(data, position), ReadU32(data, position + 4), ReadU32(data, position + 8));
                position += 12;
            }

            var stringCount = ReadI32(data, ref position);
            if (stringCount is < 1 or > 2_000_000) { error = "String count out of range."; return false; }
            var strings = new string[stringCount];
            for (var i = 0; i < stringCount; i++)
            {
                var value = ReadFString(data, ref position, 4096);
                if (value.Length == 0 && position >= data.Length) { error = "String table exceeds TOC."; return false; }
                strings[i] = value;
            }

            var parent = new int[directoryCount];
            Array.Fill(parent, -1);
            for (var i = 0; i < directoryCount; i++)
            {
                var child = dirs[i].FirstChild;
                var guard = 0;
                while (child != uint.MaxValue && child < directoryCount && guard++ < directoryCount)
                {
                    if (parent[child] < 0) parent[child] = i;
                    child = dirs[child].NextSibling;
                }
            }

            string DirectoryPath(int index)
            {
                var parts = new List<string>();
                var current = index;
                var guard = 0;
                while (current >= 0 && current < directoryCount && guard++ < 128)
                {
                    var name = dirs[current].Name;
                    if (name != uint.MaxValue && name < strings.Length && strings[name].Length > 0)
                        parts.Insert(0, strings[name]);
                    current = parent[current];
                }
                return string.Join("/", parts);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < directoryCount && entries.Count < MaxEntries; i++)
            {
                var fileIndex = dirs[i].FirstFile;
                var guard = 0;
                var dirPath = DirectoryPath(i);
                while (fileIndex != uint.MaxValue && fileIndex < fileCount && guard++ < fileCount)
                {
                    var nameIndex = files[fileIndex].Name;
                    if (nameIndex < strings.Length && strings[nameIndex].Length > 0)
                    {
                        var name = strings[nameIndex];
                        var virtualPath = NormalizePath(mount.TrimEnd('/') + "/" + dirPath.Trim('/') + "/" + name);
                        if (seen.Add(virtualPath))
                        {
                            entries.Add(new PakAssetEntry
                            {
                                Path = virtualPath,
                                FileName = System.IO.Path.GetFileName(name),
                                Extension = System.IO.Path.GetExtension(name),
                                Container = container
                            });
                        }
                    }
                    fileIndex = files[fileIndex].Next;
                }
            }

            if (entries.Count == 0)
            {
                error = "Directory index parsed but contained no files.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "IoStore index parse failed: " + ex.Message;
            return false;
        }
    }

    private bool TryParseLegacyPakIndex(byte[] index, int version, string container,
        out List<PakAssetEntry> entries, out string error)
    {
        entries = new List<PakAssetEntry>();
        error = "";
        var position = 0;
        try
        {
            var mount = ReadFString(index, ref position, 1024);
            if (string.IsNullOrWhiteSpace(mount) || mount.Any(c => c < 0x20 || c > 0x7E))
            {
                error = "Pak index mount point is unreadable (encrypted index?).";
                return false;
            }

            var count = ReadI32(index, ref position);
            if (count is < 0 or > 2_000_000)
            {
                error = "Pak entry count out of range.";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < count && entries.Count < MaxEntries; i++)
            {
                var name = ReadFString(index, ref position, 4096);
                if (string.IsNullOrWhiteSpace(name)) { error = "Pak entry name is empty."; return false; }

                var offset = ReadI64(index, ref position);
                var size = ReadI64(index, ref position);
                var uncompressed = ReadI64(index, ref position);
                uint methodIndex;
                if (version >= 8)
                {
                    methodIndex = ReadU32(index, position);
                    position += 4;
                    position += 20; // SHA-1 hash
                }
                else
                {
                    position += 20; // SHA-1 hash
                    methodIndex = version >= 3 ? ReadU32(index, position) : 0;
                    if (version >= 3) position += 4;
                    if (version < 3) position += 8; // timestamp
                }

                if (version >= 3)
                {
                    if (methodIndex != 0)
                    {
                        var blockCount = ReadI32(index, ref position);
                        if (blockCount is < 0 or > 1_000_000) { error = "Compression block count out of range."; return false; }
                        var skip = (long)blockCount * 16;
                        if (skip > index.Length - position) { error = "Compression blocks exceed index."; return false; }
                        position += (int)skip;
                    }
                    position += 1; // flags
                    position += 4; // compression block size
                }

                if (offset < 0 || size < 0 || uncompressed < 0) { error = "Pak entry offset/size is invalid."; return false; }

                var virtualPath = NormalizePath(mount.TrimEnd('/') + "/" + name.TrimStart('/'));
                if (seen.Add(virtualPath))
                {
                    entries.Add(new PakAssetEntry
                    {
                        Path = virtualPath,
                        FileName = System.IO.Path.GetFileName(name),
                        Extension = System.IO.Path.GetExtension(name),
                        UncompressedSize = uncompressed,
                        Container = container
                    });
                }
            }

            if (entries.Count == 0)
            {
                error = "Pak index parsed but contained no files.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = "Pak index parse failed: " + ex.Message;
            return false;
        }
    }

    private static List<PakAssetEntry> ExtractAssetStrings(byte[] data, string container)
    {
        var result = new List<PakAssetEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = Encoding.Latin1.GetString(data);
        foreach (Match match in AssetStringRegex.Matches(text))
        {
            var value = match.Value.Replace('\\', '/').Trim('.', '/');
            if (value.Length > 512) continue;
            if (!AssetExtensions.Contains(System.IO.Path.GetExtension(value))) continue;
            var path = NormalizePath(value);
            if (!seen.Add(path)) continue;
            result.Add(new PakAssetEntry
            {
                Path = path,
                FileName = System.IO.Path.GetFileName(path),
                Extension = System.IO.Path.GetExtension(path),
                Container = container,
                Heuristic = true
            });
            if (result.Count >= MaxEntries) break;
        }
        return result;
    }

    private static bool IsTocMagic(byte[] data)
    {
        const string magic = "-==--==--==--==-";
        if (data.Length < magic.Length) return false;
        for (var i = 0; i < magic.Length; i++)
            if (data[i] != (byte)magic[i]) return false;
        return true;
    }

    private static string NormalizePath(string path)
    {
        var value = (path ?? "").Replace('\\', '/');
        value = Regex.Replace(value, "/{2,}", "/");
        return value.Trim();
    }

    private static string ReadFString(byte[] data, ref int position, int maxLength)
    {
        if (position + 4 > data.Length) throw new EndOfStreamException();
        var length = BitConverter.ToInt32(data, position);
        position += 4;
        if (length == 0) return "";
        if (length > 0)
        {
            if (length > maxLength * 2 || position + length > data.Length) throw new EndOfStreamException();
            var value = Encoding.UTF8.GetString(data, position, length);
            position += length;
            return value.TrimEnd('\0');
        }
        var chars = -length;
        if (chars > maxLength || position + chars * 2 > data.Length) throw new EndOfStreamException();
        var utf16 = Encoding.Unicode.GetString(data, position, chars * 2);
        position += chars * 2;
        return utf16.TrimEnd('\0');
    }

    private static int ReadI32(byte[] data, ref int position)
    {
        if (position + 4 > data.Length) throw new EndOfStreamException();
        var value = BitConverter.ToInt32(data, position);
        position += 4;
        return value;
    }

    private static long ReadI64(byte[] data, ref int position)
    {
        if (position + 8 > data.Length) throw new EndOfStreamException();
        var value = BitConverter.ToInt64(data, position);
        position += 8;
        return value;
    }

    private static uint ReadU32(byte[] data, int position)
    {
        if (position < 0 || position + 4 > data.Length) throw new EndOfStreamException();
        return BitConverter.ToUInt32(data, position);
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}
