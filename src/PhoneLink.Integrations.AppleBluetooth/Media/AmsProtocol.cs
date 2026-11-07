using System.Globalization;
using System.Text;

namespace PhoneLink.Integrations.AppleBluetooth.Media;

public enum AmsRemoteCommand : byte
{
    Play, Pause, TogglePlayPause, NextTrack, PreviousTrack, VolumeUp, VolumeDown,
    AdvanceRepeatMode, AdvanceShuffleMode, SkipForward, SkipBackward, LikeTrack, DislikeTrack, BookmarkTrack,
}
public enum AmsEntity : byte { Player, Queue, Track }
public sealed record AmsAttributeObservation(AmsEntity Entity, byte Attribute, byte Flags, string Value,
    DateTimeOffset ReceivedAt)
{
    public bool IsTruncated => (Flags & 1) != 0;
    public bool HasUnknownFlags => (Flags & ~1) != 0;
    public bool IsComplete => !IsTruncated && !HasUnknownFlags;
}
public sealed record AmsPlaybackInfo(int State, double Rate, double ElapsedSeconds);

public static class AmsProtocol
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static IReadOnlyList<AmsRemoteCommand> ParseSupportedCommands(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 256) throw new InvalidDataException("The AMS command list exceeded its limit.");
        return payload.ToArray().Where(value => value <= 13).Select(value => (AmsRemoteCommand)value).Distinct().Order().ToArray();
    }

    public static byte[] CreateCommand(AmsRemoteCommand command) => (byte)command <= 13
        ? [(byte)command] : throw new ArgumentOutOfRangeException(nameof(command));

    public static byte[] CreateSubscription(AmsEntity entity)
    {
        return entity switch
        {
            AmsEntity.Player => [0, 0, 1, 2],
            AmsEntity.Queue => [1, 0, 1, 2, 3],
            AmsEntity.Track => [2, 0, 1, 2, 3],
            _ => throw new ArgumentOutOfRangeException(nameof(entity)),
        };
    }

    public static AmsAttributeObservation ParseUpdate(ReadOnlySpan<byte> payload, DateTimeOffset receivedAt)
    {
        if (payload.Length is < 3 or > 4_099 || payload[0] > 2 ||
            payload[1] > (payload[0] == 0 ? 2 : 3)) throw new InvalidDataException("The AMS entity update is unsupported or malformed.");
        string value;
        try { value = StrictUtf8.GetString(payload[3..]); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The AMS attribute is not valid UTF-8."); }
        return new((AmsEntity)payload[0], payload[1], payload[2], value, receivedAt);
    }

    internal static string ParseExtendedValue(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > 4_096) throw new InvalidDataException("The extended AMS value exceeded its limit.");
        try { return StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException) { throw new InvalidDataException("The extended AMS value is not valid UTF-8."); }
    }

    public static double? ParseVolume(AmsAttributeObservation observation) =>
        observation is { Entity: AmsEntity.Player, Attribute: 2, IsComplete: true } &&
        TryNumber(observation.Value, out var value) && value is >= 0 and <= 1 ? value : null;

    public static AmsPlaybackInfo? ParsePlayback(AmsAttributeObservation observation)
    {
        if (observation is not { Entity: AmsEntity.Player, Attribute: 1, IsComplete: true }) return null;
        var values = observation.Value.Split(',');
        return values.Length == 3 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var state) && state is >= 0 and <= 3 &&
            TryNumber(values[1], out var rate) && TryNumber(values[2], out var seconds) && seconds >= 0
            ? new(state, rate, seconds) : null;
    }

    private static bool TryNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
