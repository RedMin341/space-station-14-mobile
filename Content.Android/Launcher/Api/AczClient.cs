using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Android.Launcher.Models;
using Content.Android.Launcher.Utility;
using Robust.Shared.Utility;
using SharpZstd.Interop;
using SpaceWizards.Sodium;

namespace Content.Android.Launcher.Api;

public sealed class AczClient
{
    private static bool _nativeInitialized;
    private static readonly object _nativeLock = new();

    private readonly HttpClient _http;

    public AczClient()
    {
        InitNative();
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public static void InitNative()
    {
        lock (_nativeLock)
        {
            if (_nativeInitialized) return;
            _nativeInitialized = true;

            try { Java.Lang.JavaSystem.LoadLibrary("zstd"); } catch { }
            try { Java.Lang.JavaSystem.LoadLibrary("sodium"); } catch { }

            try
            {
                NativeLibrary.SetDllImportResolver(typeof(Zstd).Assembly, (name, asm, path) =>
                {
                    if (name is "zstd" or "libzstd")
                    {
                        if (NativeLibrary.TryLoad("libzstd.so", asm, path, out var handle)) return handle;
                        if (NativeLibrary.TryLoad("zstd", asm, path, out handle)) return handle;
                    }
                    return IntPtr.Zero;
                });
            }
            catch { }
        }
    }

    public static (string manifestUrl, string downloadUrl) GetUrls(ServerBuildInfo build, string serverAddress)
    {
        var manifestUrl = build.ManifestUrl;
        var downloadUrl = build.ManifestDownloadUrl;

        if (string.IsNullOrEmpty(manifestUrl) || string.IsNullOrEmpty(downloadUrl))
        {
            if (UriHelper.TryParseSs14Uri(serverAddress, out var uri))
            {
                var api = UriHelper.GetServerApiAddress(uri);
                if (string.IsNullOrEmpty(manifestUrl))
                    manifestUrl = new Uri(api, "manifest.txt").ToString();
                if (string.IsNullOrEmpty(downloadUrl))
                    downloadUrl = new Uri(api, "download").ToString();
            }
        }

        return (manifestUrl ?? "", downloadUrl ?? "");
    }

    private const int MaxBlobSize = 512 * 1024 * 1024;
    private readonly record struct ManifestEntry(int Index, string Path, string Hash);

    // Manifest entries are hashed with BLAKE2b-256, not ZIP SHA-256.
    private static string HashCachedEntry(ZipArchiveEntry entry, CancellationToken cancel)
    {
        var state = new CryptoGenericHashBlake2B.State();
        if (!CryptoGenericHashBlake2B.Init(ref state, ReadOnlySpan<byte>.Empty, 32))
            throw new InvalidDataException("Не удалось начать проверку кэша");
        using var input = entry.Open();
        var buffer = new byte[65536];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            if (!CryptoGenericHashBlake2B.Update(ref state, buffer.AsSpan(0, read)))
                throw new InvalidDataException("Не удалось проверить хеш кэша");
        }
        Span<byte> hash = stackalloc byte[32];
        if (!CryptoGenericHashBlake2B.Final(ref state, hash))
            throw new InvalidDataException("Не удалось завершить проверку кэша");
        return Convert.ToHexString(hash);
    }

