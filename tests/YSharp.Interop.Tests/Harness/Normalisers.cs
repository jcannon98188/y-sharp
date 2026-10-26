using System;
using System.Collections.Generic;
using System.Linq;

namespace YSharp.Interop;

/// <summary>How a mutation changed a valid update.</summary>
public enum MutationKind { Truncate, FlipByte, DeleteByte, InsertByte }

/// <summary>
/// One mutated-update case after both implementations ran it: the bytes, what the mutation was, and whether each side accepted them.
/// Acceptance of a named <c>applyForeign</c> step is the observable (ADR 0012 "Compared": the outcome class of a mutated valid update).
/// </summary>
public sealed record MutationCase(UpdateFormat Format, byte[] Valid, byte[] Mutated, MutationKind Kind, int Offset, bool OracleAccepted, bool SharpAccepted, bool SharpUnchanged)
{
    /// <summary>The oracle accepted only because a lib0 reader returned its default past the end of a buffer (the step's <c>readPastEnd</c> result).</summary>
    public bool OracleReadPastEnd { get; init; }

    /// <summary>The oracle rejected only because it re-encoded a zero-length delete range (the step's <c>zeroLengthDeleteRange</c> result).</summary>
    public bool OracleZeroLengthDeleteRange { get; init; }

    /// <summary>The oracle parked out-of-order delete ranges as pending state (the step's <c>unsortedPendingDeleteSet</c> result).</summary>
    public bool OracleUnsortedPendingDeleteSet { get; init; }

    /// <summary>The oracle parked a struct that depends on its own id or a later clock of its client (the step's <c>selfDependentPending</c> result).</summary>
    public bool OracleSelfDependentPending { get; init; }
}

/// <summary>The classes a mutated valid update falls into (ADR 0012).</summary>
public enum OutcomeClass
{
    /// <summary>Both accept; the state must then be equal, unless a normaliser claims a recorded state divergence (ENC-86).</summary>
    BothAccept,

    /// <summary>Both reject; y-sharp must also be unchanged (atomic decode, ADR 0005).</summary>
    BothReject,

    /// <summary>The oracle accepts, y-sharp rejects: only legal as a recorded divergence claimed by a normaliser.</summary>
    SharpRejectsOracleAccepts,

    /// <summary>The oracle rejects, y-sharp accepts: a bug unless a normaliser claims a recorded lenient divergence (ENC-51, ADR 0003).</summary>
    OracleRejectsSharpAccepts,
}

/// <summary>
/// A per-divergence normaliser (ADR 0012 "Generation"): it names the registry IDs of the recorded divergence it covers and decides whether a
/// case is exactly the documented difference. The divergence-targeting generator applies it and asserts the difference exactly; the default
/// generator never reaches it, so any mismatch outside is a bug. Adding a divergence to the differential is: register an entry in the
/// inventory, add a normaliser here, add a targeting generator case.
/// </summary>
public interface IDivergenceNormaliser
{
    /// <summary>Stable key of the normaliser.</summary>
    string Key { get; }

    /// <summary>The recorded divergences (registry IDs, ADR 0003/0005) this normaliser stands for.</summary>
    IReadOnlyList<string> RegistryIds { get; }

    string Description { get; }

    /// <summary>True when <paramref name="c"/> is exactly the documented difference (not merely "some difference").</summary>
    bool Claims(MutationCase c);
}

/// <summary>JS decodes past the end of a truncated update where y-sharp reports a decode error (EOF is always an error, ADR 0005).</summary>
public sealed class LenientTruncationNormaliser : IDivergenceNormaliser
{
    public string Key => "lenient-truncation";

    public IReadOnlyList<string> RegistryIds { get; } = new[] { "ENC-60", "ENC-62", "ENC-64", "ENC-65", "ENC-67", "ENC-68" };

    public string Description =>
        "A proper prefix of a valid update that the oracle's lenient readers accept (readUint8/readVarInt/Rle/UintOptRle/IntDiffOptRle/StringDecoder return a default at EOF) is rejected by y-sharp without changing the document.";

    public bool Claims(MutationCase c) =>
        c.Kind == MutationKind.Truncate && c.Mutated.Length < c.Valid.Length && c.OracleAccepted && !c.SharpAccepted && c.SharpUnchanged;
}

/// <summary>
/// The same EOF divergence reached by a mutation that is not a truncation: a flipped, deleted or inserted byte shortens a V2 column or the
/// rest buffer, and the oracle reads its default past the end where y-sharp reports a decode error (ADR 0005). Claimed only when the
/// oracle itself reports that it read past the end of a buffer, so a mutation the oracle decodes in bounds is never excused.
/// </summary>
public sealed class LenientColumnEndNormaliser : IDivergenceNormaliser
{
    public string Key => "lenient-column-end";

