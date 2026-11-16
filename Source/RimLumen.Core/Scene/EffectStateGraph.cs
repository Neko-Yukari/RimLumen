using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using RimLumen.Core.Contracts;

namespace RimLumen.Core.Scene;

/// <summary>
/// The validated, index-resolved state machine of one <see cref="EffectAsset"/>, compiled once per asset instance:
/// the asset loaders compile it at load time to reject bad authoring, and <see cref="EffectInstanceHandle"/> reuses
/// it on Play, where a C#-registered asset is compiled on its first Play and fails just as loudly.
/// </summary>
internal sealed class EffectStateGraph
{
    private static readonly ConditionalWeakTable<EffectAsset, EffectStateGraph> Compiled = new();

    internal sealed record State
    {
        // Null for the implicit state of an asset without States; it raises no StateChanged.
        public required string? Name { get; init; }
        public required bool[] NodeMask { get; init; }
        public required double Duration { get; init; }
        public required bool Loop { get; init; }
        // Index of the state entered when this one ends; -1 = the effect finishes.
        public required int Next { get; init; }
        // The timeline events bound to this state, ordered by time, ties in declaration order.
        public required TimelineEventSpec[] Events { get; init; }
    }

    private EffectStateGraph(State[] states, int initialIndex, int stopIndex)
    {
        States = states;
        InitialIndex = initialIndex;
        StopIndex = stopIndex;
    }

    public State[] States { get; }
    public int InitialIndex { get; }

    // -1 when the asset declares no StopState.
    public int StopIndex { get; }

