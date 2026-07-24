using System.Text.Json;

if (args.Length != 4 ||
    args[0] != "-json" ||
    args[1] != "-length" ||
    args[2] != "0")
{
    Console.Error.WriteLine(
        "Expected: -json -length 0 <audio path>");
    return 2;
}

string path = Path.GetFullPath(args[3]);
if (!File.Exists(path))
{
    Console.Error.WriteLine($"Input does not exist: {path}");
    return 3;
}

string fileName = Path.GetFileName(path);
if (fileName.StartsWith(
        "slow.",
        StringComparison.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromSeconds(30));
}
if (fileName.StartsWith(
        "failure.",
        StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("recorded decoder failure");
    return 7;
}
if (fileName.StartsWith(
        "malformed.",
        StringComparison.OrdinalIgnoreCase))
{
    Console.Write("not-json");
    return 0;
}

string fixture = Path.GetFileNameWithoutExtension(path)
    .Replace(
        "sample_alac",
        "alac",
        StringComparison.OrdinalIgnoreCase)
    .Replace(
        "sample",
        "",
        StringComparison.OrdinalIgnoreCase)
    .Trim('_', '-', ' ');
if (fixture.Length == 0)
    fixture = Path.GetExtension(path).TrimStart('.');
string fingerprint = $"AQAD-golden-{fixture.ToLowerInvariant()}";
Console.Write(JsonSerializer.Serialize(new
{
    duration = 42.25,
    fingerprint,
}));
return 0;
