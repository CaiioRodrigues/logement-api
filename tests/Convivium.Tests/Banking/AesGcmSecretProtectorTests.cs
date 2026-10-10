namespace Convivium.Tests.Banking;

using System.Security.Cryptography;
using Convivium.Infrastructure.Services;

/// <summary>
/// O cofre das credenciais bancarias. O que importa aqui nao e so cifrar:
/// e recusar qualquer blob mexido, trocado de campo ou lido com outra chave.
/// </summary>
public class AesGcmSecretProtectorTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void CifraEDecifraDeVolta()
    {
        var protector = new AesGcmSecretProtector(NewKey());

        string cipher = protector.Protect("segredo-do-condominio", "c1:boleto.client-secret");

        cipher.ShouldStartWith("v1:");
        cipher.ShouldNotContain("segredo");
        protector.Unprotect(cipher, "c1:boleto.client-secret").ShouldBe("segredo-do-condominio");
    }

    /// <summary>Cada cifra usa um nonce novo: o mesmo segredo nunca gera o mesmo texto.</summary>
    [Fact]
    public void MesmoSegredoGeraCifrasDiferentes()
    {
        var protector = new AesGcmSecretProtector(NewKey());

        protector.Protect("x", "p").ShouldNotBe(protector.Protect("x", "p"));
    }

    /// <summary>
    /// O rotulo entra na autenticacao: o certificado de um condominio copiado
    /// para a linha de outro, direto no banco de dados, nao decifra.
    /// </summary>
    [Fact]
    public void BlobCopiadoParaOutroCampoNaoDecifra()
    {
        var protector = new AesGcmSecretProtector(NewKey());
        string cipher = protector.Protect("certificado", "condominio-a:boleto.certificate");

        Should.Throw<CryptographicException>(() =>
            protector.Unprotect(cipher, "condominio-b:boleto.certificate"));
    }

    [Fact]
    public void BlobAlteradoERecusado()
    {
        var protector = new AesGcmSecretProtector(NewKey());
        string cipher = protector.Protect("segredo", "p");

        byte[] envelope = Convert.FromBase64String(cipher[3..]);
        envelope[^1] ^= 0x01;
        string tampered = "v1:" + Convert.ToBase64String(envelope);

        Should.Throw<CryptographicException>(() => protector.Unprotect(tampered, "p"));
    }

    /// <summary>Trocar a chave do servidor torna os segredos ilegiveis — e nao lixo silencioso.</summary>
    [Fact]
    public void OutraChaveNaoDecifra()
    {
        string cipher = new AesGcmSecretProtector(NewKey()).Protect("segredo", "p");

        Should.Throw<CryptographicException>(() =>
            new AesGcmSecretProtector(NewKey()).Unprotect(cipher, "p"));
    }

    [Fact]
    public void SemChaveConfiguradaFicaIndisponivelSemDerrubarOSistema()
    {
        var protector = new AesGcmSecretProtector(null);

        protector.IsAvailable.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => protector.Protect("x", "p"))
            .Message.ShouldContain("App:SecretsKey");
    }

    [Theory]
    [InlineData("curta")]
    [InlineData("bm90LTMyLWJ5dGVz")]
    public void ChaveForaDoFormatoEhRecusadaNaSubida(string key) =>
        Should.Throw<InvalidOperationException>(() => new AesGcmSecretProtector(key))
            .Message.ShouldContain("openssl rand -base64 32");
}
