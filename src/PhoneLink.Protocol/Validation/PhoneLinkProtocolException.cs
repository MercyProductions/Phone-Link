namespace PhoneLink.Protocol.Validation;

public sealed class PhoneLinkProtocolException : Exception
{
    public PhoneLinkProtocolException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public PhoneLinkProtocolException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

