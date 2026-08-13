namespace PhoneLink.Domain.Devices;

public readonly record struct DeviceId
{
    public DeviceId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A device ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static DeviceId New() => new(Guid.NewGuid());

    public static DeviceId Parse(string value) => new(Guid.Parse(value));

    public static bool TryParse(string? value, out DeviceId deviceId)
    {
        if (Guid.TryParse(value, out var parsed) && parsed != Guid.Empty)
        {
            deviceId = new DeviceId(parsed);
            return true;
        }

        deviceId = default;
        return false;
    }

    public override string ToString() => Value.ToString("D");
}

