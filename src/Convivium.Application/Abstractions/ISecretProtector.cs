namespace Convivium.Application.Abstractions;

/// <summary>
/// Cifra e decifra os segredos que precisam ficar guardados no banco de dados.
/// </summary>
/// <remarks>
/// Existe por causa das credenciais bancarias de cada condominio: um backup do
/// Postgres que vaze nao pode entregar junto o certificado e o client secret
/// que emitem boleto em nome do condominio. A chave mora na configuracao do
/// servidor, fora do banco — e so com as duas coisas o segredo volta.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Cifra <paramref name="plaintext"/> amarrado a <paramref name="purpose"/>.
    /// </summary>
    /// <param name="purpose">
    /// Rotulo do campo, ex.: <c>"inter.client-secret"</c>. Entra na conta da
    /// autenticacao, entao um blob copiado de um campo para outro nao decifra.
    /// </param>
    string Protect(string plaintext, string purpose);

    /// <summary>Devolve o texto original, ou lanca se o blob foi mexido.</summary>
    string Unprotect(string ciphertext, string purpose);

    /// <summary>Ha chave configurada? Sem ela, nada de credencial bancaria.</summary>
    bool IsAvailable { get; }
}
