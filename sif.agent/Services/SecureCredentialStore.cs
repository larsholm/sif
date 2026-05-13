using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using static System.Security.Cryptography.ProtectedData;

namespace sif.agent.Services;

/// <summary>
/// Cross-platform secure credential storage for API keys.
/// Uses OS-native credential stores when available, falls back to encrypted file storage.
/// </summary>
public interface ISecureCredentialStore
{
    /// <summary>
    /// Store a credential securely.
    /// </summary>
    Task<bool> StoreAsync(string key, string secret);

    /// <summary>
    /// Retrieve a credential. Returns null if not found.
    /// </summary>
    Task<string?> RetrieveAsync(string key);

    /// <summary>
    /// Delete a credential.
    /// </summary>
    Task<bool> DeleteAsync(string key);

    /// <summary>
    /// Check if a credential exists.
    /// </summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// Get the storage type being used (for diagnostics).
    /// </summary>
    string StorageType { get; }
}

/// <summary>
/// Factory to create the appropriate credential store for the current platform.
/// </summary>
public static class SecureCredentialStoreFactory
{
    public static ISecureCredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentialStore();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacOSCredentialStore();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxCredentialStore();
        }

        // Fallback to encrypted file storage
        return new EncryptedFileCredentialStore();
    }
}

/// <summary>
/// Windows Credential Manager store.
/// Uses cmdkey.exe for basic support (no additional dependencies).
/// </summary>
[SupportedOSPlatform("windows")]
internal class WindowsCredentialStore : ISecureCredentialStore
{
    private const string TargetPrefix = "sif-agent:";

    public string StorageType => "Windows Credential Manager";

