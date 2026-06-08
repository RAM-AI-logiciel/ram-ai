using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RamAI.Phase3.Memory;

/// <summary>
/// File-backed NVMe cache for cold-process snapshots.
///
/// Layout of cache\ram-ai.cache
/// ──────────────────────────────
///  [ Header : 4 KB  — magic string + reserved ]
///  [ Frame* : [int32 compressedLen][GZip-compressed JSON] ]
///
/// Each frame stores the last-known metrics of a "cold" process so that
/// the service can restore its profile when the ML model predicts it will
/// become active again.
///
/// A single FileStream with FileShare.ReadWrite handles all I/O.
/// No Win32 P/Invoke — no double-handle conflict.
/// </summary>
internal sealed class PageCacheManager : IDisposable
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int    HeaderSize  = 4096;
    private const string MagicHeader = "RAM-AI-CACHE-V1";

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly string                    _cachePath;
    private readonly ILogger<PageCacheManager> _log;

    private FileStream? _stream;

    // In-memory index: PID → byte offset of latest compressed block
    private readonly Dictionary<int, long> _index = [];
    private readonly object                _lock  = new();

    private long _totalBytesSaved;
    private long _totalEntriesWritten;

    internal long TotalBytesSaved     => _totalBytesSaved;
    internal long TotalEntriesWritten => _totalEntriesWritten;

    // ── Constructor ───────────────────────────────────────────────────────────

    internal PageCacheManager(string cachePath, ILogger<PageCacheManager> log)
    {
        _cachePath = cachePath;
        _log       = log;

        string? dir = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    internal void Open()
    {
        bool isNew = !File.Exists(_cachePath);

        // Single handle — FileShare.ReadWrite allows external readers (e.g. tools)
        // while we hold the stream open for the lifetime of the service.
        _stream = new FileStream(
            _cachePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite,
            bufferSize: 65536,
            options: FileOptions.WriteThrough);

        if (isNew) WriteHeader();
        else       RebuildIndex();

        _log.LogInformation("PageCacheManager opened {P} (entries={N})",
                            _cachePath, _index.Count);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>GZip-compress and append a cold-process snapshot.</summary>
    internal void StoreColdSnapshot(ColdProcessEntry entry)
    {
        byte[] compressed = Compress(JsonSerializer.SerializeToUtf8Bytes(entry));

        lock (_lock)
        {
            if (_stream is null) return;

            _stream.Seek(0, SeekOrigin.End);
            long offset = _stream.Position;

            // Frame: [int32 length][compressed bytes]
            _stream.Write(BitConverter.GetBytes(compressed.Length));
            _stream.Write(compressed);
            _stream.Flush();

            _index[entry.Pid] = offset;

            long saved = entry.WorkingSetBytes - compressed.Length;
            Interlocked.Add(ref _totalBytesSaved, saved > 0 ? saved : 0);
            Interlocked.Increment(ref _totalEntriesWritten);
        }
    }

    /// <summary>Retrieve the last cached snapshot for a PID, or null.</summary>
    internal ColdProcessEntry? LoadSnapshot(int pid)
    {
        lock (_lock)
        {
            if (_stream is null || !_index.TryGetValue(pid, out long offset))
                return null;

            try
            {
                _stream.Seek(offset, SeekOrigin.Begin);

                Span<byte> lenBuf = stackalloc byte[4];
                _stream.ReadExactly(lenBuf);

                int len = BitConverter.ToInt32(lenBuf);
                byte[] compressed = new byte[len];
                _stream.ReadExactly(compressed);

                return JsonSerializer.Deserialize<ColdProcessEntry>(Decompress(compressed));
            }
            catch (Exception ex)
            {
                _log.LogWarning("LoadSnapshot PID {P}: {E}", pid, ex.Message);
                return null;
            }
        }
    }

    // ── Compression ───────────────────────────────────────────────────────────

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data);
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var ms   = new MemoryStream(data);
        using var gz   = new GZipStream(ms, CompressionMode.Decompress);
        using var out_ = new MemoryStream();
        gz.CopyTo(out_);
        return out_.ToArray();
    }

    // ── Header / index ────────────────────────────────────────────────────────

    private void WriteHeader()
    {
        if (_stream is null) return;
        _stream.Seek(0, SeekOrigin.Begin);
        byte[] hdr = new byte[HeaderSize];
        System.Text.Encoding.UTF8.GetBytes(MagicHeader).CopyTo(hdr, 0);
        _stream.Write(hdr);
        _stream.Flush();
    }

    private void RebuildIndex()
    {
        if (_stream is null) return;
        try
        {
            _stream.Seek(HeaderSize, SeekOrigin.Begin);
            while (_stream.Position < _stream.Length - 4)
            {
                long   offset = _stream.Position;
                byte[] lb     = new byte[4];
                if (_stream.Read(lb) < 4) break;

                int len = BitConverter.ToInt32(lb);
                if (len <= 0 || _stream.Position + len > _stream.Length) break;

                byte[] compressed = new byte[len];
                _stream.ReadExactly(compressed);

                try
                {
                    var e = JsonSerializer.Deserialize<ColdProcessEntry>(Decompress(compressed));
                    if (e is not null) _index[e.Pid] = offset;
                }
                catch { /* corrupt block — skip */ }
            }
        }
        catch (Exception ex) { _log.LogWarning("Index rebuild: {E}", ex.Message); }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _stream?.Dispose();
}

// ── DTO ───────────────────────────────────────────────────────────────────────

internal sealed class ColdProcessEntry
{
    public int      Pid             { get; init; }
    public string   Name            { get; init; } = string.Empty;
    public DateTime CachedAt        { get; init; }
    public long     WorkingSetBytes { get; init; }
    public long     PrivateBytes    { get; init; }
    public float    LastProbability { get; init; }
}
