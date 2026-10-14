using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Content.Android.Launcher.Utility;

// Backups contain server content only, never account credentials or launcher settings.
public static class ServerCacheTransfer
{
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private sealed record CacheFile(string Name, long Size, string Sha256);
    private sealed record Manifest(int Format, CacheFile[] Files);
    private static bool ValidName(string name) => Regex.IsMatch(name, @"\A[0-9a-fA-F]{32,128}\.zip\z");

    public static int Export(string cacheDir, Stream destination)
    {
        var files = Directory.GetFiles(cacheDir, "*.zip").Where(f => ValidName(Path.GetFileName(f))).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("Нет скачанных сборок для экспорта.");
        if (files.Length > 128 || files.Sum(f => new FileInfo(f).Length) > MaxBytes)
            throw new InvalidDataException("Архив слишком большой: предел 128 сборок и 8 ГБ.");
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        var manifest = new List<CacheFile>();
        foreach (var file in files)
        {
            using var input = File.OpenRead(file);
            var hash = Convert.ToHexString(SHA256.HashData(input));
            input.Position = 0;
            using var output = zip.CreateEntry("builds/" + Path.GetFileName(file), CompressionLevel.NoCompression).Open();
            input.CopyTo(output);
            manifest.Add(new CacheFile(Path.GetFileName(file), input.Length, hash));
        }
        using var meta = zip.CreateEntry("ss14-cache.json").Open();
        JsonSerializer.Serialize(meta, new Manifest(1, manifest.ToArray()));
        return files.Length;
    }

    public static int Import(string cacheDir, string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var meta = zip.GetEntry("ss14-cache.json") ?? throw new InvalidDataException("Это не архив экспорта сборок SS14.");
        if (meta.Length > 128 * 1024) throw new InvalidDataException("Повреждён список сборок.");
        using var metadata = meta.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(metadata);
        if (manifest is not { Format: 1 } || manifest.Files is not { Length: > 0 and <= 128 })
            throw new InvalidDataException("Неподдерживаемый формат архива.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file == null || string.IsNullOrEmpty(file.Name) || !ValidName(file.Name) || !names.Add(file.Name) ||
                file.Size <= 0 || file.Size > MaxBytes || string.IsNullOrEmpty(file.Sha256) ||
                !Regex.IsMatch(file.Sha256, @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidDataException("Недопустимый файл в архиве.");
            total += file.Size;
            if (total > MaxBytes) throw new InvalidDataException("Архив превышает 8 ГБ.");
            if (zip.Entries.Count(e => e.FullName == "builds/" + file.Name) != 1 ||
                zip.GetEntry("builds/" + file.Name)!.Length != file.Size)
                throw new InvalidDataException("Архив неполный или повреждён.");
        }
        var stage = Path.Combine(cacheDir, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            // Validate every file before changing the live cache. Never extract arbitrary paths.
            foreach (var file in manifest.Files)
            {
                var target = Path.Combine(stage, file.Name.ToUpperInvariant().Replace(".ZIP", ".zip"));
                using (var input = zip.GetEntry("builds/" + file.Name)!.Open())
                using (var output = File.Create(target)) CopyLimited(input, output, file.Size);
                using (var input = File.OpenRead(target))
                {
                    if (input.Length != file.Size || !Convert.ToHexString(SHA256.HashData(input)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Контрольная сумма сборки не совпала.");
                }
                using var content = ZipFile.OpenRead(target);
                if (content.Entries.Count == 0) throw new InvalidDataException("Пустая сборка сервера.");
            }
            foreach (var file in Directory.GetFiles(stage))
                File.Move(file, Path.Combine(cacheDir, Path.GetFileName(file)), overwrite: true);
            return manifest.Files.Length;
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    public static void CopyLimited(Stream input, Stream output, long limit = MaxBytes)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Размер архива превышает допустимый предел.");
            output.Write(buffer, 0, count);
        }
    }
}
