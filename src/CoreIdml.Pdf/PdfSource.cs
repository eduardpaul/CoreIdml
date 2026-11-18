using PdfPigDocument = UglyToad.PdfPig.PdfDocument;

namespace CoreIdml.Pdf;

/// <summary>Opens PDFs with PdfPig, refusing encrypted files where decrypting them would kill the process.</summary>
internal static class PdfSource
{
    /// <summary>
    /// Decrypting would abort the process ("Cannot get required symbol ... from libssl"): see
    /// <see cref="AndroidRuntime.IsNativeLibraryOnAndroid"/>. PdfPig needs crypto (MD5, RC4, AES) only to decrypt
    /// encrypted PDFs, so those are refused up front.
    /// </summary>
    internal static bool CryptoUnavailable => AndroidRuntime.IsNativeLibraryOnAndroid;

    public static PdfPigDocument Open(byte[] data, string description)
    {
        if (CryptoUnavailable && IsEncrypted(data))
        {
            throw new IdmlException(
                $"{description} is encrypted (password protected). Decrypting PDFs is not available in the native Android build; " +
                "remove the protection (for example by printing it to a new PDF) and try again.");
        }
        try
        {
            return PdfPigDocument.Open(data);
        }
        catch (Exception exception) when (exception is not (IdmlException or OutOfMemoryException or OperationCanceledException))
        {
            // PdfPig reports a damaged or foreign file with its own exception types; to callers it is bad input.
            throw new IdmlException(IdmlErrorKind.InvalidFormat, $"{description} is not a readable PDF: {exception.Message}", exception);
        }
    }

    /// <summary>An /Encrypt entry in a trailer or cross-reference stream dictionary marks an encrypted PDF.</summary>
    internal static bool IsEncrypted(ReadOnlySpan<byte> data)
    {
        var token = "/Encrypt"u8;
        for (var index = data.IndexOf(token); index >= 0; index = NextIndex(data, index + token.Length, token))
        {
            // Skip names that only start with "Encrypt" (e.g. /EncryptMetadata): the entry is followed by a delimiter.
            var next = index + token.Length;
            if (next >= data.Length || IsDelimiter(data[next]))
            {
                return true;
            }
        }
        return false;
    }

    private static int NextIndex(ReadOnlySpan<byte> data, int start, ReadOnlySpan<byte> token)
    {
        if (start >= data.Length)
        {
            return -1;
        }
        var relative = data[start..].IndexOf(token);
        return relative < 0 ? -1 : start + relative;
    }

    private static bool IsDelimiter(byte value) =>
        value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0 or (byte)'/' or (byte)'<' or (byte)'[' or (byte)'(';

    internal static string Describe(string path) => "'" + Path.GetFileName(path) + "'";
}
