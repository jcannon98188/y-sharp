using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace YSharp.TestKit.Sim;

/// <summary>The kind of a simulated sync message (the y-protocols message types, ADR 0017).</summary>
public enum SimMessageKind { Step1 = 0, Step2 = 1, Update = 2 }

/// <summary>
/// One queued message. <see cref="UpdateIndex"/> is the position of the update in the sender's update list (updates only);
/// <see cref="Name"/> is the delivery-schedule name of the captured state vector (step 1) or reply (step 2); it is null when
/// nothing records the run.
/// </summary>
public sealed record SimMessage(byte[] Frame, SimMessageKind Kind, int UpdateIndex = -1, string? Name = null);

/// <summary>
/// Observes a simulation run to produce its op log: the edits every client made and the delivery schedule (ADR 0011, 0016
/// <c>deliver</c>). The connector calls the schedule members at exactly the points the Node generator
/// (<c>tools/fixture-gen/lib/sim.mjs</c>) emits its entries, so both logs can be compared entry by entry.
/// </summary>
public interface ISimRecorder
{
    /// <summary>Called once, right after the coin flip of <c>init</c> chose the encoding of the run (SIM-04).</summary>
    void Begin(UpdateFormat format);

    /// <summary>A client made an edit: one ops step (stored form) naming its replica in <c>doc</c>.</summary>
    void Edit(JsonObject step);

    /// <summary>A SyncStep1 message was created for <paramref name="user"/>; returns the name of its captured state vector.</summary>
    string StateVector(int user);

    /// <summary><paramref name="user"/> answered the step 1 captured as <paramref name="svName"/>; returns the name of the reply.</summary>
    string Reply(int user, string svName);

    /// <summary><paramref name="user"/> applied the captured reply <paramref name="replyName"/>.</summary>
    void ApplyReply(int user, string replyName);

    /// <summary><paramref name="user"/> applied the update at <paramref name="index"/> of <paramref name="from"/>'s update list.</summary>
    void ApplyUpdate(int user, int from, int index);
}

/// <summary>
/// The recorder behind the draw-parity test and the repro files of failing seeds: builds the same script the Node generator
/// stores in <c>fixtures/sim/*.json</c> (replicas <c>a</c>, <c>b</c>, ... with client ids <c>0..n-1</c>; adjacent deliveries
/// merged into one <c>deliver</c> step).
/// </summary>
public sealed class SimLog : ISimRecorder
{
    private readonly int _users;
    private readonly List<JsonNode> _steps = new();
    private JsonArray? _schedule;
    private int _svSeq;
    private int _replySeq;

    public SimLog(int users = 5) => _users = users;

    public UpdateFormat Format { get; private set; } = UpdateFormat.V1;

    public static string Name(int user) => ((char)('a' + user)).ToString();

    public void Begin(UpdateFormat format) => Format = format;

    public void Edit(JsonObject step)
    {
        FlushSchedule();
        _steps.Add(step);
    }

    public string StateVector(int user)
    {
        var name = "s" + _svSeq++;
        Add(new JsonObject { ["do"] = "sv", ["doc"] = Name(user), ["name"] = name });
        return name;
    }

    public string Reply(int user, string svName)
    {
        var name = "r" + _replySeq++;
        Add(new JsonObject { ["do"] = "reply", ["doc"] = Name(user), ["sv"] = svName, ["name"] = name });
        return name;
    }

    public void ApplyReply(int user, string replyName) =>
        Add(new JsonObject { ["do"] = "apply", ["doc"] = Name(user), ["update"] = replyName });

    public void ApplyUpdate(int user, int from, int index) =>
        Add(new JsonObject
        {
            ["do"] = "apply",
            ["doc"] = Name(user),
            ["update"] = new JsonObject { ["from"] = Name(from), ["index"] = index },
        });

    private void Add(JsonObject entry) => (_schedule ??= new JsonArray()).Add(entry);

    private void FlushSchedule()
    {
        if (_schedule == null) return;
        var step = new JsonObject { ["step"] = "deliver", ["schedule"] = _schedule };
        if (Format == UpdateFormat.V2) step["encoding"] = "v2";
        _steps.Add(step);
        _schedule = null;
    }

    /// <summary>The op log so far as a <c>schema: 1</c> script (replicas, then steps).</summary>
    public JsonObject ToScript()
    {
        FlushSchedule();
        var replicas = new JsonArray();
        for (var i = 0; i < _users; i++) replicas.Add(new JsonObject { ["name"] = Name(i), ["clientId"] = i });
        var steps = new JsonArray();
        foreach (var s in _steps) steps.Add(s.DeepClone());
        return new JsonObject { ["schema"] = 1, ["replicas"] = replicas, ["steps"] = steps };
    }
}
