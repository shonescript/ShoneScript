// Copyright (c) 2026 Shone
// SPDX-License-Identifier: MIT

using Sdcb.Arithmetic;
using ShonePy;

namespace ShonePy.Tests;

public sealed class PyScalarSemanticsTests
{
    [Fact]
    public void MixedIntegerFloatComparisonIsExact()
    {
        var integer = PyValue.From(BigInt.Parse("9007199254740993"));
        var roundedFloat = PyValue.From(9007199254740992d);

        Assert.False(PyOperations.Equal(integer, roundedFloat));
        Assert.True(PyOperations.Compare(integer, ">", roundedFloat));
        Assert.Equal(PyValue.From(1), PyValue.From(1d));
        Assert.Equal(PyOperations.Hash(PyValue.From(1)), PyOperations.Hash(PyValue.From(1d)));
    }

    [Fact]
    public void NaNIsUnequalAndUnordered()
    {
        var nan = PyValue.From(double.NaN);

        Assert.False(PyOperations.Equal(nan, nan));
        Assert.False(PyOperations.Compare(nan, "<", PyValue.From(0)));
        Assert.False(PyOperations.Compare(nan, ">=", nan));
    }

    [Fact]
    public void BooleansParticipateInIntegerAndBitwiseArithmetic()
    {
        Assert.Equal(PyValue.From(2), PyOperations.Binary(PyValue.True, "+", PyValue.True));
        Assert.Equal(PyValue.From(1), PyOperations.Binary(PyValue.True, "|", PyValue.False));
        Assert.Equal(PyValue.From(0), PyOperations.Binary(PyValue.True, "^", PyValue.True));
        Assert.Equal(PyValue.From(2), PyOperations.Binary(PyValue.True, "<<", PyValue.True));
    }

    [Fact]
    public void IdentityContainmentAndUnicodeLengthAreAvailable()
    {
        var text = PyValue.From("😀x");
        Assert.Equal(PyValue.From(2), PyBuiltins.Call("len", [text]));
        Assert.True(PyOperations.Contains(text, PyValue.From("😀")));
        Assert.True(PyOperations.Contains(PyValue.From(new PyList([PyValue.From(1)])), PyValue.True));
        Assert.True(PyOperations.Identity(PyValue.From(1), PyValue.From(1)));
        Assert.False(PyOperations.Identity(PyValue.From(new PyList()), PyValue.From(new PyList())));
    }

    [Fact]
    public void RepetitionRejectsOversizedAllocations()
    {
        var error = Assert.Throws<PythonException>(() => PyOperations.Binary(
            PyValue.From("123456789"), "*", PyValue.From(1_000_000)));
        Assert.Equal("OverflowError", error.ErrorName);
    }
}
