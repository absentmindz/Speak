using System;
using System.Security.Cryptography;
using System.Text;

namespace MaxFlowWindows.Core;

public static class SecureCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SpeakApp-Credential-v1");

    public static string Encrypt(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return "";

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] encrypted = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch
        {
            return "";
        }
    }

    public static string Decrypt(string encryptedBase64)
    {
        if (string.IsNullOrWhiteSpace(encryptedBase64))
            return "";

        try
        {
            byte[] encrypted = Convert.FromBase64String(encryptedBase64);
            byte[] plainBytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }

    public static bool IsEncrypted(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            Convert.FromBase64String(value);
            return value.Length > 20;
        }
        catch
        {
            return false;
        }
    }
}