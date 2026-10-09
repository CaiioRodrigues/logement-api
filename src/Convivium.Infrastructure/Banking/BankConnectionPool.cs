namespace Convivium.Infrastructure.Banking;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Convivium.Application.Banking;
using Microsoft.Extensions.Options;

/// <summary>
/// Guarda um <see cref="HttpClient"/> por certificado de condominio.
/// </summary>
/// <remarks>
/// <para>
/// A API do Inter exige mTLS: o certificado vai no handshake, nao num
/// cabecalho. Isso impede usar um <c>HttpClient</c> compartilhado, porque o
/// certificado pertence ao condominio, nao ao sistema.
/// </para>
/// <para>
/// Criar um cliente por chamada tambem nao serve — cada um abre conexoes
/// novas e, debaixo de carga, esgota as portas do servidor. Entao os clientes
/// ficam num mapa por condominio e certificado, e trocar o certificado cria
/// uma entrada nova em vez de reaproveitar a conexao antiga.
/// </para>
/// </remarks>
public sealed class BankConnectionPool(IOptions<InterOptions> options) : IDisposable
{
    /// <summary>
    /// Teto de clientes vivos. Com mais condominios que isto, o mapa e
    /// esvaziado inteiro: perder o pool de conexoes custa um handshake a mais,
    /// enquanto deixa-lo crescer sem limite custa memoria para sempre.
    /// </summary>
    private const int MaxClients = 64;

    private readonly ConcurrentDictionary<string, Lazy<HttpClient>> clients = new();

    public HttpClient Get(BankCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        if (clients.Count >= MaxClients)
        {
            Clear();
        }

        string key = Key(credentials);

        return clients.GetOrAdd(key, _ => new Lazy<HttpClient>(() => Create(credentials))).Value;
    }

    /// <summary>
    /// Descarta o cliente deste condominio. Chamado quando as credenciais
    /// mudam, para a conexao nao continuar apresentando o certificado velho.
    /// </summary>
    public void Invalidate(string cacheKey)
    {
        foreach (string key in clients.Keys.Where(k => k.StartsWith($"{cacheKey}|", StringComparison.Ordinal)))
        {
            if (clients.TryRemove(key, out Lazy<HttpClient>? removed) && removed.IsValueCreated)
            {
                removed.Value.Dispose();
            }
        }
    }

    public void Dispose() => Clear();

    private void Clear()
    {
        foreach (string key in clients.Keys)
        {
            if (clients.TryRemove(key, out Lazy<HttpClient>? removed) && removed.IsValueCreated)
            {
                removed.Value.Dispose();
            }
        }
    }

    /// <summary>
    /// A chave inclui o resumo do certificado: trocar o arquivo muda a chave e
    /// o cliente antigo e abandonado em vez de continuar valendo.
    /// </summary>
    private static string Key(BankCredentials credentials) =>
        $"{credentials.CacheKey}|{(credentials.Sandbox ? "sandbox" : "prod")}|" +
        Convert.ToHexString(SHA256.HashData(credentials.Certificate));

    private HttpClient Create(BankCredentials credentials)
    {
        X509Certificate2 certificate;

        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(
                credentials.Certificate,
                credentials.CertificatePassword,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException exception)
        {
            throw new BoletoProviderException(
                "Não consegui abrir o certificado do banco. Confira se o arquivo " +
                "está em formato PKCS#12 (.pfx ou .p12) e se a senha está certa.",
                exception);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();

            throw new BoletoProviderException(
                "O certificado enviado não tem a chave privada. O arquivo que o " +
                "banco gera precisa trazer o certificado e a chave juntos.");
        }

        var handler = new SocketsHttpHandler
        {
            // Reciclar a conexao de tempos em tempos faz o cliente reaprender
            // o DNS do banco sem precisar reiniciar a aplicacao.
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        };

        handler.SslOptions.ClientCertificates = new X509CertificateCollection { certificate };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(options.Value.BaseUrl(credentials.Sandbox) + "/"),
            Timeout = options.Value.Timeout,
        };
    }
}