    private static bool SafeManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains(':'))
            return false;
        return !path.Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == "..");
    }

    public async Task<string> DownloadManifestBuildAsync(
        ServerBuildInfo build,
        string serverAddress,
        string cacheDir,
        Action<int, int, string>? progress,
        CancellationToken cancel = default)
    {
        Directory.CreateDirectory(cacheDir);
        var expectedHash = build.ManifestHash?.ToUpperInvariant();
        if (!string.IsNullOrEmpty(expectedHash))
        {
            var existing = Path.Combine(cacheDir, $"{expectedHash}.zip");
            if (File.Exists(existing) && new FileInfo(existing).Length > 0)
                return existing;
        }

        var (manifestUrl, downloadUrl) = GetUrls(build, serverAddress);
        progress?.Invoke(0, 0, "Получаем список файлов сервера…");
        using var manifestReq = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        manifestReq.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
        using var manifestResp = await _http.SendAsync(manifestReq, HttpCompletionOption.ResponseHeadersRead, cancel);
        manifestResp.EnsureSuccessStatusCode();

        using var manifestStream = await manifestResp.Content.ReadAsStreamAsync(cancel);
        using var reader = new StreamReader(manifestStream, Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync(cancel);
        if (headerLine != "Robust Content Manifest 1")
            throw new InvalidDataException($"Неизвестный формат манифеста: {headerLine}");

        var entries = new List<ManifestEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = await reader.ReadLineAsync(cancel)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var split = line.IndexOf(' ');
            if (split != 64) throw new InvalidDataException("Повреждён хеш в манифесте");
            var hash = line[..split];
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("Недопустимый хеш файла");
            var path = line[(split + 1)..];
            if (!SafeManifestPath(path) || !paths.Add(path))
                throw new InvalidDataException($"Недопустимый путь файла: {path}");
            entries.Add(new ManifestEntry(entries.Count, path, hash.ToUpperInvariant()));
        }
        if (entries.Count == 0)
            throw new InvalidDataException("Манифест сервера пуст");

        var finalHash = expectedHash ?? (build.Version?.ToUpperInvariant() ?? Guid.NewGuid().ToString("N"));
        var targetZip = Path.Combine(cacheDir, $"{finalHash}.zip");
        if (File.Exists(targetZip) && new FileInfo(targetZip).Length > 0)
            return targetZip;

        var cachedArchives = new List<ZipArchive>();
        var reusable = new Dictionary<int, ZipArchiveEntry>();
        var tempZip = Path.Combine(cacheDir, $"{finalHash}.tmp");

        try
        {
            // Support old ZIPs already stored by earlier Android versions.
            var previousZips = Directory.EnumerateFiles(cacheDir, "*.zip")
                .Where(f => !string.Equals(f, targetZip, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                .Where(f => f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(2)
                .ToArray();
            foreach (var file in previousZips)
            {
                try { cachedArchives.Add(ZipFile.OpenRead(file.FullName)); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    global::Android.Util.Log.Warn("SS14_ACZ", $"Bad cache {file.Name}: {ex.Message}");
                }
            }

            if (cachedArchives.Count > 0)
            {
                progress?.Invoke(0, entries.Count, "Сравниваем старый кэш с новой сборкой…");
                foreach (var entry in entries)
                {
                    cancel.ThrowIfCancellationRequested();
                    foreach (var oldZip in cachedArchives)
                    {
                        var old = oldZip.GetEntry(entry.Path);
                        if (old == null || old.Length > MaxBlobSize)
                            continue;

                        try
                        {
                            if (HashCachedEntry(old, cancel) == entry.Hash)
                            {
                                reusable[entry.Index] = old;
                                break;
                            }
                        }
                        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
                        {
                            global::Android.Util.Log.Warn("SS14_ACZ",
                                $"Unusable cached blob {entry.Path}: {ex.Message}");
                        }
                    }
                    if (entry.Index % 300 == 0)
                        progress?.Invoke(reusable.Count, entries.Count,
                            $"Кэш: проверено {entry.Index + 1}/{entries.Count}, совпало {reusable.Count}");
                }
            }

            var needed = entries.Where(e => !reusable.ContainsKey(e.Index)).ToArray();
            global::Android.Util.Log.Info("SS14_ACZ",
                $"Delta total={entries.Count}, cache={reusable.Count}, new={needed.Length}");
            progress?.Invoke(reusable.Count, entries.Count,
                $"Из кэша: {reusable.Count}; докачка: {needed.Length} файлов");

            if (File.Exists(tempZip)) File.Delete(tempZip);
            using (var file = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1048576, false))
            using (var outputZip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                // Build a complete ZIP locally, but copy unchanged files from
                // previous versions instead of fetching them over the internet.
                foreach (var entry in entries)
                {
                    if (!reusable.TryGetValue(entry.Index, out var old))
                        continue;
                    cancel.ThrowIfCancellationRequested();
                    using var source = old.Open();
                    using var dest = outputZip.CreateEntry(entry.Path, CompressionLevel.Fastest).Open();
                    await source.CopyToAsync(dest, 65536, cancel);
                }

                if (needed.Length > 0)
                {
                    // Protocol v1 expects the manifest indices of missing blobs.
                    var requestBody = new byte[needed.Length * 4];
                    for (var i = 0; i < needed.Length; i++)
                        BinaryPrimitives.WriteInt32LittleEndian(requestBody.AsSpan(i * 4, 4), needed[i].Index);

                    using var req = new HttpRequestMessage(HttpMethod.Post, downloadUrl);
                    req.Headers.Add("X-Robust-Download-Protocol", "1");
                    req.Headers.UserAgent.ParseAdd("SS14.Launcher/61");
                    req.Headers.TryAddWithoutValidation("Accept-Encoding", "zstd");
                    req.Content = new ByteArrayContent(requestBody);
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                    using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
                    resp.EnsureSuccessStatusCode();
                    using var rawStream = await resp.Content.ReadAsStreamAsync(cancel);
                    using Stream decoded = resp.Content.Headers.ContentEncoding.Any(e =>
                        e.Equals("zstd", StringComparison.OrdinalIgnoreCase))
                        ? new ZStdDecompressStream(rawStream) : rawStream;
                    using var stream = new BufferedStream(decoded, 1048576);

                    var streamHeader = new byte[4];
                    await stream.ReadExactlyAsync(streamHeader, cancel);
                    var flags = BinaryPrimitives.ReadInt32LittleEndian(streamHeader);
                    if ((flags & ~1) != 0)
                        throw new InvalidDataException("Неизвестный формат загрузки сервера");
                    var precompressed = (flags & 1) != 0;

                    var readBuffer = new byte[65536];
                    var compressedBuffer = new byte[65536];
                    var fileHeader = new byte[precompressed ? 8 : 4];
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    long lastProgress = 0;

                    for (var i = 0; i < needed.Length; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var entry = needed[i];
                        await stream.ReadExactlyAsync(fileHeader, cancel);
                        var size = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(0, 4));
                        if (size < 0 || size > MaxBlobSize)
                            throw new InvalidDataException($"Недопустимый размер файла {entry.Path}");

                        ReadOnlyMemory<byte> data;
                        if (precompressed)
                        {
                            var compressedSize = BinaryPrimitives.ReadInt32LittleEndian(fileHeader.AsSpan(4, 4));
                            if (compressedSize < 0 || compressedSize > MaxBlobSize)
                                throw new InvalidDataException($"Некорректный сжатый размер {entry.Path}");
                            if (compressedSize > 0)
                            {
                                EnsureBuffer(ref compressedBuffer, compressedSize);
                                await stream.ReadExactlyAsync(compressedBuffer.AsMemory(0, compressedSize), cancel);
                                EnsureBuffer(ref readBuffer, size);
                                unsafe
                                {
                                    fixed (byte* dst = readBuffer)
                                    fixed (byte* src = compressedBuffer)
                                    {
                                        var ret = Zstd.ZSTD_decompress(dst, (nuint)size, src, (nuint)compressedSize);
                                        if (Zstd.ZSTD_isError(ret) != 0 || ret != (nuint)size)
                                            throw new InvalidDataException($"Повреждён сжатый файл {entry.Path}");
                                    }
                                }
                                data = readBuffer.AsMemory(0, size);
                            }
                            else
                            {
                                EnsureBuffer(ref readBuffer, size);
                                await stream.ReadExactlyAsync(readBuffer.AsMemory(0, size), cancel);
                                data = readBuffer.AsMemory(0, size);
                            }
                        }
                        else
                        {
                            EnsureBuffer(ref readBuffer, size);
                            await stream.ReadExactlyAsync(readBuffer.AsMemory(0, size), cancel);
                            data = readBuffer.AsMemory(0, size);
                        }

                        var digest = CryptoGenericHashBlake2B.Hash(32, data.Span, ReadOnlySpan<byte>.Empty);
                        if (Convert.ToHexString(digest) != entry.Hash)
                            throw new InvalidDataException($"BLAKE2b не совпадает для {entry.Path}");
                        using (var dest = outputZip.CreateEntry(entry.Path, CompressionLevel.Fastest).Open())
                            dest.Write(data.Span);

                        if (stopwatch.ElapsedMilliseconds - lastProgress >= 150 || i == needed.Length - 1)
                        {
                            lastProgress = stopwatch.ElapsedMilliseconds;
                            var complete = reusable.Count + i + 1;
                            var pct = complete * 100L / entries.Count;
                            progress?.Invoke(complete, entries.Count,
                                $"Докачка: {pct}% (новых {i + 1}/{needed.Length})");
                        }
                    }
                }
            }

            File.Move(tempZip, targetZip, overwrite: true);
            progress?.Invoke(entries.Count, entries.Count,
                $"Готово: {reusable.Count} из кэша, {needed.Length} скачано");
            return targetZip;
        }
        catch
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
        finally
        {
            foreach (var zip in cachedArchives) zip.Dispose();
        }
    }

    private static void EnsureBuffer(ref byte[] buffer, int size)
    {
        if (buffer.Length < size)
        {
            var newSize = Math.Max(buffer.Length * 2, size);
            buffer = new byte[newSize];
        }
    }
}
