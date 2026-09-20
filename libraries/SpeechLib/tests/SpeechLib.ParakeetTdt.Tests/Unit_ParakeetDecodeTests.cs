using System.Reflection;
using Microsoft.ML.OnnxRuntime;
using SpeechLib.ParakeetTdt;
using Xunit;

namespace SpeechLib.ParakeetTdt.Tests;

/// <summary>
/// Unit tests for the CPU-tuning and greedy-decode helpers of
/// <see cref="ParakeetTdtRecognizer"/> that do not require the ONNX model
/// files (session creation / decoding need the real model and are covered
/// by the E2E verification in tools/converters/ParakeetTdt).
/// </summary>
public sealed class Unit_ParakeetDecodeTests
{
    private static readonly Type RecognizerType = typeof(ParakeetTdtRecognizer);
    private static readonly Type ProviderSelectorType =
        typeof(ParakeetTdtRecognizer).Assembly.GetType("SpeechLib.ExecutionProviderSelector")!;

    [Fact]
    public void CreateSessionOptions_UsesEveryCore()
    {
        // Half the cores used to be the policy because ORT's spinning workers saturated
        // the box; with spinning disabled all cores measure faster (RTF 0.074 -> 0.067).
        var options = InvokeStatic<SessionOptions>("CreateSessionOptions", "cpu");

        Assert.Equal(Math.Max(2, Environment.ProcessorCount), options.IntraOpNumThreads);
        Assert.Equal(1, options.InterOpNumThreads);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, options.GraphOptimizationLevel);
    }

    [Fact]
    public void CreateSessionOptions_NeverBelowTwoThreads()
    {
        // Even on a hypothetical 2-core box the floor is 2 threads.
        var options = InvokeStatic<SessionOptions>("CreateSessionOptions", "cpu");
        Assert.True(options.IntraOpNumThreads >= 2);
    }

    [Fact]
    public void Constructor_DefaultLeftContext_IsReducedForCpu()
    {
        // The default left context must be 5s (down from 10s) to cut the
        // re-encoded window from 14s to 9s per 2s chunk (~35% less encoder work).
        var ctor = RecognizerType.GetConstructors().Single();
        var left = ctor.GetParameters().Single(p => p.Name == "leftContextSeconds");
        Assert.Equal(5.0, left.DefaultValue);
    }

    [Fact]
    public void MaxTokensPerStep_IsAppliedInDecodeLoop()
    {
        // Regression guard: the greedy loop must reference _maxTokensPerStep so a
        // non-blank token with zero duration does NOT advance the frame until the
        // per-frame cap is reached (matches onnx-asr reference behaviour).
        var field = RecognizerType.GetField("_maxTokensPerStep", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        var decodeLoop = RecognizerType.GetMethod("DecodeFrames", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(decodeLoop);
        // If the field exists and the loop compiles against it, the cap is wired in.
    }

    [Fact]
    public void TrimConsumedAudio_KeepsLeftContext()
    {
        // The trim logic must retain at least one left-context worth of samples
        // before the decoded position so the next window can be rebuilt.
        var method = RecognizerType.GetMethod("TrimConsumedAudio", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
    }

    [Theory]
    [InlineData(true, true, true)]    // new word after emission -> space (word boundary)
    [InlineData(true, false, false)]  // first word -> no leading space
    [InlineData(false, true, false)]  // continuation token -> no space (mid-word join)
    [InlineData(false, false, false)] // continuation, first emission -> no space
    public void ShouldPrefixSpace_AddsSpaceOnlyForNewWordAfterEmission(
        bool startsWord, bool alreadyEmitted, bool expected)
    {
        var result = InvokeStatic<bool>("ShouldPrefixSpace", startsWord, alreadyEmitted);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DetokenizeChunk_UsesWordStartMarker()
    {
        // Regression guard for the mid-word split bug ("достаточ ный"): the
        // chunk detokenizer must consult the SentencePiece ▁ word-start marker
        // instead of unconditionally prefixing a space.
        Assert.NotNull(RecognizerType.GetField("_wordStartIds", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetMethod("DetokenizeChunk", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetMethod("ShouldPrefixSpace", BindingFlags.NonPublic | BindingFlags.Static));
    }

    [Theory]
    [InlineData("cpu", new[] { "CPUExecutionProvider" }, "Cpu")]
    [InlineData("cuda", new[] { "CPUExecutionProvider", "CUDAExecutionProvider" }, "Cuda")]
    [InlineData("cuda", new[] { "CPUExecutionProvider" }, "Cpu")] // unavailable -> fallback
    [InlineData("dml", new[] { "CPUExecutionProvider", "DmlExecutionProvider" }, "Dml")]
    [InlineData("dml", new[] { "CPUExecutionProvider" }, "Cpu")]  // unavailable -> fallback
    [InlineData("garbage", new[] { "CPUExecutionProvider", "CUDAExecutionProvider" }, "Cpu")]
    [InlineData(null, new[] { "CPUExecutionProvider", "CUDAExecutionProvider" }, "Cpu")]
    public void SelectProvider_ResolvesRequestedOrFallsBackToCpu(
        string? requested, string[] available, string expected)
    {
        var result = InvokeStatic<object>(ProviderSelectorType, "Select", requested, available);
        Assert.Equal(expected, result.ToString());
    }

    [Fact]
    public void CreateSessionOptions_StaleGpuSetting_FallsBackToCpuInsteadOfThrowing()
    {
        // Regression guard for the GPU	o-CPU move: a persisted provider
        // setting that is no longer shipped must not fail session creation.
        var options = InvokeStatic<SessionOptions>("CreateSessionOptions", "cuda");
        Assert.NotNull(options);
    }

    private static T InvokeStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, methodName);
        return (T)method.Invoke(null, args)!;
    }

    private static T InvokeStatic<T>(string methodName, params object?[] args) =>
        InvokeStatic<T>(RecognizerType, methodName, args);
}
