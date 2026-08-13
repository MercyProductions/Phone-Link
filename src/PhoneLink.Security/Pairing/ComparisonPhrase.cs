namespace PhoneLink.Security.Pairing;

public static class ComparisonPhrase
{
    private static readonly string[] Prefixes =
        "amber aqua arctic autumn bold bright calm cedar coral cosmic crisp dawn deep ember fern frost gentle golden harbor hazel indigo ivory jade lunar maple misty navy olive quiet rapid silver vivid"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static readonly string[] Nouns =
        "acorn anchor apple arrow atlas badger beacon birch bison blossom bridge brook cabin canyon castle cloud comet crane creek crown delta dune eagle elm falcon field finch flame forest fox garden glacier grove hawk hill island lake lantern leaf lion meadow mesa moon oak ocean orbit owl pebble pine planet quartz raven reef river robin sail shore sky sparrow star stone summit sun tiger"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static string Create(ReadOnlySpan<byte> comparisonDigest)
    {
        if (comparisonDigest.Length < 9)
        {
            throw new ArgumentException("A comparison digest must contain at least 66 bits.", nameof(comparisonDigest));
        }

        if (Prefixes.Length != 32 || Nouns.Length != 64)
        {
            throw new InvalidOperationException("The comparison code-word table must contain exactly 2,048 entries.");
        }

        var words = new string[6];
        for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
        {
            var tableIndex = ReadElevenBits(comparisonDigest, wordIndex * 11);
            words[wordIndex] = $"{Prefixes[tableIndex >> 6]}-{Nouns[tableIndex & 0x3F]}";
        }

        return string.Join(' ', words);
    }

    private static int ReadElevenBits(ReadOnlySpan<byte> value, int startBit)
    {
        var result = 0;
        for (var offset = 0; offset < 11; offset++)
        {
            var bit = startBit + offset;
            var bitValue = (value[bit / 8] >> (7 - (bit % 8))) & 1;
            result = (result << 1) | bitValue;
        }

        return result;
    }
}
