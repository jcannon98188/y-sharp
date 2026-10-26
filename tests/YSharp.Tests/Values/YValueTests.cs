using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;
using YSharp.TestKit.Gating;
using YSharp.TestKit.Traceability;

namespace YSharp.Tests.Values;

/// <summary>
/// The public YValue surface (ADR 0002, 0007): nine cases, copy on construction, wire equality and the YText attribute rule. These
/// run without a document; the byte-level behaviour is in <see cref="YValueAnyTests"/> and, through Doc, in ValueQuirkTests.
/// </summary>
[Requires(Feature.Values)]
public sealed class YValueTests
{
    private static YValue Obj(params (string Key, YValue Value)[] m) =>
        YValue.Object(m.Select(x => new KeyValuePair<string, YValue>(x.Key, x.Value)).ToArray());

    private static YValue Arr(params YValue[] items) => YValue.Array(items);

    [Fact]
    public void Layout_IsSixteenBytes() => Assert.Equal(16, Unsafe.SizeOf<YValue>());

    [Fact]
    public void Default_IsUndefined_AndNullIsADifferentCase()
    {
        Assert.Equal(YValueKind.Undefined, default(YValue).Kind);
        Assert.Equal(YValueKind.Undefined, YValue.Undefined.Kind);
        Assert.Equal(YValueKind.Null, YValue.Null.Kind);
        Assert.NotEqual(YValue.Null, YValue.Undefined);
    }

    [Fact]
    public void Kinds_FollowTheFactoryThatBuiltTheValue()
    {
        Assert.Equal(YValueKind.Bool, ((YValue)true).Kind);
        Assert.Equal(YValueKind.Number, ((YValue)1.5).Kind);
        Assert.Equal(YValueKind.Number, ((YValue)3).Kind);
        Assert.Equal(YValueKind.Number, ((YValue)3u).Kind);
        Assert.Equal(YValueKind.BigInt, YValue.BigInt(3).Kind);
        Assert.Equal(YValueKind.String, ((YValue)"s").Kind);
        Assert.Equal(YValueKind.Bytes, YValue.Bytes(new byte[] { 1 }).Kind);
        Assert.Equal(YValueKind.Array, Arr().Kind);
        Assert.Equal(YValueKind.Object, Obj().Kind);
    }

    [Fact]
    public void NullString_ConvertsToNull_AndEmptyStringIsAString()
    {
        Assert.Equal(YValue.Null, (YValue)(string?)null);
        Assert.Equal(YValueKind.String, ((YValue)"").Kind);
        Assert.True(((YValue)"").TryGetString(out var s) && s.Length == 0);
    }

