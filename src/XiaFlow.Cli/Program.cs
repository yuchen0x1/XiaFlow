using System.Text.Json;
using XiaFlow.Core;
using XiaFlow.Core.Engines;
using XiaFlow.Core.Security;

if (args is ["--version"])
{
    Console.WriteLine(Branding.VersionText);
    return 0;
}
if (args.Length == 0 || args is ["--help"])
{
    Console.WriteLine("""
        匣流 XiaFlow CLI

        xiaflow-cli list <archive> [--json]
        xiaflow-cli test <archive>
        xiaflow-cli create <new.zip|new.7z> <file-or-directory> [...]
        xiaflow-cli extract <archive.zip|archive.7z> <new-directory>
        xiaflow-cli sha256 <file>
        xiaflow-cli --version

        Paths may be relative to your current directory. Create and extract never overwrite
        existing destinations. Install 7-Zip and set XIAFLOW_ENGINE_DIR to its directory.
        """);
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var ct = cancellation.Token;

try
{
    switch (args)
    {
        case ["sha256", var path]:
            Console.WriteLine(ArchiveService.Sha256(InputPath(path), ct: ct));
            break;
        case ["list", _] or ["list", _, "--json"]:
            using (var engine = new SevenZipEngine())
            {
                var listing = engine.List(InputPath(args[1]));
                if (args.Length == 3)
                    Console.WriteLine(JsonSerializer.Serialize(listing.Entries.Select(e => new
                    {
                        path = e.Name,
                        size = e.Size,
                        directory = e.IsDirectory,
                        encrypted = e.Encrypted,
                        link = e.IsSymlink
                    })));
                else
                    foreach (var entry in listing.Entries)
                        Console.WriteLine($"{entry.Size,12}  {JsonSerializer.Serialize(entry.Name)}");
            }
            break;
        case ["test", var path]:
            using (var engine = new SevenZipEngine())
                engine.Test(InputPath(path), ct: ct, failOnWarning: true);
            Console.WriteLine("Archive verified.");
            break;
        case ["create", var path, .. var inputs] when inputs.Length > 0:
            Create(path, inputs, ct);
            break;
        case ["extract", var path, var dir]:
            Extract(path, dir, ct);
            break;
        default:
            Console.Error.WriteLine("Invalid arguments. Run xiaflow-cli --help.");
            return 2;
    }
    return 0;
}
catch (Exception) when (ct.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArchiveException or ArgumentException or
    NotSupportedException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static string InputPath(string value)
{
    if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        throw new ArgumentException("Path is empty or contains control characters.");
    return Path.GetFullPath(value);
}

static ArchiveFormat WriteFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".zip" => ArchiveFormat.Zip,
    ".7z" => ArchiveFormat.SevenZ,
    _ => throw new ArgumentException("This command accepts ZIP and 7z archives.")
};

static string NewPath(string value)
{
    var path = Path.TrimEndingDirectorySeparator(InputPath(value));
    if (Path.Exists(path)) throw new IOException("Destination already exists.");
    var parent = Path.GetDirectoryName(path);
    if (parent == null || !Directory.Exists(parent))
        throw new IOException("Create the parent directory first.");
    return path;
}

static void Create(string archive, string[] inputs, CancellationToken ct)
{
    var output = NewPath(archive);
    var format = WriteFormat(output);
    var sources = inputs.Select(InputPath).ToList();
    if (sources.Any(s => !Path.Exists(s)))
        throw new FileNotFoundException("At least one input path does not exist.");
    if (sources.Any(s => Directory.Exists(s) && PathGuard.IsInsideDirectory(output, s)))
        throw new ArgumentException("The archive destination must be outside the input directories.");

    var stage = Directory.CreateTempSubdirectory("xiaflow-create-");
    try
    {
        using var engine = new SevenZipEngine();
        var path = Path.Combine(stage.FullName, "archive" + Path.GetExtension(output));
        engine.Create(new SevenZipEngine.CreateRequest
        {
            ArchivePath = path, Format = format, InputPaths = sources, FailOnWarning = true
        }, ct);
        engine.Test(path, ct: ct, failOnWarning: true);
        ct.ThrowIfCancellationRequested();
        File.Move(path, output);
    }
    finally
    {
        stage.Delete(recursive: true);
    }
}

static void Extract(string archive, string destination, CancellationToken ct)
{
    var path = InputPath(archive);
    WriteFormat(path);
    var output = NewPath(destination);
    using var engine = new SevenZipEngine();
    var listing = engine.List(path);
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in listing.Entries)
    {
        var name = entry.Name.Replace('\\', '/').TrimEnd('/');
        if (entry.IsSymlink || entry.Encrypted || name.Length == 0 || name.Any(char.IsControl) ||
            PathGuard.SanitizeEntryName(name) != name || !names.Add(name))
            throw new InvalidDataException("Archive contains an encrypted, linked, duplicate or unsupported entry.");
    }

    var stage = Path.Combine(Path.GetDirectoryName(output)!, ".xiaflow-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(stage);
    try
    {
        var safety = PathGuard.EvaluateExtraction(
            listing.Entries.Select(e => (e.Name, e.Size, e.IsDirectory, e.IsSymlink)), stage, new ExtractSafetyLimits());
        if (!safety.IsSafe) throw new InvalidDataException(string.Join(" ", safety.Violations.Take(3)));
        engine.Extract(new SevenZipEngine.ExtractRequest
        {
            ArchivePath = path, OutputDir = stage, FailOnWarning = true
        }, ct);
        if (Directory.EnumerateFileSystemEntries(stage, "*", new EnumerationOptions
        {
            AttributesToSkip = 0, RecurseSubdirectories = true, MaxRecursionDepth = 256
        }).Any(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidDataException("The extraction contains links.");
        ct.ThrowIfCancellationRequested();
        Directory.Move(stage, output);
    }
    finally
    {
        if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
    }
}
