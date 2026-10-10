namespace Convivium.Application.Banking;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Convivium.Domain.Common;

/// <summary>
/// Le o certificado mTLS que o banco entrega e o converte para um formato so.
/// </summary>
/// <remarks>
/// O Inter entrega o certificado em dois arquivos PEM — um <c>.crt</c> e um
/// <c>.key</c> — e outros bancos entregam um <c>.pfx</c> com senha. Os dois
/// caminhos terminam aqui em PKCS#12, que e o que o cliente HTTP carrega.
/// Ler o arquivo no cadastro, e nao na primeira emissao, faz a senha errada
/// ou a chave trocada aparecerem na hora, com o sindico ainda na tela.
/// </remarks>
public static class ClientCertificate
{
    public sealed record Normalized(byte[] Pkcs12, string? Password, string Subject, DateTimeOffset ExpiresAt);

    /// <summary>A partir do par PEM: certificado e chave privada.</summary>
    public static Normalized FromPem(string certificatePem, string privateKeyPem, DateTimeOffset now)
    {
        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(certificatePem) || !certificatePem.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal),
            "O arquivo do certificado (.crt) não parece um certificado PEM.");

        DomainException.ThrowIf(
            string.IsNullOrWhiteSpace(privateKeyPem) || !privateKeyPem.Contains("PRIVATE KEY", StringComparison.Ordinal),
            "O arquivo da chave (.key) não parece uma chave privada PEM.");

        X509Certificate2 certificate;

        try
        {
            certificate = X509Certificate2.CreateFromPem(certificatePem, privateKeyPem);
        }
        catch (CryptographicException exception)
        {
            throw new DomainException(
                "O certificado e a chave não formam um par. Confira se os dois arquivos " +
                $"vieram do mesmo download no banco. ({exception.Message})");
        }

        using (certificate)
        {
            Validate(certificate, now);

            // Exportado sem senha: o PKCS#12 so existe cifrado pelo
            // ISecretProtector, e uma segunda senha guardada ao lado dele
            // nao protegeria nada a mais.
            byte[] pkcs12 = certificate.Export(X509ContentType.Pkcs12);

            return new Normalized(pkcs12, null, certificate.Subject, certificate.NotAfter.ToUniversalTime());
        }
    }

    /// <summary>A partir de um arquivo PKCS#12 (.pfx ou .p12) e a senha dele.</summary>
    public static Normalized FromPkcs12(byte[] content, string? password, DateTimeOffset now)
    {
        DomainException.ThrowIf(content is not { Length: > 0 }, "O arquivo do certificado está vazio.");

        X509Certificate2 certificate;

        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(
                content,
                string.IsNullOrEmpty(password) ? null : password,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException)
        {
            throw new DomainException(
                "Não consegui abrir o certificado. Confira a senha e se o arquivo é .pfx ou .p12.");
        }

        using (certificate)
        {
            Validate(certificate, now);

            return new Normalized(
                content,
                string.IsNullOrEmpty(password) ? null : password,
                certificate.Subject,
                certificate.NotAfter.ToUniversalTime());
        }
    }

    private static void Validate(X509Certificate2 certificate, DateTimeOffset now)
    {
        DomainException.ThrowIf(
            !certificate.HasPrivateKey,
            "O certificado veio sem a chave privada; sem ela o banco recusa a conexão.");

        DomainException.ThrowIf(
            certificate.NotAfter.ToUniversalTime() <= now.UtcDateTime,
            $"Este certificado venceu em {certificate.NotAfter:dd/MM/yyyy}. Gere um novo no internet banking.");
    }
}
