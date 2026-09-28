namespace Convivium.Application.Notifications;

using Convivium.Domain.Notifications;

/// <summary>Uma mensagem pronta para envio.</summary>
public sealed record OutgoingEmail
{
    public required string ToAddress { get; init; }

    public string? ToName { get; init; }

    /// <summary>
    /// Nome que aparece como remetente. Nulo usa o nome geral da configuracao.
    /// </summary>
    /// <remarks>
    /// E o nome do condominio, nao o da administradora. O morador reconhece o
    /// predio dele; "Logement" nao diz nada para quem nunca ouviu falar da
    /// empresa, e num e-mail que cobra dinheiro remetente estranho vira golpe.
    /// </remarks>
    public string? FromName { get; init; }

    public required string Subject { get; init; }

    public required string HtmlBody { get; init; }

    public string? TextBody { get; init; }

    public IReadOnlyList<EmailAttachment> Attachments { get; init; } = [];
}

public sealed record EmailAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Entrega efetiva da mensagem. Fica atras de uma interface para que trocar
/// SMTP por um provedor de API (SendGrid, SES) nao toque na regra de negocio.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(OutgoingEmail email, CancellationToken cancellationToken = default);
}

/// <summary>Conteudo renderizado de um e-mail.</summary>
public sealed record EmailContent(string Subject, string HtmlBody, string TextBody);

/// <summary>Resultado do enfileiramento em lote.</summary>
public sealed record EmailQueueResult(int Queued, int SkippedWithoutEmail, IReadOnlyList<string> Skipped)
{
    public bool HasSkipped => SkippedWithoutEmail > 0;
}

/// <summary>Uma mensagem da fila, para acompanhamento na interface.</summary>
public sealed record EmailMessageDto(
    Guid Id,
    EmailKind Kind,
    string ToAddress,
    string? ToName,
    string Subject,
    EmailStatus Status,
    int Attempts,
    string? LastError,
    DateTimeOffset ScheduledFor,
    DateTimeOffset? SentAt,
    Guid? ChargeId);
