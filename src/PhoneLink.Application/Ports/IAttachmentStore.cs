using PhoneLink.Application.Features.Attachments;

namespace PhoneLink.Application.Ports;

public interface IAttachmentStore
{
    Task SaveVerifiedAsync(ImportedAttachment attachment, CancellationToken cancellationToken);

    Task<IReadOnlyList<ImportedAttachment>> ListAsync(int limit, CancellationToken cancellationToken);
}
