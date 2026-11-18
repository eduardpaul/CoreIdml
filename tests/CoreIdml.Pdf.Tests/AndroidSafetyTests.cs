using System.Text;

namespace CoreIdml.Pdf.Tests;

/// <summary>
/// Guards for the Native AOT build on Android (Flutter): .NET crypto aborts that process, so the shipped PDF code
/// must not call it, and encrypted PDFs must be refused before PdfPig would decrypt them.
/// </summary>
public class AndroidSafetyTests
{
    [Theory]
    [InlineData("trailer\n<< /Size 9 /Root 1 0 R /Encrypt 5 0 R >>", true)]
    [InlineData("<< /Type /XRef /Encrypt 12 0 R /W [1 2 1] >>", true)]
    [InlineData("<< /Encrypt<< /Filter /Standard >> >>", true)]
    [InlineData("trailer\n<< /Size 9 /Root 1 0 R >>", false)]
    [InlineData("<< /EncryptMetadata false >>", false)]
    public void Encrypted_pdfs_are_detected(string pdf, bool encrypted)
    {
        Assert.Equal(encrypted, PdfSource.IsEncrypted(Encoding.ASCII.GetBytes("%PDF-1.7\n" + pdf + "\n%%EOF")));
    }

    [Fact]
    public void Shipped_pdf_code_does_not_reference_dotnet_crypto()
    {
        var assembly = typeof(IdmlPdf).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(name => name.Name);
        Assert.DoesNotContain("System.Security.Cryptography", references);
        Assert.False(PdfSource.CryptoUnavailable, "the desktop test host has crypto available");
    }
}