    [Fact]
    public void Number_FromUlong_IsExactUpTo2Pow53_AndErrorsAbove()
    {
        Assert.Equal(9007199254740992.0, YValue.Number(1UL << 53).AsNumber());
        Assert.Equal(0.0, YValue.Number(0).AsNumber());
        Assert.Throws<ArgumentOutOfRangeException>(() => YValue.Number((1UL << 53) + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => YValue.Number(ulong.MaxValue));
    }

    [Fact]
    public void TryGet_SucceedsOnlyForItsOwnCase()
    {
        YValue num = 2.5, flag = true, text = "x", big = YValue.BigInt(-7);
        Assert.True(num.TryGetNumber(out var d) && d == 2.5);
        Assert.False(num.TryGetBool(out _));
        Assert.False(num.TryGetString(out var s) || s.Length != 0);
        Assert.False(num.TryGetBigInt(out _));
        Assert.True(flag.TryGetBool(out var b) && b);
        Assert.False(flag.TryGetNumber(out _));
        Assert.True(text.TryGetString(out var str) && str == "x");
        Assert.True(big.TryGetBigInt(out var l) && l == -7);
        Assert.False(big.TryGetNumber(out _));
        Assert.False(num.TryGetBytes(out var bytes) || bytes.Length != 0);
        Assert.False(num.TryGetArray(out var items) || items.Length != 0);
        Assert.False(num.TryGetObject(out var view) || view.Count != 0);
        Assert.False(YValue.Null.TryGetBool(out _));
        Assert.False(YValue.Undefined.TryGetNumber(out _));
    }

    [Fact]
    public void As_ThrowsInvalidOperation_ForAnotherCase()
    {
        Assert.True(((YValue)true).AsBool());
        Assert.Equal(4.0, ((YValue)4).AsNumber());
        Assert.Equal("q", ((YValue)"q").AsString());
        Assert.Throws<InvalidOperationException>(() => ((YValue)1).AsString());
        Assert.Throws<InvalidOperationException>(() => ((YValue)"1").AsNumber());
        Assert.Throws<InvalidOperationException>(() => YValue.Null.AsBool());
    }

    [Fact]
    public void Bytes_AreCopied_AndBytesOwnedTransfers()
    {
        var source = new byte[] { 1, 2, 3 };
        var copy = YValue.Bytes(source);
        source[0] = 9;
        Assert.True(copy.TryGetBytes(out var kept));
        Assert.Equal(new byte[] { 1, 2, 3 }, kept.ToArray());

        var owned = new byte[] { 4, 5 };
        var transferred = YValue.BytesOwned(owned);
        owned[1] = 6;
        Assert.True(transferred.TryGetBytes(out var shared));
        Assert.Equal(new byte[] { 4, 6 }, shared.ToArray());

        Assert.True(YValue.Bytes(ReadOnlySpan<byte>.Empty).TryGetBytes(out var empty) && empty.Length == 0);
        Assert.Throws<ArgumentNullException>(() => YValue.BytesOwned(null!));
    }

    [Fact]
    public void Array_CopiesItsItems_AndKeepsOrder()
    {
        var items = new YValue[] { 1, "a", YValue.Null };
        var value = YValue.Array(items);
        items[0] = 99;
        Assert.True(value.TryGetArray(out var read));
        Assert.Equal(3, read.Length);
        Assert.Equal(1.0, read[0].AsNumber());
        Assert.Equal("a", read[1].AsString());
        Assert.Equal(YValue.Null, read[2]);
    }

    [Fact]
    public void Object_KeepsInsertionOrder_AndLooksKeysUp()
    {
        var value = Obj(("b", 1), ("a", 2), ("10", 3), ("2", 4));
        Assert.True(value.TryGetObject(out var view));
        Assert.Equal(4, view.Count);
        Assert.Equal(new[] { "b", "a", "10", "2" }, Enumerable.Range(0, view.Count).Select(i => view[i].Key).ToArray());
        Assert.True(view.TryGetValue("a", out var a) && a.AsNumber() == 2);
        Assert.False(view.TryGetValue("missing", out var none));
        Assert.Equal(YValueKind.Undefined, none.Kind);
        Assert.Equal(0, default(YObjectView).Count);
        Assert.False(default(YObjectView).TryGetValue("a", out _));
    }

    [Fact, Covers("Q09")]
    public void Object_DuplicateKeys_KeepTheFirstPositionAndTheLastValue()
    {
        var value = Obj(("a", 1), ("b", 2), ("a", 3), ("__proto__", 4), ("b", 5));
        Assert.True(value.TryGetObject(out var view));
        Assert.Equal(3, view.Count);
        Assert.Equal("a", view[0].Key);
        Assert.Equal(3.0, view[0].Value.AsNumber());
        Assert.Equal("b", view[1].Key);
        Assert.Equal(5.0, view[1].Value.AsNumber());
        Assert.Equal("__proto__", view[2].Key);                    // an ordinary key
        Assert.True(view.TryGetValue("__proto__", out var proto) && proto.AsNumber() == 4);
    }

    [Fact]
    public void Object_WithManyKeys_LooksUpEveryKey_AndDedupesWithoutQuadraticWork()
    {
        const int n = 50_000;
        var members = Enumerable.Range(0, n).Select(i => new KeyValuePair<string, YValue>("k" + i, i))
            .Concat(Enumerable.Range(0, n).Select(i => new KeyValuePair<string, YValue>("k" + i, i + n))).ToArray();
        var value = YValue.Object(members);
        Assert.True(value.TryGetObject(out var view));
        Assert.Equal(n, view.Count);
        Assert.True(view.TryGetValue("k49999", out var last) && last.AsNumber() == 99_999);
        Assert.Equal("k0", view[0].Key);
        Assert.Equal((double)n, view[0].Value.AsNumber());
    }

    [Fact]
    public void Object_RejectsANullKey() =>
        Assert.Throws<ArgumentNullException>(() => YValue.Object(new[] { new KeyValuePair<string, YValue>(null!, 1) }));

    // ------------------------------------------------------------------ wire equality

    [Fact, Covers("Q03", "Q04")]
    public void Equality_IsWireEquality_NaNEqualsNaN_AndNegativeZeroDiffers()
    {
        Assert.Equal((YValue)double.NaN, (YValue)BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL)));
        Assert.NotEqual((YValue)0.0, (YValue)(-0.0));
        Assert.Equal((YValue)(-0.0), (YValue)(-0.0));
        Assert.True((YValue)1 == (YValue)1.0);
        Assert.True((YValue)1 != (YValue)2);
        Assert.NotEqual(YValue.BigInt(5), (YValue)5);
        Assert.Equal(YValue.BigInt(5), YValue.BigInt(5));
        Assert.NotEqual((YValue)"1", (YValue)1);
        Assert.NotEqual((YValue)true, (YValue)1);
        Assert.NotEqual(YValue.Null, (YValue)false);
    }

    [Fact]
    public void Equality_ComparesContainersDeeply_AndInOrder()
    {
        Assert.Equal(Obj(("a", Arr(1, 2)), ("b", YValue.Bytes(new byte[] { 1 }))), Obj(("a", Arr(1, 2)), ("b", YValue.Bytes(new byte[] { 1 }))));
        Assert.NotEqual(Arr(1, 2), Arr(2, 1));
        Assert.NotEqual(Arr(1), Arr(1, 1));
        Assert.NotEqual(Obj(("a", 1), ("b", 2)), Obj(("b", 2), ("a", 1)));
        Assert.NotEqual(Obj(("a", 1)), Obj(("a", 2)));
        Assert.NotEqual(Obj(("a", 1)), Obj(("b", 1)));
        Assert.NotEqual(Arr(Arr(1)), Arr(Arr(2)));
        Assert.NotEqual(YValue.Bytes(new byte[] { 1 }), YValue.Bytes(new byte[] { 2 }));
        Assert.NotEqual(Arr(), Obj());
        Assert.True(Equals((object)Arr(1), Arr(1)));
        Assert.False(Arr(1).Equals("not a value"));
    }

    [Fact]
    public void GetHashCode_AgreesWithEquals()
    {
        var pairs = new (YValue, YValue)[]
        {
            (double.NaN, BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8000000000001UL))),
            (1, 1.0), ("abc", new string("abc".ToCharArray())),
            (Obj(("a", Arr(1, "x", YValue.Null))), Obj(("a", Arr(1, "x", YValue.Null)))),
            (YValue.Bytes(new byte[] { 1, 2 }), YValue.Bytes(new byte[] { 1, 2 })),
            (YValue.BigInt(9), YValue.BigInt(9)), (YValue.Undefined, default),
        };
        foreach (var (a, b) in pairs)
        {
            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }
        Assert.NotEqual(((YValue)0.0).GetHashCode(), ((YValue)(-0.0)).GetHashCode());   // -0 is a different value, so it may hash differently
        Assert.NotEqual(Arr(1, 2).GetHashCode(), Arr(2, 1).GetHashCode());
    }

    [Fact]
    public void EqualsAndHash_AreIterative_ADeeplyNestedValueDoesNotOverflowTheStack()
    {
        static YValue Nest(int depth, YValue leaf)
        {
            var v = leaf;
            for (var i = 0; i < depth; i++) v = YValue.Array(new[] { v });
            return v;
        }
        var a = Nest(200_000, 1);
        var b = Nest(200_000, 1);
        var c = Nest(200_000, 2);
        Assert.True(a.Equals(b));
        Assert.False(a.Equals(c));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ------------------------------------------------------------------ AttributeEquals (Q27)

    [Fact, Covers("Q27")]
    public void AttributeEquals_Scalars_FollowStrictEquality()
    {
        Assert.False(((YValue)double.NaN).AttributeEquals((YValue)double.NaN));
        Assert.True(((YValue)0.0).AttributeEquals((YValue)(-0.0)));
        Assert.True(((YValue)1).AttributeEquals((YValue)1.0));
        Assert.True(YValue.Null.AttributeEquals(YValue.Null));
        Assert.True(YValue.Undefined.AttributeEquals(default));
        Assert.False(YValue.Null.AttributeEquals(default));
        Assert.False(((YValue)1).AttributeEquals(YValue.Null));
        Assert.True(YValue.BigInt(4).AttributeEquals(YValue.BigInt(4)));
        Assert.False(YValue.BigInt(4).AttributeEquals((YValue)4));
        Assert.True(((YValue)"a").AttributeEquals((YValue)new string('a', 1)));
        Assert.False(((YValue)"a").AttributeEquals((YValue)"b"));
        Assert.True(((YValue)true).AttributeEquals((YValue)true));
        Assert.False(((YValue)true).AttributeEquals((YValue)false));
    }

    [Fact, Covers("Q27")]
    public void AttributeEquals_Containers_CompareOneLevelDeep_AndNestedOnesByReference()
    {
        Assert.True(Obj(("a", 1)).AttributeEquals(Obj(("a", 1))));
        Assert.True(Obj(("a", 1), ("b", 2)).AttributeEquals(Obj(("b", 2), ("a", 1))));      // key order does not matter
        Assert.False(Obj(("a", 1)).AttributeEquals(Obj(("a", 2))));
        Assert.False(Obj(("a", 1)).AttributeEquals(Obj(("a", 1), ("b", 2))));
        Assert.False(Obj(("a", Obj(("b", 1)))).AttributeEquals(Obj(("a", Obj(("b", 1))))));  // structurally equal but distinct instances

        var shared = Obj(("b", 1));
        Assert.True(Obj(("a", shared)).AttributeEquals(Obj(("a", shared))));                 // the same instance is equal
        Assert.True(shared.AttributeEquals(shared));

        Assert.True(Arr(1, "x").AttributeEquals(Arr(1, "x")));
        Assert.False(Arr(1, "x").AttributeEquals(Arr("x", 1)));
        Assert.False(Arr(Arr(1)).AttributeEquals(Arr(Arr(1))));
        Assert.False(Obj(("a", double.NaN)).AttributeEquals(Obj(("a", double.NaN))));        // NaN never equals NaN, one level down too
        Assert.True(Obj(("a", YValue.Undefined)).AttributeEquals(Obj(("a", YValue.Undefined))));
        Assert.False(Obj(("a", YValue.Undefined)).AttributeEquals(Obj(("b", YValue.Undefined))));
        Assert.True(YValue.Bytes(new byte[] { 1, 2 }).AttributeEquals(YValue.Bytes(new byte[] { 1, 2 })));
        Assert.False(YValue.Bytes(new byte[] { 1, 2 }).AttributeEquals(YValue.Bytes(new byte[] { 1, 3 })));
        Assert.True(Arr().AttributeEquals(Arr()));
        Assert.False(Arr(1).AttributeEquals(YValue.Null));
        Assert.False(Obj().AttributeEquals((YValue)"x"));
    }

    [Fact, Covers("Q27")]
    public void AttributeEquals_ObjectAgainstArray_ComparesKeysAsJsDoes()
    {
        // equalFlat compares own keys one level deep and does not look at the container type: {"0":1} equals [1]
        Assert.True(Obj(("0", 1)).AttributeEquals(Arr(1)));
        Assert.True(Arr(1, 2).AttributeEquals(Obj(("1", 2), ("0", 1))));
        Assert.False(Obj(("1", 1)).AttributeEquals(Arr(1)));
        Assert.False(Obj(("0", 1), ("1", 2)).AttributeEquals(Arr(1)));
    }

    [Fact]
    public void ToString_DescribesTheCase_WithoutWalkingContainers()
    {
        Assert.Equal("Undefined", default(YValue).ToString());
        Assert.Equal("Null", YValue.Null.ToString());
        Assert.Equal("Bool(true)", ((YValue)true).ToString());
        Assert.StartsWith("Number(", ((YValue)1.5).ToString());
        Assert.Equal("Array(2)", Arr(1, 2).ToString());
        Assert.Equal("Object(1)", Obj(("a", 1)).ToString());
    }
}