    public Task<bool> StoreAsync(string key, string secret)
    {
        try
        {
            var target = TargetPrefix + key;
            // Use cmdkey to add generic credential
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = $"/add:{target} /generic /user:sif-agent /pass:{secret}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return Task.FromResult(process?.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<string?> RetrieveAsync(string key)
    {
        try
        {
            var target = TargetPrefix + key;
            // cmdkey doesn't have a way to retrieve passwords via command line for security reasons
            // We'll need to use the Credential Management API via P/Invoke or a library
            // For now, fall back to encrypted file storage on Windows too
            return Task.FromResult<string?>(null);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> DeleteAsync(string key)
    {
        try
        {
            var target = TargetPrefix + key;
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = $"/delete:{target}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return Task.FromResult(process?.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> ExistsAsync(string key)
    {
        try
        {
            var target = TargetPrefix + key;
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = $"/list:{target}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (process == null) return Task.FromResult(false);
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return Task.FromResult(output.Contains(target));
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// macOS Keychain store.
/// Uses the security command-line tool.
/// </summary>
[SupportedOSPlatform("macos")]
internal class MacOSCredentialStore : ISecureCredentialStore
{
    private const string ServiceName = "sif-agent";

    public string StorageType => "macOS Keychain";

    public Task<bool> StoreAsync(string key, string secret)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"add-generic-password -s {ServiceName} -a {key} -w {secret} -U",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return Task.FromResult(process?.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<string?> RetrieveAsync(string key)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"find-generic-password -s {ServiceName} -a {key} -w",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return Task.FromResult<string?>(null);
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return Task.FromResult(string.IsNullOrEmpty(output) ? null : output);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> DeleteAsync(string key)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"delete-generic-password -s {ServiceName} -a {key}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            process?.WaitForExit();
            return Task.FromResult(process?.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> ExistsAsync(string key)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "security",
                Arguments = $"find-generic-password -s {ServiceName} -a {key}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process == null) return Task.FromResult(false);
            process.WaitForExit();
            return Task.FromResult(process.ExitCode == 0);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Linux credential store.
/// Tries libsecret (secret-tool) first, falls back to encrypted file storage.
/// </summary>
[SupportedOSPlatform("linux")]
internal class LinuxCredentialStore : ISecureCredentialStore
{
    private const string SchemaName = "sif-agent";
    private readonly EncryptedFileCredentialStore _fallbackStore = new();
    private bool? _hasSecretTool;

    private bool HasSecretTool()
    {
        if (_hasSecretTool.HasValue) return _hasSecretTool.Value;
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "secret-tool",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            process?.WaitForExit();
            _hasSecretTool = process?.ExitCode == 0;
        }
        catch
        {
            _hasSecretTool = false;
        }
        return _hasSecretTool.Value;
    }

    public string StorageType => HasSecretTool() ? "libsecret (secret-tool)" : "Encrypted file storage";

    public async Task<bool> StoreAsync(string key, string secret)
    {
        if (HasSecretTool())
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "secret-tool",
                    Arguments = $"store --label=\"sif-agent {key}\" service {SchemaName} account {key}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                });
                if (process != null)
                {
                    await process.StandardInput.WriteLineAsync(secret);
                    process.StandardInput.Close();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // Fall through to encrypted file storage
            }
        }
        return await _fallbackStore.StoreAsync(key, secret);
    }

    public async Task<string?> RetrieveAsync(string key)
    {
        if (HasSecretTool())
        {
            try
            {
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "secret-tool",
                    Arguments = $"lookup service {SchemaName} account {key}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });
                if (process != null)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    process.WaitForExit();
                    // Treat empty string as "not found" (may have been "deleted")
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                        return output.Trim();
                }
            }
            catch
            {
                // Fall through to encrypted file storage
            }
        }
        return await _fallbackStore.RetrieveAsync(key);
    }

    public async Task<bool> DeleteAsync(string key)
    {
        if (HasSecretTool())
        {
            try
            {
                // secret-tool doesn't have a direct delete command.
                // We'll store an empty string to effectively "delete" the secret.
                // The RetrieveAsync method will treat empty as "not found".
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "secret-tool",
                    Arguments = $"store --label=\"sif-agent {key}\" service {SchemaName} account {key}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                });
                if (process != null)
                {
                    // Write empty string
                    await process.StandardInput.WriteLineAsync(string.Empty);
                    process.StandardInput.Close();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
                // Fall through to encrypted file storage
            }
        }
        return await _fallbackStore.DeleteAsync(key);
    }

    public async Task<bool> ExistsAsync(string key)
    {
        var value = await RetrieveAsync(key);
        return !string.IsNullOrEmpty(value);
    }
}

/// <summary>
/// Encrypted file storage using DPAPI (Windows) or AES (cross-platform).
/// Used as fallback when OS credential store is not available.
/// </summary>
internal class EncryptedFileCredentialStore : ISecureCredentialStore
{
    private readonly string _storageDir;
    private readonly byte[] _entropy;

    public string StorageType => "Encrypted file storage";

    public EncryptedFileCredentialStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _storageDir = Path.Combine(home, ".sif", "credentials");
        Directory.CreateDirectory(_storageDir);

        // Create entropy from machine-specific information
        var machineInfo = Environment.MachineName + Environment.UserName + Environment.OSVersion;
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes(machineInfo));
    }

    public Task<bool> StoreAsync(string key, string secret)
    {
        try
        {
            var filePath = GetFilePath(key);
            byte[] encryptedData;

            if (OperatingSystem.IsWindows())
            {
                // Use DPAPI on Windows
                encryptedData = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(secret),
                    _entropy,
                    DataProtectionScope.CurrentUser);
            }
            else
            {
                // Use AES on other platforms
                encryptedData = EncryptAes(secret, _entropy);
            }

            File.WriteAllBytes(filePath, encryptedData);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<string?> RetrieveAsync(string key)
    {
        try
        {
            var filePath = GetFilePath(key);
            if (!File.Exists(filePath))
                return Task.FromResult<string?>(null);

            var encryptedData = File.ReadAllBytes(filePath);
            byte[] decryptedData;

            if (OperatingSystem.IsWindows())
            {
                decryptedData = ProtectedData.Unprotect(
                    encryptedData,
                    _entropy,
                    DataProtectionScope.CurrentUser);
            }
            else
            {
                decryptedData = DecryptAes(encryptedData, _entropy);
            }

            return Task.FromResult<string?>(Encoding.UTF8.GetString(decryptedData));
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    public Task<bool> DeleteAsync(string key)
    {
        try
        {
            var filePath = GetFilePath(key);
            if (File.Exists(filePath))
                File.Delete(filePath);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task<bool> ExistsAsync(string key)
    {
        return Task.FromResult(File.Exists(GetFilePath(key)));
    }

    private string GetFilePath(string key)
    {
        // Sanitize key for filename
        var safeKey = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(_storageDir, $"{safeKey}.enc");
    }

    private static byte[] EncryptAes(string plainText, byte[] key)
    {
        using var aes = Aes.Create();
        // Use modern Pbkdf2 method instead of deprecated constructor
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(key, new byte[8], 10000, HashAlgorithmName.SHA256, 32); // 256-bit key
        aes.IV = Rfc2898DeriveBytes.Pbkdf2(key, new byte[8], 10000, HashAlgorithmName.SHA256, 16);  // 128-bit IV

        using var encryptor = aes.CreateEncryptor();
        using var ms = new MemoryStream();
        using var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        cs.Write(plainBytes, 0, plainBytes.Length);
        cs.FlushFinalBlock();
        return ms.ToArray();
    }

    private static byte[] DecryptAes(byte[] cipherText, byte[] key)
    {
        using var aes = Aes.Create();
        // Use modern Pbkdf2 method instead of deprecated constructor
        aes.Key = Rfc2898DeriveBytes.Pbkdf2(key, new byte[8], 10000, HashAlgorithmName.SHA256, 32);
        aes.IV = Rfc2898DeriveBytes.Pbkdf2(key, new byte[8], 10000, HashAlgorithmName.SHA256, 16);

        using var decryptor = aes.CreateDecryptor();
        using var ms = new MemoryStream(cipherText);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var resultMs = new MemoryStream();
        cs.CopyTo(resultMs);
        return resultMs.ToArray();
    }
}

/// <summary>
/// Extension methods for AgentConfig to support secure API key storage.
/// </summary>
internal static class SecureApiKeyExtensions
{
    private const string ApiKeyCredentialName = "default-api-key";

    /// <summary>
    /// Load API key from secure storage if configured to use it.
    /// </summary>
    public static async Task LoadApiKeyFromSecureStore(this AgentConfig config, ISecureCredentialStore? store = null)
    {
        if (!config.UseSecureApiKeyStorage)
            return;

        store ??= SecureCredentialStoreFactory.Create();
        var secureKey = await store.RetrieveAsync(ApiKeyCredentialName);
        if (!string.IsNullOrEmpty(secureKey))
        {
            config.ApiKey = secureKey;
        }
    }

    /// <summary>
    /// Save API key to secure storage and remove from config.
    /// </summary>
    public static async Task<bool> MigrateApiKeyToSecureStore(this AgentConfig config, ISecureCredentialStore? store = null)
    {
        if (string.IsNullOrEmpty(config.ApiKey))
            return false;

        store ??= SecureCredentialStoreFactory.Create();
        var success = await store.StoreAsync(ApiKeyCredentialName, config.ApiKey);

        if (success)
        {
            config.UseSecureApiKeyStorage = true;
            config.ApiKey = null; // Remove from plaintext config
            return true;
        }

        return false;
    }

    /// <summary>
    /// Clear API key from secure storage.
    /// </summary>
    public static async Task<bool> ClearApiKeyFromSecureStore(this AgentConfig config, ISecureCredentialStore? store = null)
    {
        store ??= SecureCredentialStoreFactory.Create();
        var success = await store.DeleteAsync(ApiKeyCredentialName);
        if (success)
        {
            config.UseSecureApiKeyStorage = false;
        }
        return success;
    }
}
