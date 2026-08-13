using System.Buffers.Binary;
using System.Text;

namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public static class AncsProtocolParser
{
    public static readonly byte[] RequestedAttributeIds = [0, 1, 2, 3, 5, 6, 7];

    public static AncsNotificationEvent ParseNotificationSource(ReadOnlySpan<byte> value)
    {
        if (value.Length != 8 || value[0] > (byte)AncsEventKind.Removed)
        {
            throw new InvalidDataException("The ANCS Notification Source value is malformed.");
        }

        return new AncsNotificationEvent(
            (AncsEventKind)value[0],
            value[1],
            (AncsCategory)value[2],
            value[3],
            BinaryPrimitives.ReadUInt32LittleEndian(value[4..8]));
    }

    public static byte[] CreateGetNotificationAttributesCommand(uint notificationUid)
    {
        var command = new List<byte>(32) { 0 };
        Span<byte> uid = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(uid, notificationUid);
        command.AddRange(uid.ToArray());
        command.Add(0);
        AddLimitedAttribute(command, 1, 256);
        AddLimitedAttribute(command, 2, 256);
        AddLimitedAttribute(command, 3, 1_024);
        command.Add(5);
        command.Add(6);
        command.Add(7);
        return command.ToArray();
    }

    public static bool TryParseNotificationAttributes(
        ReadOnlySpan<byte> value,
        out uint notificationUid,
        out IReadOnlyDictionary<byte, string>? attributes,
        out int bytesConsumed)
    {
        notificationUid = 0;
        attributes = null;
        bytesConsumed = 0;
        if (value.Length < 5)
        {
            return false;
        }
        if (value[0] != 0)
        {
            throw new InvalidDataException("The ANCS Data Source command identifier is unsupported.");
        }

        notificationUid = BinaryPrimitives.ReadUInt32LittleEndian(value[1..5]);
        var parsed = new Dictionary<byte, string>();
        var offset = 5;
        foreach (var expectedAttributeId in RequestedAttributeIds)
        {
            if (value.Length - offset < 3)
            {
                return false;
            }
            var attributeId = value[offset];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(value[(offset + 1)..(offset + 3)]);
            if (attributeId != expectedAttributeId || length > 4_096)
            {
                throw new InvalidDataException("The ANCS attribute response is malformed.");
            }
            offset += 3;
            if (value.Length - offset < length)
            {
                return false;
            }
            parsed[attributeId] = Encoding.UTF8.GetString(value.Slice(offset, length));
            offset += length;
        }

        attributes = parsed;
        bytesConsumed = offset;
        return true;
    }

    private static void AddLimitedAttribute(List<byte> command, byte attributeId, ushort maxLength)
    {
        command.Add(attributeId);
        command.Add((byte)(maxLength & 0xff));
        command.Add((byte)(maxLength >> 8));
    }
}
