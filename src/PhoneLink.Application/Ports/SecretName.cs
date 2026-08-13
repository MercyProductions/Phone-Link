namespace PhoneLink.Application.Ports;

public readonly record struct SecretName
{
    public SecretName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 200 || value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '/' or '_')))
        {
            throw new ArgumentException(
                "A secret name contains an unsupported character or is too long.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

