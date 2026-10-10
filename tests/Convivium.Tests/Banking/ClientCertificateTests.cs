namespace Convivium.Tests.Banking;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Convivium.Application.Banking;
using Convivium.Domain.Common;

/// <summary>
/// O certificado e lido no cadastro, nao na primeira emissao: arquivo
/// trocado ou senha errada precisam aparecer com o sindico ainda na tela.
/// </summary>
public class ClientCertificateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static X509Certificate2 NewCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Condominio Teste, O=Logement",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static (string Certificate, string Key) Pem(X509Certificate2 certificate) =>
        (certificate.ExportCertificatePem(), certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

    /// <summary>O formato que o Inter entrega: um .crt e um .key separados.</summary>
    [Fact]
    public void ParPemViraPkcs12ComTitularEValidade()
    {
        using X509Certificate2 original = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        (string crt, string key) = Pem(original);

        ClientCertificate.Normalized result = ClientCertificate.FromPem(crt, key, Now);

        result.Subject.ShouldContain("CN=Condominio Teste");
        result.ExpiresAt.Date.ShouldBe(Now.AddYears(1).UtcDateTime.Date);
        result.Password.ShouldBeNull();

        // O PKCS#12 gerado precisa carregar com a chave privada: e o que o
        // cliente HTTP apresenta no handshake com o banco.
        using X509Certificate2 loaded = X509CertificateLoader.LoadPkcs12(result.Pkcs12, null);
        loaded.HasPrivateKey.ShouldBeTrue();
        loaded.Thumbprint.ShouldBe(original.Thumbprint);
    }

    /// <summary>Dois downloads diferentes do banco: o certificado de um, a chave de outro.</summary>
    [Fact]
    public void ChaveDeOutroCertificadoERecusada()
    {
        using X509Certificate2 first = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        using X509Certificate2 second = NewCertificate(Now.AddDays(-1), Now.AddYears(1));

        Should.Throw<DomainException>(() =>
            ClientCertificate.FromPem(Pem(first).Certificate, Pem(second).Key, Now))
            .Message.ShouldContain("não formam um par");
    }

    [Fact]
    public void ArquivoTrocadoDeCampoERecusado()
    {
        using X509Certificate2 certificate = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        (string crt, string key) = Pem(certificate);

        Should.Throw<DomainException>(() => ClientCertificate.FromPem(key, crt, Now));
    }

    [Fact]
    public void CertificadoVencidoERecusado()
    {
        using X509Certificate2 expired = NewCertificate(Now.AddYears(-2), Now.AddDays(-1));
        (string crt, string key) = Pem(expired);

        Should.Throw<DomainException>(() => ClientCertificate.FromPem(crt, key, Now))
            .Message.ShouldContain("venceu");
    }

    [Fact]
    public void PfxComSenhaCertaEAceito()
    {
        using X509Certificate2 certificate = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        byte[] pfx = certificate.Export(X509ContentType.Pkcs12, "senha-do-pfx");

        ClientCertificate.Normalized result = ClientCertificate.FromPkcs12(pfx, "senha-do-pfx", Now);

        result.Password.ShouldBe("senha-do-pfx");
        result.Subject.ShouldContain("Condominio Teste");
    }

    [Fact]
    public void PfxComSenhaErradaERecusado()
    {
        using X509Certificate2 certificate = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        byte[] pfx = certificate.Export(X509ContentType.Pkcs12, "senha-do-pfx");

        Should.Throw<DomainException>(() => ClientCertificate.FromPkcs12(pfx, "outra", Now))
            .Message.ShouldContain("senha");
    }

    /// <summary>Sem a chave privada o banco recusa o handshake; melhor saber no cadastro.</summary>
    [Fact]
    public void CertificadoSemChavePrivadaERecusado()
    {
        using X509Certificate2 certificate = NewCertificate(Now.AddDays(-1), Now.AddYears(1));
        byte[] onlyPublic = certificate.Export(X509ContentType.Cert);
        using X509Certificate2 publicOnly = X509CertificateLoader.LoadCertificate(onlyPublic);
        byte[] pfx = publicOnly.Export(X509ContentType.Pkcs12, "x");

        Should.Throw<DomainException>(() => ClientCertificate.FromPkcs12(pfx, "x", Now))
            .Message.ShouldContain("chave privada");
    }
}
