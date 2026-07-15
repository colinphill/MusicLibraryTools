using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicLibrary.Core.Models;

namespace MusicLibrary.Core.Services;

/// <summary>ADB-backed endpoint implemented directly over the local ADB server protocol.</summary>
public sealed class AdbFileTreeEndpoint : IFileTreeEndpoint
{
    private readonly AdbProtocolClient _client;
    public FileTreeEndpointDescriptor Descriptor { get; }

    public AdbFileTreeEndpoint(FileTreeEndpointDescriptor descriptor)
        : this(descriptor, new AdbProtocolClient()) { }

    internal AdbFileTreeEndpoint(FileTreeEndpointDescriptor descriptor, AdbProtocolClient client)
    {
        if (descriptor.Kind != FileTreeEndpointKind.Adb)
            throw new ArgumentException("An ADB endpoint requires an ADB descriptor.", nameof(descriptor));
        Descriptor = descriptor with { Root = FileTreeEndpointFactory.NormalizeAdbPath(descriptor.Root) };
        _client = client;
    }

    public async Task<FileTreeSnapshot> CaptureAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken ct = default)
    {
        AdbShellResult result = await _client.ShellAsync(
            $"TZ=UTC ls -l -A -R {Quote(Descriptor.Root)}", Descriptor.DeviceSerial, ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Unable to enumerate Android path '{Descriptor.Root}': {result.Stderr}");

        var entries = new Dictionary<string, FileTreeEntry>(StringComparer.Ordinal);
        string currentDirectory = Descriptor.Root.TrimEnd('/');
        foreach (string rawLine in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            string line = rawLine.TrimEnd();
            if (line[0] is not ('-' or 'd') && line.EndsWith(':') &&
                !line.StartsWith("total ", StringComparison.Ordinal))
            {
                currentDirectory = line[..^1];
                string directoryRelative = Relative(currentDirectory);
                if (directoryRelative.Length > 0 && !entries.ContainsKey(directoryRelative))
                    entries[directoryRelative] = new(directoryRelative, true, 0, DateTime.MinValue);
                continue;
            }
            if (line.Length == 0 || line.StartsWith("total ", StringComparison.Ordinal) ||
                (line[0] != '-' && line[0] != 'd'))
                continue;
            string[] columns = line.Split(' ', 8, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 8 || !long.TryParse(columns[4], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long length) ||
                !DateTime.TryParseExact(columns[5] + " " + columns[6], "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal, out DateTime modified))
                throw new InvalidDataException($"Unsupported Android directory entry: {line}");
            string path = currentDirectory.TrimEnd('/') + "/" + columns[7];
            string relative = Relative(path);
            var entry = new FileTreeEntry(relative, line[0] == 'd', line[0] == 'd' ? 0 : length,
                modified);
            if (!entries.TryAdd(relative, entry) && entries[relative].LastWriteTimeUtc == DateTime.MinValue)
                entries[relative] = entry;
        }
        progress?.Report(new(OperationPhase.IndexingSources, entries.Count,
            CurrentPath: Descriptor.DisplayName, Message: "Inventoried Android endpoint"));
        return new(Descriptor, entries, DateTimeOffset.UtcNow);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $"androidsync-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await _client.PullAsync(path, output, Descriptor.DeviceSerial, ct).ConfigureAwait(false);
            return new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default) =>
        await EnsureSuccessAsync($"mkdir -p {Quote(path)}", ct).ConfigureAwait(false);

    public async Task WriteFileAsync(string path, Stream source, DateTime modifiedUtc,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        string temporary = path + $".androidsync-{Guid.NewGuid():N}";
        try
        {
            await CreateDirectoryAsync(DirectoryOf(path), ct).ConfigureAwait(false);
            await _client.PushAsync(source, temporary, modifiedUtc, Descriptor.DeviceSerial,
                progress, ct).ConfigureAwait(false);
            await EnsureSuccessAsync($"mv {Quote(temporary)} {Quote(path)}", ct).ConfigureAwait(false);
        }
        catch
        {
            try { await EnsureSuccessAsync($"rm -f {Quote(temporary)}", CancellationToken.None).ConfigureAwait(false); }
            catch { }
            throw;
        }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken ct = default)
    {
        await CreateDirectoryAsync(DirectoryOf(destinationPath), ct).ConfigureAwait(false);
        await EnsureSuccessAsync($"mv {Quote(sourcePath)} {Quote(destinationPath)}", ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        await EnsureSuccessAsync($"rm -f {Quote(path)}", ct).ConfigureAwait(false);

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct = default) =>
        await EnsureSuccessAsync($"rmdir {Quote(path)}", ct).ConfigureAwait(false);

    public async Task AppendJournalLinesAsync(
        string journalPath, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        if (lines.Count == 0) return;
        await CreateDirectoryAsync(DirectoryOf(journalPath), ct).ConfigureAwait(false);
        // One shell round trip per bounded chunk, rather than one per PLAN line. This is material
        // for large libraries over high-latency USB/Wi-Fi ADB links.
        var arguments = new List<string>();
        int length = 0;
        foreach (string line in lines)
        {
            string quoted = Quote(line);
            if (arguments.Count > 0 && length + quoted.Length > 16 * 1024)
            {
                await AppendChunkAsync(journalPath, arguments, ct).ConfigureAwait(false);
                arguments.Clear();
                length = 0;
            }
            arguments.Add(quoted);
            length += quoted.Length + 1;
        }
        if (arguments.Count > 0)
            await AppendChunkAsync(journalPath, arguments, ct).ConfigureAwait(false);
    }

    private Task AppendChunkAsync(string journalPath, IReadOnlyList<string> quotedLines,
        CancellationToken ct) => EnsureSuccessAsync(
            $"printf '%s\\n' {string.Join(' ', quotedLines)} >> {Quote(journalPath)} && sync", ct);

    private async Task EnsureSuccessAsync(string command, CancellationToken ct)
    {
        AdbShellResult result = await _client.ShellAsync(command, Descriptor.DeviceSerial, ct)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException($"Android command failed ({result.ExitCode}): {result.Stderr}");
    }

    private string Relative(string path)
    {
        string root = Descriptor.Root.TrimEnd('/');
        if (StringComparer.Ordinal.Equals(path, root)) return "";
        if (!path.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidDataException($"Android enumeration escaped its root: {path}");
        return path[(root.Length + 1)..];
    }

    private static string DirectoryOf(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

internal sealed record AdbShellResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Minimal cancellable client for the host transport, shell-v2, and sync ADB protocols.</summary>
internal sealed class AdbProtocolClient
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly IPEndPoint _endpoint;
    private const int BufferSize = 64 * 1024;

    public AdbProtocolClient() : this(new(IPAddress.Loopback, 5037)) { }
    internal AdbProtocolClient(IPEndPoint endpoint) => _endpoint = endpoint;

    public async Task<AdbShellResult> ShellAsync(
        string command, string? serial, CancellationToken ct)
    {
        using Socket socket = await ConnectTransportAsync(serial, ct).ConfigureAwait(false);
        await SendRequestAsync(socket, "shell,v2,raw:" + command, ct).ConfigureAwait(false);
        await ReadHostStatusAsync(socket, ct).ConfigureAwait(false);
        using var stdout = new MemoryStream();
        using var stderr = new MemoryStream();
        int exitCode = 0;
        byte[] header = new byte[5];
        while (true)
        {
            int first = await socket.ReceiveAsync(header.AsMemory(0, 1), SocketFlags.None, ct)
                .ConfigureAwait(false);
            if (first == 0) break;
            await ReceiveExactlyAsync(socket, header.AsMemory(1, 4), ct).ConfigureAwait(false);
            byte type = header[0];
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
            if (length < 0 || length > 16 * 1024 * 1024)
                throw new IOException($"Invalid ADB shell packet length: {length}.");
            byte[] payload = new byte[length];
            await ReceiveExactlyAsync(socket, payload, ct).ConfigureAwait(false);
            switch (type)
            {
                case 1: stdout.Write(payload); break;
                case 2: stderr.Write(payload); break;
                case 3 when payload.Length >= 4:
                    exitCode = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    break;
                case 3 when payload.Length > 0: exitCode = payload[0]; break;
            }
        }
        return new(exitCode, Utf8.GetString(stdout.ToArray()), Utf8.GetString(stderr.ToArray()));
    }

    public async Task PushAsync(Stream source, string destination, DateTime modifiedUtc,
        string? serial, IProgress<long>? progress, CancellationToken ct)
    {
        using Socket socket = await ConnectSyncAsync(serial, ct).ConfigureAwait(false);
        await SendSyncPacketAsync(socket, "SEND", Utf8.GetBytes(destination + ",505"), ct)
            .ConfigureAwait(false);
        byte[] buffer = new byte[BufferSize - 64];
        long transferred = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            await SendSyncPacketAsync(socket, "DATA", buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            progress?.Report(transferred += read);
        }
        byte[] done = new byte[8];
        "DONE"u8.CopyTo(done);
        long seconds = Math.Max(0, (long)(modifiedUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds);
        BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4), checked((uint)Math.Min(seconds, uint.MaxValue)));
        await SendAllAsync(socket, done, ct).ConfigureAwait(false);
        await ReadSyncStatusAsync(socket, ct).ConfigureAwait(false);
    }

    public async Task PullAsync(string source, Stream destination, string? serial, CancellationToken ct)
    {
        using Socket socket = await ConnectSyncAsync(serial, ct).ConfigureAwait(false);
        await SendSyncPacketAsync(socket, "RECV", Utf8.GetBytes(source), ct).ConfigureAwait(false);
        byte[] header = new byte[8];
        while (true)
        {
            await ReceiveExactlyAsync(socket, header, ct).ConfigureAwait(false);
            string tag = Utf8.GetString(header, 0, 4);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (tag == "DONE") break;
            if (length < 0 || length > 16 * 1024 * 1024)
                throw new IOException($"Invalid ADB sync packet length: {length}.");
            byte[] payload = new byte[length];
            await ReceiveExactlyAsync(socket, payload, ct).ConfigureAwait(false);
            if (tag == "FAIL") throw new IOException("ADB pull failed: " + Utf8.GetString(payload));
            if (tag != "DATA") throw new IOException("Unexpected ADB sync response: " + tag);
            await destination.WriteAsync(payload, ct).ConfigureAwait(false);
        }
    }

    private async Task<Socket> ConnectTransportAsync(string? serial, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_endpoint, ct).ConfigureAwait(false);
            await SendRequestAsync(socket, serial is null ? "host:transport-any" :
                "host:transport:" + serial, ct).ConfigureAwait(false);
            await ReadHostStatusAsync(socket, ct).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private async Task<Socket> ConnectSyncAsync(string? serial, CancellationToken ct)
    {
        Socket socket = await ConnectTransportAsync(serial, ct).ConfigureAwait(false);
        try
        {
            await SendRequestAsync(socket, "sync:", ct).ConfigureAwait(false);
            await ReadHostStatusAsync(socket, ct).ConfigureAwait(false);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static async Task SendRequestAsync(Socket socket, string request, CancellationToken ct)
    {
        byte[] payload = Utf8.GetBytes(request);
        byte[] prefix = Utf8.GetBytes(payload.Length.ToString("X4", CultureInfo.InvariantCulture));
        await SendAllAsync(socket, prefix, ct).ConfigureAwait(false);
        await SendAllAsync(socket, payload, ct).ConfigureAwait(false);
    }

    private static async Task ReadHostStatusAsync(Socket socket, CancellationToken ct)
    {
        byte[] status = new byte[4];
        await ReceiveExactlyAsync(socket, status, ct).ConfigureAwait(false);
        string value = Utf8.GetString(status);
        if (value == "OKAY") return;
        if (value != "FAIL") throw new IOException("Unexpected ADB host response: " + value);
        byte[] lengthBytes = new byte[4];
        await ReceiveExactlyAsync(socket, lengthBytes, ct).ConfigureAwait(false);
        int length = int.Parse(Utf8.GetString(lengthBytes), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        byte[] message = new byte[length];
        await ReceiveExactlyAsync(socket, message, ct).ConfigureAwait(false);
        throw new IOException("ADB request failed: " + Utf8.GetString(message));
    }

    private static async Task ReadSyncStatusAsync(Socket socket, CancellationToken ct)
    {
        byte[] status = new byte[4];
        await ReceiveExactlyAsync(socket, status, ct).ConfigureAwait(false);
        string value = Utf8.GetString(status);
        if (value == "OKAY") return;
        if (value != "FAIL") throw new IOException("Unexpected ADB sync response: " + value);
        byte[] lengthBytes = new byte[4];
        await ReceiveExactlyAsync(socket, lengthBytes, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        byte[] message = new byte[length];
        await ReceiveExactlyAsync(socket, message, ct).ConfigureAwait(false);
        throw new IOException("ADB sync failed: " + Utf8.GetString(message));
    }

    private static async Task SendSyncPacketAsync(Socket socket, string tag,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] header = new byte[8];
        Utf8.GetBytes(tag, header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);
        await SendAllAsync(socket, header, ct).ConfigureAwait(false);
        await SendAllAsync(socket, payload, ct).ConfigureAwait(false);
    }

    private static async Task SendAllAsync(Socket socket, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        int sent = 0;
        while (sent < data.Length)
        {
            int count = await socket.SendAsync(data[sent..], SocketFlags.None, ct).ConfigureAwait(false);
            if (count == 0) throw new IOException("ADB closed the connection while sending.");
            sent += count;
        }
    }

    private static async Task ReceiveExactlyAsync(Socket socket, Memory<byte> data, CancellationToken ct)
    {
        int received = 0;
        while (received < data.Length)
        {
            int count = await socket.ReceiveAsync(data[received..], SocketFlags.None, ct)
                .ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("ADB closed the connection unexpectedly.");
            received += count;
        }
    }
}
