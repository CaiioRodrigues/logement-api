namespace Convivium.Infrastructure.Banking;

/// <summary>Rotina de conferencia dos boletos, lida da secao "Banking".</summary>
public sealed class BankingOptions
{
    public const string SectionName = "Banking";

    /// <summary>Liga a conferencia periodica. Desligada, a baixa so acontece pelo botao.</summary>
    public bool SyncEnabled { get; set; } = true;

    /// <summary>
    /// Intervalo entre conferencias. Trinta minutos dao baixa no mesmo turno
    /// em que o morador pagou, e um predio de 40 unidades gasta 40 das 120
    /// consultas por minuto que o Inter permite — sobra folga para a tela.
    /// </summary>
    public int SyncMinutes { get; set; } = 30;
}
