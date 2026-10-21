using System.Security.Cryptography;

namespace AutoCadMcpInstaller;

internal static class ReleaseAssetIntegrity
{
    public static string VerifySha256(byte[] content, string sidecar)
    {
        var expected = sidecar.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expected is null || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("Release asset SHA-256 sidecar is malformed.");
        var actual = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release asset SHA-256 verification failed.");
        return actual;
    }
}
