#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

// ---------------------------------------------------------------- ids, state vectors, delete sets

public readonly record struct ItemId(ulong Client, long Clock);   // client ids are 53-bit capable (ADR 0008, Q12); clock <= 2^53-1, so long

