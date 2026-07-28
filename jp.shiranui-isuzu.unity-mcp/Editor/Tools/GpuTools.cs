using System;
using System.Linq;

using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

using UnityMCP.Editor.Core;
using UnityMCP.Editor.Core.Attributes;

namespace UnityMCP.Editor.Tools
{
    /// <summary>
    /// Reading GPU memory back and describing it.
    /// </summary>
    /// <remarks>
    /// Reports statistics rather than contents. A shadow page pool or a cull counter buffer is
    /// megabytes; what a question about it actually needs is "is anything in there", "what is the
    /// range", "how many are still zero" — and those answers are a few dozen bytes. Raw elements
    /// are available, but only as many as are asked for.
    /// </remarks>
    internal static class GpuTools
    {
        [McpTool(
            "gpu_readback",
            "Read a GPU buffer or texture back and report its statistics: range, mean, how many " +
            "elements are zero, and a histogram. Point it at a buffer with the same path syntax as " +
            "reflect_read. Use this to answer whether a compute pass actually wrote anything, which " +
            "is the question a screenshot cannot settle.",
            Idempotency = McpIdempotency.Safe)]
        public static JObject Readback(
            [McpArg("path", "Path to a GraphicsBuffer, ComputeBuffer or Texture, as reflect_read takes.")]
            string path = null,
            [McpArg("global_texture", "Read a globally bound shader texture by name instead.")]
            string globalTexture = null,
            [McpArg("format", "How to read each element: uint, int or float.")]
            string format = "float",
            [McpArg("offset", "First element to include.")]
            int offset = 0,
            [McpArg("count", "How many elements to examine; 0 reads everything.")]
            int count = 0,
            [McpArg("samples", "How many raw values to return alongside the statistics.")]
            int samples = 8,
            [McpArg("histogram", "Number of histogram buckets; 0 for none.")]
            int histogram = 8)
        {
            var source = ResolveSource(path, globalTexture, out var description);
            var kind = ParseFormat(format);

            double[] values;
            var meta = new JObject { ["source"] = description };

            switch (source)
            {
                case GraphicsBuffer graphicsBuffer:
                    meta["kind"] = "GraphicsBuffer";
                    meta["elementCount"] = graphicsBuffer.count;
                    meta["stride"] = graphicsBuffer.stride;
                    values = ReadBuffer(request => AsyncGPUReadback.Request(graphicsBuffer), kind);
                    break;

                case ComputeBuffer computeBuffer:
                    meta["kind"] = "ComputeBuffer";
                    meta["elementCount"] = computeBuffer.count;
                    meta["stride"] = computeBuffer.stride;
                    values = ReadBuffer(request => AsyncGPUReadback.Request(computeBuffer), kind);
                    break;

                case Texture texture:
                    meta["kind"] = texture.GetType().Name;
                    meta["width"] = texture.width;
                    meta["height"] = texture.height;
                    values = ReadTexture(texture, kind, meta);
                    break;

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"{description} is a {source.GetType().Name}, which is not a buffer or a texture.");
            }

            return Describe(values, offset, count, samples, histogram, meta);
        }

        private enum ElementKind
        {
            UInt,
            Int,
            Float,
        }

        private static ElementKind ParseFormat(string format)
        {
            switch ((format ?? "float").Trim().ToLowerInvariant())
            {
                case "uint":
                case "u32":
                    return ElementKind.UInt;

                case "int":
                case "i32":
                    return ElementKind.Int;

                case "float":
                case "f32":
                    return ElementKind.Float;

                default:
                    throw new McpToolException(
                        "invalid_params",
                        $"'{format}' is not an element format. Use uint, int or float.");
            }
        }

        private static object ResolveSource(string path, string globalTexture, out string description)
        {
            if (!string.IsNullOrWhiteSpace(globalTexture))
            {
                var texture = Shader.GetGlobalTexture(globalTexture);

                if (texture == null)
                {
                    throw new McpToolException(
                        "not_found",
                        $"No global texture named '{globalTexture}' is bound. Global bindings are set " +
                        "per frame, so one that is only bound during a pass will not be here.");
                }

                description = $"global texture '{globalTexture}'";
                return texture;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new McpToolException("invalid_params", "Either 'path' or 'global_texture' is required.");
            }