    public IReadOnlyList<string> RegistryIds { get; } = new[] { "ENC-60", "ENC-62", "ENC-64", "ENC-65", "ENC-67", "ENC-68" };

    public string Description =>
        "A mutated valid update that the oracle accepts only by reading a default past the end of a column or of the rest buffer is rejected by y-sharp without changing the document.";

    public bool Claims(MutationCase c) => c.OracleReadPastEnd && c.OracleAccepted && !c.SharpAccepted && c.SharpUnchanged;
}

/// <summary>
/// y-sharp ignores a zero-length delete range (ENC-51, ratified in TASK-4.14, ADR 0003); Yjs throws <c>Unexpected case</c> when the range
/// belongs to a client the document does not know yet, because it parks the range and re-encodes it as V2. The one case where the oracle
/// rejects and y-sharp accepts that is not a bug, claimed only when the oracle reports that its error came from that re-encode.
/// </summary>
public sealed class ZeroLengthDeleteRangeNormaliser : IDivergenceNormaliser
{
    public string Key => "zero-length-delete-range";

    public IReadOnlyList<string> RegistryIds { get; } = new[] { "ENC-51" };

    public string Description =>
        "A mutated update with a zero-length delete range for a client the document does not know is rejected by the oracle (Unexpected case from writeDsLen) and accepted by y-sharp, which ignores the range.";

    public bool Claims(MutationCase c) => c.OracleZeroLengthDeleteRange && !c.OracleAccepted && c.SharpAccepted;
}

/// <summary>
/// Yjs parks delete ranges it cannot apply yet and re-encodes them as V2; a client's ranges that arrive out of order make the V2 writer
/// write a negative clock difference, which lib0 writes as one garbage byte, so the parked ranges and every later state encode are corrupt
/// (ENC-86, ADR 0003). y-sharp keeps the ranges as sent. Both accept; the encoded state differs while the state vector and the content
/// match. Claimed only when the oracle reports that it parked out-of-order ranges, and the harness still compares sv and doc (the results
/// differ only by the flag itself).
/// </summary>
public sealed class UnsortedPendingDeleteSetNormaliser : IDivergenceNormaliser
{
    public string Key => "unsorted-pending-delete-set";

    public IReadOnlyList<string> RegistryIds { get; } = new[] { "ENC-86" };

    public string Description =>
        "A mutated update whose out-of-order delete ranges are parked as pending state is accepted by both; the oracle's re-encoded pending delete set is corrupt, so only the state encodings differ.";

    /// <summary>The observations still compared when this normaliser claims a case.</summary>
    public static readonly string[] StillCompared = { "sv", "doc" };

    public bool Claims(MutationCase c) => c.OracleUnsortedPendingDeleteSet && c.OracleAccepted && c.SharpAccepted;
}

/// <summary>
/// A struct whose origin, right origin or parent id is its own id or a later clock of its own client can never integrate. Yjs parks it as
/// pending state for good and accepts the update; y-sharp rejects the update as Malformed (TASK-4.14), the strict decoding of ADR 0005
/// recorded as Q15. Claimed only when the oracle reports that it parked such a struct and y-sharp left the document unchanged.
/// </summary>
public sealed class SelfDependentStructNormaliser : IDivergenceNormaliser
{
    public string Key => "self-dependent-struct";

    public IReadOnlyList<string> RegistryIds { get; } = new[] { "Q15" };

    public string Description =>
        "A mutated update holding a struct that depends on itself is parked as pending by the oracle and rejected by y-sharp without changing the document.";

    public bool Claims(MutationCase c) => c.OracleSelfDependentPending && c.OracleAccepted && !c.SharpAccepted && c.SharpUnchanged;
}

public static class Normalisers
{
    /// <summary>Every divergence the live differential knows. A divergence nobody registered here is reported as a bug.</summary>
    public static readonly IReadOnlyList<IDivergenceNormaliser> Registered =
        new IDivergenceNormaliser[] { new LenientTruncationNormaliser(), new LenientColumnEndNormaliser(), new ZeroLengthDeleteRangeNormaliser(), new UnsortedPendingDeleteSetNormaliser(),
            new SelfDependentStructNormaliser() };

    public static OutcomeClass Classify(bool oracleAccepted, bool sharpAccepted) =>
        (oracleAccepted, sharpAccepted) switch
        {
            (true, true) => OutcomeClass.BothAccept,
            (false, false) => OutcomeClass.BothReject,
            (true, false) => OutcomeClass.SharpRejectsOracleAccepts,
            _ => OutcomeClass.OracleRejectsSharpAccepts,
        };

    public static IDivergenceNormaliser? Claim(MutationCase c) => Registered.FirstOrDefault(n => n.Claims(c));
}
