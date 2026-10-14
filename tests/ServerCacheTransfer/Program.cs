using System.IO.Compression;
using System.Text.Json;
using Content.Android.Launcher.Utility;

var root = Path.Combine(Path.GetTempPath(), "ss14-cache-test-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
    var name = new string('A', 64) + ".zip";
    using (var content = ZipFile.Open(Path.Combine(source, name), ZipArchiveMode.Create))
    using (var writer = new StreamWriter(content.CreateEntry("Prototypes/test.yml").Open())) writer.Write("test-content");
    File.WriteAllText(Path.Combine(source, "account.json"), "must-not-export");
    var archive = Path.Combine(root, "backup.zip");
    using (var output = File.Create(archive))
        Check(ServerCacheTransfer.Export(source, output) == 1, "export count");
    using (var zip = ZipFile.OpenRead(archive)) Check(zip.Entries.Count == 2, "content-only export");
    Check(ServerCacheTransfer.Import(target, archive) == 1, "import count");
    Check(File.ReadAllBytes(Path.Combine(source, name)).SequenceEqual(File.ReadAllBytes(Path.Combine(target, name))), "round trip");
    Check(ServerCacheTransfer.Import(target, archive) == 1, "repeated import");
    var before = File.ReadAllBytes(Path.Combine(target, name));
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("builds/" + name)!;
        var length = entry.Length;
        entry.Delete();
        using var output = zip.CreateEntry("builds/" + name).Open();
        output.Write(new byte[(int)length]);
    }
    Reject(() => ServerCacheTransfer.Import(target, archive));
    Check(before.SequenceEqual(File.ReadAllBytes(Path.Combine(target, name))), "bad hash preserves existing cache");
    Check(!Directory.EnumerateDirectories(target).Any(), "staging cleaned");
    var malicious = Path.Combine(root, "traversal.zip");
    using (var zip = ZipFile.Open(malicious, ZipArchiveMode.Create))
    using (var output = zip.CreateEntry("ss14-cache.json").Open())
        JsonSerializer.Serialize(output, new { Format = 1, Files = new[] { new { Name = "../escape.zip", Size = 1, Sha256 = new string('A', 64) } } });
    Reject(() => ServerCacheTransfer.Import(target, malicious));
    Check(!File.Exists(Path.Combine(root, "escape.zip")), "path traversal rejected");
    Console.WriteLine("PASS: round trip, repeat import, content-only backup, corrupt hash, safe paths and cleanup.");
}
finally { Directory.Delete(root, true); }

static void Check(bool value, string label) { if (!value) throw new Exception(label); }
static void Reject(Action action)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new Exception("Invalid archive was accepted");
}
