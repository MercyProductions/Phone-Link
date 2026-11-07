namespace PhoneLink.Integrations.AppleBluetooth.Media;

public sealed record AmsSnapshot(bool Ended, bool CommandListReceived, long ContextRevision,
    IReadOnlyList<AmsRemoteCommand> Commands, IReadOnlyList<AmsAttributeObservation> Attributes);

public sealed class AmsCommandRequest
{
    internal AmsCommandRequest(object owner, long revision, AmsRemoteCommand command)
    { Owner = owner; ContextRevision = revision; Command = command; }
    internal object Owner { get; }
    public long ContextRevision { get; }
    public AmsRemoteCommand Command { get; }
    public override string ToString() => $"AMS command request: {Command}";
}

public sealed class AmsSessionState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(AmsEntity, byte), AmsAttributeObservation> _attributes = [];
    private AmsRemoteCommand[] _commands = [];
    private bool _received, _ended;
    private long _revision;

    public void UpdateCommands(ReadOnlySpan<byte> payload)
    {
        var commands = AmsProtocol.ParseSupportedCommands(payload).ToArray();
        lock (_gate)
        {
            if (_ended) return;
            if (!_received || !_commands.SequenceEqual(commands)) _revision++;
            _commands = commands;
            _received = true;
        }
    }

    public void Update(AmsAttributeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if ((byte)observation.Entity > 2 || observation.Attribute > (observation.Entity == AmsEntity.Player ? 2 : 3) ||
            observation.Value.Length > 4_096) throw new ArgumentException("Unsupported AMS attribute observation.", nameof(observation));
        lock (_gate)
        {
            if (_ended) return;
            var key = (observation.Entity, observation.Attribute);
            var contextField = observation.Entity == AmsEntity.Track || key is (AmsEntity.Player, 0) or (AmsEntity.Queue, 0);
            if (contextField && (!_attributes.TryGetValue(key, out var previous) || previous.Value != observation.Value || previous.Flags != observation.Flags)) _revision++;
            _attributes[key] = observation;
        }
    }

    public AmsCommandRequest CreateRequest(AmsRemoteCommand command)
    {
        lock (_gate)
        {
            if (_ended || !_received || !_commands.Contains(command)) throw new InvalidOperationException("This media command is not currently advertised by the selected session.");
            return new(this, _revision, command);
        }
    }

    public bool IsCurrent(AmsCommandRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate) return !_ended && _received && ReferenceEquals(request.Owner, this) && request.ContextRevision == _revision && _commands.Contains(request.Command);
    }

    public AmsSnapshot Capture()
    {
        lock (_gate) return new(_ended, _received, _revision, Array.AsReadOnly(_commands.ToArray()),
            Array.AsReadOnly(_attributes.Values.OrderBy(value => value.Entity).ThenBy(value => value.Attribute).ToArray()));
    }

    internal bool IsExpandable(AmsAttributeObservation observation, long revision)
    {
        lock (_gate) return IsExpandableCore(observation, revision);
    }

    internal bool TryExpand(AmsAttributeObservation observation, long revision, string expanded, DateTimeOffset receivedAt)
    {
        lock (_gate)
        {
            if (!IsExpandableCore(observation, revision) || expanded.Length > 4_096 ||
                !expanded.StartsWith(observation.Value, StringComparison.Ordinal)) return false;
            _attributes[(observation.Entity, observation.Attribute)] = observation with { Flags = 0, Value = expanded, ReceivedAt = receivedAt };
            _revision++;
            return true;
        }
    }

    private bool IsExpandableCore(AmsAttributeObservation observation, long revision) =>
        !_ended && _revision == revision && observation.IsTruncated && !observation.HasUnknownFlags &&
        _attributes.TryGetValue((observation.Entity, observation.Attribute), out var current) && ReferenceEquals(current, observation);

    public void End()
    {
        lock (_gate) { _ended = true; _commands = []; _attributes.Clear(); _received = false; _revision++; }
    }

    public void Invalidate()
    {
        lock (_gate) { _commands = []; _attributes.Clear(); _received = false; _revision++; }
    }
}
