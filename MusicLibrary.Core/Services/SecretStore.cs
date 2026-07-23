using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MusicLibrary.Core.Services;

public enum SecretStoreKind
{
    WindowsCredentialManager,
    MacOSKeychain,
    LinuxSecretService,
    SessionOnly,
}

public interface ISecretStore
{
    SecretStoreKind Kind { get; }
    bool IsPersistent { get; }

    Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default);

    Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default);

    Task DeleteAsync(
        string key,
        CancellationToken ct = default);
}

public interface ISecretStoreBackend : ISecretStore
{
}

public sealed class SecretStoreUnavailableException(
    string message,
    Exception? inner = null) : Exception(message, inner);

public sealed class SessionSecretStore : ISecretStoreBackend
{
    private readonly Dictionary<string, string> _secrets =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public SecretStoreKind Kind => SecretStoreKind.SessionOnly;
    public bool IsPersistent => false;

    public Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
            return Task.FromResult(_secrets.GetValueOrDefault(key));
    }

    public Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
            _secrets[key] = secret;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(
        string key,
        CancellationToken ct = default)
    {
        ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
            _secrets.Remove(key);
        return Task.CompletedTask;
    }

    internal static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 200 ||
            key.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '-' or '_')))
            throw new ArgumentException(
                "Secret keys may contain only ASCII letters, digits, '.', '-', and '_'.",
                nameof(key));
    }
}

/// <summary>
/// Uses the native credential facility while it is available, then latches to
/// one process-local fallback if the platform service cannot be reached.
/// </summary>
public sealed class CrossPlatformSecretStore(
    ISecretStoreBackend native,
    SessionSecretStore? fallback = null) : ISecretStore
{
    private readonly SessionSecretStore _fallback =
        fallback ?? new SessionSecretStore();
    private volatile bool _useFallback;

    public SecretStoreKind Kind =>
        _useFallback ? SecretStoreKind.SessionOnly : native.Kind;
    public bool IsPersistent => !_useFallback && native.IsPersistent;

    public async Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        if (_useFallback)
            return await _fallback.ReadAsync(key, ct);
        try
        {
            return await native.ReadAsync(key, ct);
        }
        catch (SecretStoreUnavailableException)
        {
            _useFallback = true;
            return await _fallback.ReadAsync(key, ct);
        }
    }

    public async Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        if (_useFallback)
        {
            await _fallback.WriteAsync(key, secret, ct);
            return;
        }
        try
        {
            await native.WriteAsync(key, secret, ct);
        }
        catch (SecretStoreUnavailableException)
        {
            _useFallback = true;
            await _fallback.WriteAsync(key, secret, ct);
        }
    }

    public async Task DeleteAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        if (_useFallback)
        {
            await _fallback.DeleteAsync(key, ct);
            return;
        }
        try
        {
            await native.DeleteAsync(key, ct);
        }
        catch (SecretStoreUnavailableException)
        {
            _useFallback = true;
            await _fallback.DeleteAsync(key, ct);
        }
    }

    public static CrossPlatformSecretStore CreateDefault()
    {
        ISecretStoreBackend backend = OperatingSystem.IsWindows()
            ? new WindowsCredentialManagerSecretStore()
            : OperatingSystem.IsMacOS()
                ? new MacOSKeychainSecretStore()
                : OperatingSystem.IsLinux()
                    ? new LinuxSecretServiceSecretStore()
                    : new UnavailableSecretStore();
        return new(backend);
    }
}