    // Ensures: the index of the declared state called name, or -1 (always -1 for the implicit state).
    public int IndexOf(string name)
    {
        for (int i = 0; i < States.Length; i++)
            if (string.Equals(States[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    // Requires: asset.Nodes is non-null; asset is not mutated after its first call.
    // Ensures: the graph Compile builds for this asset instance, compiled on the first call and reused after.
    //          A failed compile throws its InvalidDataException naming source and caches nothing.
    public static EffectStateGraph For(EffectAsset asset, string source)
    {
        if (Compiled.TryGetValue(asset, out EffectStateGraph? graph)) return graph;
        graph = Compile(asset, source);
        Compiled.AddOrUpdate(asset, graph);
        return graph;
    }

    // Requires: alias carries asset's Nodes, States, TimelineEvents, DefaultDuration, InitialState and StopState
    //           unchanged (only its EffectId differs).
    // Ensures: For(alias) returns asset's graph without compiling it again.
    public static void ShareWithAlias(EffectAsset asset, EffectAsset alias) =>
        Compiled.AddOrUpdate(alias, For(asset, $"effect '{asset.EffectId}'"));

    // Requires: asset.Nodes is non-null.
    // Ensures: throws InvalidDataException naming source when
    //          - DefaultDuration is negative or non-finite;
    //          - States is empty but InitialState, StopState or an event's State is set;
    //          - a state is null, has an empty or duplicate name, lists a null node array or a node name that no
    //            node carries, has a negative or non-finite duration (its own or the defaultDuration it inherits),
    //            is Loop with a Next, or names a Next that does not exist;
    //          - InitialState or StopState names no state, or StopState is Loop or has a Next;
    //          - a chain of zero-duration states returns to itself through Next;
    //          - an event's State (default: the asset's initial state) names no state.
    //          A node may belong to any number of states, including none.
    private static EffectStateGraph Compile(EffectAsset asset, string source)
    {
        if (!float.IsFinite(asset.DefaultDuration) || asset.DefaultDuration < 0f)
            throw new InvalidDataException($"{source}: defaultDuration must be finite and >= 0, got {asset.DefaultDuration}.");
        EffectNodeSpec[] nodes = asset.Nodes;
        EffectStateSpec[] specs = asset.States ?? Array.Empty<EffectStateSpec>();
        TimelineEventSpec[] events = asset.TimelineEvents ?? Array.Empty<TimelineEventSpec>();
        if (specs.Length == 0)
            return CompileImplicit(asset, nodes, events, source);

        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < specs.Length; i++)
        {
            EffectStateSpec spec = specs[i] ?? throw new InvalidDataException($"{source}: states[{i}] is null.");
            if (string.IsNullOrEmpty(spec.Name))
                throw new InvalidDataException($"{source}: states[{i}]: name must not be null or empty.");
            if (!names.TryAdd(spec.Name, i))
                throw new InvalidDataException($"{source}: states[{i}]: duplicate state name '{spec.Name}'.");
        }

        var states = new State[specs.Length];
        var eventLists = new List<TimelineEventSpec>[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            EffectStateSpec spec = specs[i];
            string path = $"{source}: state '{spec.Name}'";
            var mask = new bool[nodes.Length];
            string[] members = spec.Nodes ?? throw new InvalidDataException($"{path}: nodes must not be null.");
            foreach (string member in members)
            {
                bool found = false;
                for (int n = 0; n < nodes.Length; n++)
                {
                    if (!string.Equals(nodes[n].Name, member, StringComparison.Ordinal)) continue;
                    mask[n] = true;
                    found = true;
                }
                if (!found) throw new InvalidDataException($"{path}: no node is named '{member}'.");
            }

            float duration = spec.Duration ?? asset.DefaultDuration;
            if (!float.IsFinite(duration) || duration < 0f)
                throw new InvalidDataException($"{path}: duration must be finite and >= 0, got {duration}.");
            if (spec.Loop && spec.Next is not null)
                throw new InvalidDataException($"{path}: a loop state never ends on its own, so it cannot declare next ('{spec.Next}').");
            int next = -1;
            if (spec.Next is not null && !names.TryGetValue(spec.Next, out next))
                throw new InvalidDataException($"{path}: next names unknown state '{spec.Next}'.");
            eventLists[i] = new List<TimelineEventSpec>();
            states[i] = new State
            {
                Name = spec.Name, NodeMask = mask, Duration = duration, Loop = spec.Loop, Next = next,
                Events = Array.Empty<TimelineEventSpec>(),
            };
        }

        int initial = 0;
        if (asset.InitialState is not null && !names.TryGetValue(asset.InitialState, out initial))
            throw new InvalidDataException($"{source}: initialState names unknown state '{asset.InitialState}'.");
        int stop = -1;
        if (asset.StopState is not null)
        {
            if (!names.TryGetValue(asset.StopState, out stop))
                throw new InvalidDataException($"{source}: stopState names unknown state '{asset.StopState}'.");
            if (states[stop].Loop || states[stop].Next >= 0)
                throw new InvalidDataException($"{source}: stopState '{asset.StopState}' must end on its own and finish the effect: it cannot be a loop state or declare next.");
        }

        for (int i = 0; i < states.Length; i++)
        {
            var visited = new HashSet<int>();
            for (int at = i; at >= 0 && !states[at].Loop && states[at].Duration == 0d; at = states[at].Next)
            {
                if (!visited.Add(at))
                    throw new InvalidDataException($"{source}: state '{states[i].Name}' starts a cycle of zero-duration states through next.");
            }
        }

        for (int i = 0; i < events.Length; i++)
        {
            TimelineEventSpec timelineEvent = events[i];
            int owner = initial;
            if (timelineEvent.State is not null && !names.TryGetValue(timelineEvent.State, out owner))
                throw new InvalidDataException($"{source}: timelineEvents[{i}] ('{timelineEvent.EventName}'): state names unknown state '{timelineEvent.State}'.");
            eventLists[owner].Add(timelineEvent);
        }

        for (int i = 0; i < states.Length; i++)
            states[i] = states[i] with { Events = SortByTime(eventLists[i]) };
        return new EffectStateGraph(states, initial, stop);
    }

    private static EffectStateGraph CompileImplicit(EffectAsset asset, EffectNodeSpec[] nodes, TimelineEventSpec[] events, string source)
    {
        if (asset.InitialState is not null || asset.StopState is not null)
            throw new InvalidDataException($"{source}: initialState and stopState require a states list.");
        foreach (TimelineEventSpec timelineEvent in events)
            if (timelineEvent.State is not null)
                throw new InvalidDataException($"{source}: timeline event '{timelineEvent.EventName}' names state '{timelineEvent.State}', but the effect declares no states.");
        var mask = new bool[nodes.Length];
        Array.Fill(mask, true);
        bool loop = false;
        foreach (EffectNodeSpec node in nodes)
            loop |= node.Emitter is { Looping: true };
        var state = new State
        {
            Name = null, NodeMask = mask, Duration = asset.DefaultDuration, Loop = loop, Next = -1,
            Events = SortByTime(new List<TimelineEventSpec>(events)),
        };
        return new EffectStateGraph(new[] { state }, 0, -1);
    }

    private static TimelineEventSpec[] SortByTime(List<TimelineEventSpec> events)
    {
        var ordered = new TimelineEventSpec[events.Count];
        var keys = new (float Time, int Index)[events.Count];
        for (int i = 0; i < keys.Length; i++) keys[i] = (events[i].TimeSeconds, i);
        Array.Sort(keys);
        for (int i = 0; i < keys.Length; i++) ordered[i] = events[keys[i].Index];
        return ordered;
    }
}
