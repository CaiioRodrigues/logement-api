namespace Convivium.Infrastructure.Banking;

/// <summary>Endereços da API do Banco Inter, lidos da seção "Banking:Inter".</summary>
/// <remarks>
/// Ficam em configuracao, e nao em constante, porque o Inter ja trocou de
/// host entre versoes da API. Quando trocar de novo, o condominio volta ao ar
/// com uma variavel de ambiente, sem esperar uma nova versao do sistema.
/// </remarks>
public sealed class InterOptions
{
    public const string SectionName = "Banking:Inter";

    public string ProductionBaseUrl { get; set; } = "https://cdpj.partners.bancointer.com.br";

    public string SandboxBaseUrl { get; set; } = "https://cdpj-sandbox.partners.uatinter.co";

    /// <summary>Escopos pedidos no token. Leitura e escrita da cobranca.</summary>
    public string Scope { get; set; } = "boleto-cobranca.read boleto-cobranca.write";

    /// <summary>Tempo maximo de uma chamada ao banco.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    public string BaseUrl(bool sandbox) =>
        (sandbox ? SandboxBaseUrl : ProductionBaseUrl).TrimEnd('/');
}
