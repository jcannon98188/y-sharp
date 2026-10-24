using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using YSharp.Fuzz.Corpus;
using YSharp.TestKit.Traceability;

namespace YSharp.Fuzz.Tests;

/// <summary>
/// The fixed corpus of <c>fixtures/hostile</c> (ADR 0013 Corpus, Regression): every file is replayed on every commit through the
/// guarded harness, so adding a file is the whole regression test. One test per class names all its failing cases.
/// </summary>
public sealed class CorpusReplayTests
{
    private static readonly string[] Classes =
    {
        "HOS-TRUNC", "HOS-MUT", "HOS-LEN", "HOS-DEEP", "HOS-CLOCK", "HOS-REF", "HOS-SEM", "HOS-AMP", "HOS-PROTO",
    };

    // HOS-DEEP has no stored bytes: 100k-deep Any values and 1M nested types are generated (see DeepInputTests and the canary).
    private static readonly string[] ClassesWithStoredCases = Classes.Where(c => c != "HOS-DEEP").ToArray();

    private static IEnumerable<CorpusCase> CasesOf(string hosClass) =>
        CorpusFiles.LoadAll().Where(c => c.Classes.Contains(hosClass));

    private static void Replay(string hosClass) => Stub.Run(() => CorpusRunner.ReplayAll(CasesOf(hosClass)));

    [Fact, Covers("HOS-TRUNC", Layer = CoverLayer.Unit)]
    public void Corpus_Truncation_EveryStoredPrefixIsMalformedAndChangesNothing() => Replay("HOS-TRUNC");

    [Fact, Covers("HOS-MUT", Layer = CoverLayer.Unit)]
    public void Corpus_Mutations_NoSingleEditCrashesHangsOverAllocatesOrFaultsTheDocument() => Replay("HOS-MUT");

    [Fact, Covers("HOS-LEN", "ERR-09", Layer = CoverLayer.Unit)]
    public void Corpus_OversizedCountsAndLengths_AreRejectedWithinTheAllocationBound() => Replay("HOS-LEN");

    [Fact, Covers("HOS-CLOCK", Layer = CoverLayer.Unit)]
    public void Corpus_Clocks_GapsOverlapsAndOverflowsAreContained() => Replay("HOS-CLOCK");

    [Fact, Covers("HOS-REF", "ERR-06", "ERR-07", Layer = CoverLayer.Unit)]
    public void Corpus_References_MissingCyclicAndNonTypeParentsNeverSpin() => Replay("HOS-REF");

    [Fact, Covers("HOS-SEM", "ERR-04", Layer = CoverLayer.Unit)]
    public void Corpus_MeaninglessButWellFormedInput_IsRejectedOrIgnored() => Replay("HOS-SEM");

    [Fact, Covers("HOS-AMP", Layer = CoverLayer.Unit)]
    public void Corpus_Amplification_IsBoundedInTimeAndAllocation() => Replay("HOS-AMP");

    [Fact, Covers("HOS-PROTO", Layer = CoverLayer.Unit)]
    public void Corpus_ProtocolFrames_UnknownTypesOverrunsAndBadStatesAreContained() => Replay("HOS-PROTO");

    [Fact, Covers("ERR-21", Layer = CoverLayer.Unit)]
    public void Corpus_EveryStoredFile_LeavesTheDocumentUnfaultedAndUsable() => Stub.Run(() =>
        CorpusRunner.ReplayAll(CorpusFiles.LoadAll()));

    [Fact]
    public void Corpus_EveryHostileClassWithStoredBytesHasAFile()
    {
        var all = CorpusFiles.LoadAll();
        foreach (var hosClass in ClassesWithStoredCases)
            Assert.True(all.Count(c => c.Classes.Contains(hosClass)) >= 6, $"{hosClass} needs at least 6 stored cases");
    }

    [Fact]
    public void Corpus_Files_AreWellFormed()
    {
        var all = CorpusFiles.LoadAll();
        var valid = new HashSet<string> { "ok", "Malformed", "LimitExceeded", "Rejected", "UnknownMessage" };
        foreach (var file in all.GroupBy(c => c.File))
        {
            Assert.Equal(file.Count(), file.Select(c => c.Name).Distinct().Count());
            foreach (var c in file)
            {
                Assert.NotEmpty(c.Expect);
                Assert.All(c.Expect, e => Assert.Contains(e, valid));
                Assert.Contains(c.Api, new[] { "update", "sv", "sync", "awareness" });
            }
        }
        // ids of the corpus classes are the ones the registry knows (ADR 0010); a typo would otherwise hide a file from its class test
        Assert.All(all.SelectMany(c => c.Classes).Distinct(), id => Assert.Contains(id, Classes));
    }

    [Fact]
    public void Corpus_FilesAreAllHostileFixturesOrKnownLegacyOnes()
    {
        // the folder holds the Spec 5 malformed files (ENC-*) and the Spec 11 class files; anything else needs a deliberate decision
        var names = CorpusFiles.Files().Select(Path.GetFileName).ToHashSet();
        var legacy = new[] { "malformed-codec-extras.json", "malformed-v1-updates.json", "malformed-v2-updates.json" };
        foreach (var l in legacy) Assert.Contains(l, names);
        foreach (var n in names.Where(n => !legacy.Contains(n))) Assert.StartsWith("hos-", n);
    }

    [Fact]
    public void Corpus_OracleOutcomesAreRecordedForEveryUpdateCase()
    {
        // ADR 0011 / 0013: the stored oracle outcome table is what the smoke and nightly comparison reads; update cases must carry one
        foreach (var c in CorpusFiles.LoadAll().Where(c => c.Api == "update"))
            Assert.Contains(c.OracleOutcome, new[] { "ok", "throws", "js-crash" });
    }
}
