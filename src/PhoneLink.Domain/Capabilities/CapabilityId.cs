namespace PhoneLink.Domain.Capabilities;

public readonly record struct CapabilityId
{
    public const int MaximumLength = 100;

    public CapabilityId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Trim();
        if (normalized.Length > MaximumLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterLower(character) ||
                  char.IsAsciiDigit(character) ||
                  character is '.' or '-')))
        {
            throw new ArgumentException(
                "A capability ID may contain only lowercase ASCII letters, digits, periods, and hyphens.",
                nameof(value));
        }

        Value = normalized;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

