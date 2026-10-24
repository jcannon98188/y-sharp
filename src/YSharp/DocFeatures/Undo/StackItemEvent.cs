#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;

namespace YSharp;

public readonly record struct StackItemEvent(StackItem Item, object? Origin, bool IsRedo, IReadOnlyCollection<YType> ChangedParentTypes);

