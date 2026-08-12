using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

sealed class LocalSecretStore
{
    private readonly object sync = new();
    private readonly string filePath;

    public LocalSecretStore()
    {
        filePath = Environment.GetEnvironmentVariable("TIA_SECRET_FILE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TiaCodexConsole", "secrets.json");
    }

    public bool HasOpenAiKey => Load()?.EncryptedOpenAiKey is { Length: > 0 };
    public string? Model => Load()?.Model;

    public string? GetOpenAiKey()
    {
        var encrypted = Load()?.EncryptedOpenAiKey;
        if (string.IsNullOrWhiteSpace(encrypted)) return null;
        try { return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(encrypted))); }
        catch (Exception ex) { throw new InvalidOperationException("The locally encrypted OpenAI API key could not be decrypted by this Windows user.", ex); }
    }

    public void SaveOpenAi(string apiKey, string model)
    {
        lock (sync)
        {
            var directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);
            var data = new SecretData(Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(apiKey))), model);
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            File.Move(tempPath, filePath, true);
        }
    }

    private SecretData? Load()
    {
        lock (sync)
        {
            if (!File.Exists(filePath)) return null;
            try { return JsonSerializer.Deserialize<SecretData>(File.ReadAllText(filePath)); }
            catch (Exception ex) { throw new InvalidOperationException("The local secret configuration is invalid.", ex); }
        }
    }

    private static byte[] Protect(byte[] input) => Transform(input, protect: true);
    private static byte[] Unprotect(byte[] input) => Transform(input, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = new DataBlob();
        var outputBlob = new DataBlob();
        try
        {
            inputBlob.Size = input.Length;
            inputBlob.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, inputBlob.Data, input.Length);
            var success = protect
                ? CryptProtectData(ref inputBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out outputBlob);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Size);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero) Marshal.FreeHGlobal(inputBlob.Data);
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record SecretData(string EncryptedOpenAiKey, string Model);
}