            var value = ReflectTools.ResolvePath(path, out _, out var walked);

            if (value == null)
            {
                throw new McpToolException("not_found", $"'{walked}' is null.");
            }

            description = walked;
            return value;
        }

        private static double[] ReadBuffer(Func<int, AsyncGPUReadbackRequest> requestFactory, ElementKind kind)
        {
            var request = requestFactory(0);
            request.WaitForCompletion();

            if (request.hasError)
            {
                throw new McpToolException(
                    "tool_failed",
                    "The readback failed. A buffer created without a readable target, or one already " +
                    "released, cannot be read back.");
            }

            switch (kind)
            {
                case ElementKind.UInt:
                    return request.GetData<uint>().Select(v => (double)v).ToArray();

                case ElementKind.Int:
                    return request.GetData<int>().Select(v => (double)v).ToArray();

                default:
                    return request.GetData<float>().Select(v => (double)v).ToArray();
            }
        }

        private static double[] ReadTexture(Texture texture, ElementKind kind, JObject meta)
        {
            var graphicsFormat = kind == ElementKind.UInt
                ? GraphicsFormat.R32_UInt
                : kind == ElementKind.Int
                    ? GraphicsFormat.R32_SInt
                    : GraphicsFormat.R32_SFloat;

            var request = AsyncGPUReadback.Request(texture, 0, graphicsFormat);
            request.WaitForCompletion();

            // Unity logs "R32_UInt ReadPixels failed" for integer formats and returns the data
            // anyway. hasError is the thing to check; the console line is noise.
            if (request.hasError)
            {
                throw new McpToolException(
                    "tool_failed",
                    $"The readback failed. {texture.name} may not be readable in {graphicsFormat}; " +
                    "try a different format, and note that a RenderTexture must have been rendered to.");
            }

            meta["readAs"] = graphicsFormat.ToString();

            switch (kind)
            {
                case ElementKind.UInt:
                    return request.GetData<uint>().Select(v => (double)v).ToArray();

                case ElementKind.Int:
                    return request.GetData<int>().Select(v => (double)v).ToArray();

                default:
                    return request.GetData<float>().Select(v => (double)v).ToArray();
            }
        }

        private static JObject Describe(
            double[] values, int offset, int count, int samples, int buckets, JObject meta)
        {
            var start = Math.Max(offset, 0);
            var length = count <= 0 ? values.Length - start : Math.Min(count, values.Length - start);

            if (start >= values.Length || length <= 0)
            {
                meta["examined"] = 0;
                meta["note"] = $"The window starts past the end; {values.Length} element(s) were read.";
                return meta;
            }

            var min = double.MaxValue;
            var max = double.MinValue;
            var sum = 0d;
            var zero = 0;

            for (var i = start; i < start + length; i++)
            {
                var v = values[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                if (v == 0d) zero++;
            }

            meta["readElements"] = values.Length;
            meta["examined"] = length;
            meta["min"] = min;
            meta["max"] = max;
            meta["mean"] = Math.Round(sum / length, 6);
            meta["zeroCount"] = zero;
            meta["nonZeroCount"] = length - zero;
            // The question behind most readbacks is whether the pass ran at all.
            meta["allZero"] = zero == length;

            if (samples > 0)
            {
                meta["samples"] = new JArray(values.Skip(start).Take(Math.Min(samples, length))
                    .Select(v => (object)v).ToArray());
            }

            if (buckets > 0 && max > min)
            {
                var counts = new int[buckets];

                for (var i = start; i < start + length; i++)
                {
                    var bucket = (int)((values[i] - min) / (max - min) * buckets);
                    counts[Math.Min(bucket, buckets - 1)]++;
                }

                meta["histogram"] = new JObject
                {
                    ["min"] = min,
                    ["max"] = max,
                    ["counts"] = new JArray(counts.Cast<object>().ToArray()),
                };
            }

            return meta;
        }
    }
}
