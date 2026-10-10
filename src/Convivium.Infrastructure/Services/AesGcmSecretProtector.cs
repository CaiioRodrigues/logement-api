namespace Convivium.Infrastructure.Services;

using System.Security.Cryptography;
using System.Text;
using Convivium.Application.Abstractions;

/// <summary>
/// Cifra segredos com AES-256-GCM e uma chave unica do servidor.
/// </summary>
/// <remarks>
/// <para>
/// GCM foi escolhido por ser autenticado: alem de esconder o conteudo, ele
/// recusa um blob que tenha sido alterado. Sem isso, quem conseguisse escrever
/// no banco poderia trocar o certificado de um condominio pelo seu e passar a
/// receber os boletos — e a troca so apareceria no extrato.
/// </para>
/// <para>
/// O formato e <c>v1:</c> seguido de base64 de nonce (12 bytes), tag (16) e
/// texto cifrado. O prefixo de versao existe para um dia trocar de algoritmo
/// sem precisar adivinhar o que cada registro antigo guarda.
/// </para>
/// </remarks>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    private const string Version = "v1:";
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[]? key;

    public AesGcmSecretProtector(string? base64Key)
    {
        if (string.IsNullOrWhiteSpace(base64Key))
        {
            return;
        }

        if (!Convert.TryFromBase64String(base64Key, new byte[KeyBytes + 1], out int written)
            || written != KeyBytes)
        {
            throw new InvalidOperationException(
                "App:SecretsKey precisa ser uma chave de 32 bytes em base64. " +
                "Gere uma com: openssl rand -base64 32");
        }

        key = Convert.FromBase64String(base64Key);
    }

    public bool IsAvailable => key is not null;

    public string Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        byte[] secretKey = RequireKey();
        byte[] content = Encoding.UTF8.GetBytes(plaintext);

        byte[] envelope = new byte[NonceBytes + TagBytes + content.Length];
        Span<byte> nonce = envelope.AsSpan(0, NonceBytes);
        Span<byte> tag = envelope.AsSpan(NonceBytes, TagBytes);
        Span<byte> cipher = envelope.AsSpan(NonceBytes + TagBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(secretKey, TagBytes);
        aes.Encrypt(nonce, content, cipher, tag, Encoding.UTF8.GetBytes(purpose));

        return Version + Convert.ToBase64String(envelope);
    }

    public string Unprotect(string ciphertext, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        byte[] secretKey = RequireKey();

        if (!ciphertext.StartsWith(Version, StringComparison.Ordinal))
        {
            throw new CryptographicException("Segredo guardado em formato desconhecido.");
        }

        byte[] envelope = Convert.FromBase64String(ciphertext[Version.Length..]);

        if (envelope.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Segredo guardado está truncado.");
        }

        byte[] content = new byte[envelope.Length - NonceBytes - TagBytes];

        using var aes = new AesGcm(secretKey, TagBytes);
        aes.Decrypt(
            envelope.AsSpan(0, NonceBytes),
            envelope.AsSpan(NonceBytes + TagBytes),
            envelope.AsSpan(NonceBytes, TagBytes),
            content,
            Encoding.UTF8.GetBytes(purpose));

        return Encoding.UTF8.GetString(content);
    }

    private byte[] RequireKey() =>
        key ?? throw new InvalidOperationException(
            "Nenhuma chave de criptografia configurada. Defina App:SecretsKey " +
            "(ou a variável App__SecretsKey) para guardar credenciais bancárias.");
}
