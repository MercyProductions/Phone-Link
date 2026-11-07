using System.Text;

namespace PhoneLink.Application.Features.Calling;

public sealed class CompanionMessageDraft
{
    public const int MaximumBodyBytes = 1_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private CompanionMessageDraft(string recipient, string body) { Recipient = recipient; Body = body; }
    public string Recipient { get; }
    public string Body { get; }
    public override string ToString() => "Companion message draft (content redacted)";

    public static CompanionMessageDraft Create(string recipient, string body)
    {
        var normalized = TelephoneDialString.Normalize(recipient);
        if (normalized.Contains('*', StringComparison.Ordinal) || normalized.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException("Enter one telephone number, not a service or dialing code.", nameof(recipient));
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        if (body.Length > MaximumBodyBytes) throw new ArgumentException("The draft exceeds the 1,000-byte limit.", nameof(body));
        var text = body.ReplaceLineEndings("\n");
        if (text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')) ||
            StrictUtf8.GetByteCount(text) > MaximumBodyBytes)
            throw new ArgumentException("The draft contains unsupported controls or exceeds 1,000 UTF-8 bytes.", nameof(body));
        return new(normalized, text);
    }
}
