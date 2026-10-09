namespace Convivium.Domain.Payments;

/// <summary>Estado de um boleto registrado em banco.</summary>
public enum BoletoState
{
    /// <summary>Aceito pelo banco, que ainda nao devolveu o codigo de barras.</summary>
    Pending = 0,

    /// <summary>Registrado na camara de compensacao e pagavel.</summary>
    Registered = 1,

    Paid = 2,

    Cancelled = 3,

    /// <summary>Venceu e saiu da agenda do banco sem ser pago.</summary>
    Expired = 4,
}