public sealed class WindowsCredentialManagerSecretStore :
    ISecretStoreBackend
{
    private const int ErrorNotFound = 1168;
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const string TargetPrefix = "MusicLibraryManager/";

    public SecretStoreKind Kind =>
        SecretStoreKind.WindowsCredentialManager;
    public bool IsPersistent => true;

    public Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!CredRead(
                    TargetPrefix + key,
                    CredentialTypeGeneric,
                    0,
                    out IntPtr pointer))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                    return Task.FromResult<string?>(null);
                throw Unavailable(error);
            }
            try
            {
                Credential credential =
                    Marshal.PtrToStructure<Credential>(pointer);
                byte[] bytes = new byte[credential.CredentialBlobSize];
                if (bytes.Length > 0)
                    Marshal.Copy(
                        credential.CredentialBlob,
                        bytes,
                        0,
                        bytes.Length);
                try
                {
                    return Task.FromResult<string?>(
                        Encoding.UTF8.GetString(bytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                CredFree(pointer);
            }
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "Windows Credential Manager is unavailable.",
                error);
        }
    }

    public Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        ct.ThrowIfCancellationRequested();
        byte[] bytes = Encoding.UTF8.GetBytes(secret);
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var credential = new Credential
            {
                Type = CredentialTypeGeneric,
                TargetName = TargetPrefix + key,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
                throw Unavailable(Marshal.GetLastWin32Error());
            return Task.CompletedTask;
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "Windows Credential Manager is unavailable.",
                error);
        }
        finally
        {
            if (handle.IsAllocated)
                handle.Free();
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task DeleteAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        try
        {
            if (!CredDelete(
                    TargetPrefix + key,
                    CredentialTypeGeneric,
                    0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != ErrorNotFound)
                    throw Unavailable(error);
            }
            return Task.CompletedTask;
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "Windows Credential Manager is unavailable.",
                error);
        }
    }

    private static SecretStoreUnavailableException Unavailable(int error) =>
        new(
            "Windows Credential Manager could not complete the operation.",
            new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credential);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(
        [In] ref Credential credential,
        uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(
        string target,
        uint type,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}

public sealed class MacOSKeychainSecretStore : ISecretStoreBackend
{
    private const int ItemNotFound = -25300;
    private static readonly byte[] Service =
        Encoding.UTF8.GetBytes("MusicLibraryManager");

    public SecretStoreKind Kind => SecretStoreKind.MacOSKeychain;
    public bool IsPersistent => true;

    public Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        byte[] account = Encoding.UTF8.GetBytes(key);
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)Service.Length),
                Service,
                checked((uint)account.Length),
                account,
                out uint length,
                out IntPtr data,
                out IntPtr item);
            if (status == ItemNotFound)
                return Task.FromResult<string?>(null);
            EnsureSuccess(status);
            try
            {
                byte[] bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                try
                {
                    return Task.FromResult<string?>(
                        Encoding.UTF8.GetString(bytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "macOS Keychain is unavailable.",
                error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(account);
        }
    }

    public Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        ct.ThrowIfCancellationRequested();
        byte[] account = Encoding.UTF8.GetBytes(key);
        byte[] password = Encoding.UTF8.GetBytes(secret);
        try
        {
            int find = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)Service.Length),
                Service,
                checked((uint)account.Length),
                account,
                out _,
                out IntPtr existingData,
                out IntPtr item);
            if (find == 0)
            {
                SecKeychainItemFreeContent(
                    IntPtr.Zero, existingData);
                try
                {
                    EnsureSuccess(SecKeychainItemModifyContent(
                        item,
                        IntPtr.Zero,
                        checked((uint)password.Length),
                        password));
                }
                finally
                {
                    if (item != IntPtr.Zero)
                        CFRelease(item);
                }
            }
            else if (find == ItemNotFound)
            {
                EnsureSuccess(SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    checked((uint)Service.Length),
                    Service,
                    checked((uint)account.Length),
                    account,
                    checked((uint)password.Length),
                    password,
                    out IntPtr created));
                if (created != IntPtr.Zero)
                    CFRelease(created);
            }
            else
            {
                EnsureSuccess(find);
            }
            return Task.CompletedTask;
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "macOS Keychain is unavailable.",
                error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(account);
            CryptographicOperations.ZeroMemory(password);
        }
    }

    public Task DeleteAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ct.ThrowIfCancellationRequested();
        byte[] account = Encoding.UTF8.GetBytes(key);
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                checked((uint)Service.Length),
                Service,
                checked((uint)account.Length),
                account,
                out _,
                out IntPtr data,
                out IntPtr item);
            if (status == ItemNotFound)
                return Task.CompletedTask;
            EnsureSuccess(status);
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                EnsureSuccess(SecKeychainItemDelete(item));
            }
            finally
            {
                if (item != IntPtr.Zero)
                    CFRelease(item);
            }
            return Task.CompletedTask;
        }
        catch (SecretStoreUnavailableException)
        {
            throw;
        }
        catch (Exception error) when (
            error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreUnavailableException(
                "macOS Keychain is unavailable.",
                error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(account);
        }
    }

    private static void EnsureSuccess(int status)
    {
        if (status != 0)
            throw new SecretStoreUnavailableException(
                $"macOS Keychain returned status {status}.");
    }

    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyContent(
        IntPtr itemRef,
        IntPtr attrList,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(
        IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(
        IntPtr attrList,
        IntPtr data);

    [DllImport(CoreFoundationFramework)]
    private static extern void CFRelease(IntPtr value);
}

public sealed class LinuxSecretServiceSecretStore :
    ISecretStoreBackend
{
    public SecretStoreKind Kind =>
        SecretStoreKind.LinuxSecretService;
    public bool IsPersistent => true;

    public async Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        SecretCommandResult result = await RunAsync(
            ["lookup", "service", "MusicLibraryManager", "account", key],
            secret: null,
            ct);
        if (result.ExitCode == 0)
            return result.Output.TrimEnd('\r', '\n');
        if (result.ExitCode == 1 &&
            string.IsNullOrWhiteSpace(result.Error))
            return null;
        throw new SecretStoreUnavailableException(
            "Linux Secret Service is unavailable.");
    }

    public async Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        ArgumentNullException.ThrowIfNull(secret);
        SecretCommandResult result = await RunAsync(
            [
                "store",
                "--label",
                "MusicLibraryManager",
                "service",
                "MusicLibraryManager",
                "account",
                key,
            ],
            secret,
            ct);
        if (result.ExitCode != 0)
            throw new SecretStoreUnavailableException(
                "Linux Secret Service is unavailable.");
    }

    public async Task DeleteAsync(
        string key,
        CancellationToken ct = default)
    {
        SessionSecretStore.ValidateKey(key);
        SecretCommandResult result = await RunAsync(
            ["clear", "service", "MusicLibraryManager", "account", key],
            secret: null,
            ct);
        if (result.ExitCode is not 0 and not 1)
            throw new SecretStoreUnavailableException(
                "Linux Secret Service is unavailable.");
    }

    private static async Task<SecretCommandResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? secret,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo("secret-tool")
        {
            UseShellExecute = false,
            RedirectStandardInput = secret is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new SecretStoreUnavailableException(
                    "Linux Secret Service helper could not start.");
        }
        catch (Exception error) when (
            error is Win32Exception or InvalidOperationException)
        {
            throw new SecretStoreUnavailableException(
                "Linux Secret Service helper is unavailable.",
                error);
        }
        if (secret is not null)
        {
            await process.StandardInput.WriteLineAsync(
                secret.AsMemory(), ct);
            process.StandardInput.Close();
        }
        Task<string> output =
            process.StandardOutput.ReadToEndAsync(ct);
        Task<string> errorOutput =
            process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
        return new(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await errorOutput.ConfigureAwait(false));
    }

    private sealed record SecretCommandResult(
        int ExitCode,
        string Output,
        string Error);
}

internal sealed class UnavailableSecretStore : ISecretStoreBackend
{
    public SecretStoreKind Kind => SecretStoreKind.SessionOnly;
    public bool IsPersistent => false;

    public Task<string?> ReadAsync(
        string key,
        CancellationToken ct = default) =>
        throw new SecretStoreUnavailableException(
            "No native secret store is available.");

    public Task WriteAsync(
        string key,
        string secret,
        CancellationToken ct = default) =>
        throw new SecretStoreUnavailableException(
            "No native secret store is available.");

    public Task DeleteAsync(
        string key,
        CancellationToken ct = default) =>
        throw new SecretStoreUnavailableException(
            "No native secret store is available.");
}
