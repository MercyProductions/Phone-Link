namespace PhoneLink.Application.Features.Calling;

public static class TelephoneDialString
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The phone number is too long.");
        }

        var buffer = new char[32];
        var length = 0;
        var digitCount = 0;
        foreach (var character in trimmed)
        {
            if (char.IsAsciiDigit(character))
            {
                Append(character);
                digitCount++;
                continue;
            }

            if (character == '+' && length == 0)
            {
                Append(character);
                continue;
            }

            if (character is '*' or '#')
            {
                Append(character);
                continue;
            }

            if (character is ' ' or '-' or '(' or ')' or '.')
            {
                continue;
            }

            throw new ArgumentException("The phone number contains an unsupported character.", nameof(value));
        }

        if (digitCount < 3)
        {
            throw new ArgumentException("Enter at least three digits.", nameof(value));
        }

        return new string(buffer, 0, length);

        void Append(char character)
        {
            if (length == buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "The normalized phone number is too long.");
            }
            buffer[length++] = character;
        }
    }
}
