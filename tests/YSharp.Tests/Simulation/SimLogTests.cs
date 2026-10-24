using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
using YSharp.TestKit.Sim;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Simulation;

/// <summary>The recorder that turns a run into the op log stored in the golden fixtures (no documents involved).</summary>
public sealed class SimLogTests
{
    [Fact]
    [Covers("SIM-10")]
    public void Log_NamesCapturesInOrder_AndMergesDeliveriesBetweenEdits()
    {
        var log = new SimLog(3);
        log.Begin(UpdateFormat.V1);
        Assert.Equal("s0", log.StateVector(0));
        Assert.Equal("s1", log.StateVector(1));
        Assert.Equal("r0", log.Reply(1, "s0"));
        log.ApplyReply(0, "r0");
        log.Edit(new JsonObject { ["step"] = "map.delete", ["doc"] = "b" });
        log.ApplyUpdate(2, 1, 0);

        var script = log.ToScript();
        Assert.Equal(3, script["replicas"]!.AsArray().Count);
        Assert.Equal("c", script["replicas"]![2]!["name"]!.GetValue<string>());
        Assert.Equal(2, script["replicas"]![2]!["clientId"]!.GetValue<int>());
        var steps = script["steps"]!.AsArray();
        Assert.Equal(new[] { "deliver", "map.delete", "deliver" }, steps.Select(s => s!["step"]!.GetValue<string>()));
        Assert.Equal(4, steps[0]!["schedule"]!.AsArray().Count);
        Assert.Equal(
            "{\"do\":\"apply\",\"doc\":\"c\",\"update\":{\"from\":\"b\",\"index\":0}}",
            steps[2]!["schedule"]![0]!.ToJsonString());
        Assert.Null(steps[0]!["encoding"]);
    }

    [Fact]
    [Covers("SIM-10")]
    public void RecordingTheSameEventsTwice_GivesTheSameScript()
    {
        static string Record()
        {
            var log = new SimLog(2);
            log.Begin(UpdateFormat.V1);
            log.StateVector(1);
            var first = log.ToScript().ToJsonString();
            log.Edit(new JsonObject { ["step"] = "tick", ["ms"] = 1 });
            return first + log.ToScript().ToJsonString();
        }
        Assert.Equal(Record(), Record());
    }
}
