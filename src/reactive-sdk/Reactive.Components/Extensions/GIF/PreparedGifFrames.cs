using System;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

internal sealed class PreparedGifFrames {
    private const long StorageBudget = 32L * 1024 * 1024;
    private const long CompositionBudget = 64L * 1024 * 1024;
    private const int MaxFrames = 1024;

    private readonly Color32[][] _first;
    private readonly Color32[][] _steady;

    private PreparedGifFrames(int width, int height, Color32[][] first, Color32[][] steady) {
        Width = width;
        Height = height;
        _first = first;
        _steady = steady;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FrameCount => _first.Length;

    internal Color32[] GetFrame(int index, bool hasLooped) => hasLooped ? _steady[index] : _first[index];

    internal static PreparedGifFrames? TryCreate(GIFImage gif) {
        try {
            var width = (int)gif.screen.width;
            var height = (int)gif.screen.height;
            var count = gif.imageData.Count;
            if (width <= 0 || height <= 0 || count <= 0 || count > MaxFrames) return null;

            var pixels = (long)width * height;
            var frameBytes = pixels * 4 + 32;
            var usedBytes = pixels * 4 + count * 16L;
            if (pixels > int.MaxValue || usedBytes >= StorageBudget ||
                frameBytes > (StorageBudget - usedBytes) / count) return null;

            var blocks = new GIFImageBlock[count];
            long visits = 0;
            for (var i = 0; i < count; i++) {
                if (gif.imageData[i] is not GIFImageBlock block || block.GetType() != typeof(GIFImageBlock)) return null;
                if (block.width == 0 || block.height == 0) return null;
                visits += (long)block.width * block.height * 8;
                if (visits > CompositionBudget) return null;
                blocks[i] = block.CloneForPreparation();
            }

            var colors = new Color32[(int)pixels];
            var first = new Color32[count][];
            var steady = new Color32[count][];
            for (var i = 0; i < count; i++) {
                Compose(blocks[i], colors, width, height);
                var frame = (Color32[])colors.Clone();
                Compose(blocks[i], frame, width, height);
                if (!SamePixels(colors, frame)) return null;
                first[i] = frame;
                usedBytes += frameBytes;
            }

            for (var i = 0; i < count; i++) {
                Compose(blocks[i], colors, width, height);
                if (SamePixels(colors, first[i])) {
                    steady[i] = first[i];
                } else {
                    if (frameBytes > StorageBudget - usedBytes) return null;
                    var frame = (Color32[])colors.Clone();
                    Compose(blocks[i], frame, width, height);
                    if (!SamePixels(colors, frame)) return null;
                    steady[i] = frame;
                    usedBytes += frameBytes;
                }
            }

            // Equal terminal pixels make the second cycle repeat from the same state.
            return SamePixels(colors, first[count - 1]) ? new PreparedGifFrames(width, height, first, steady) : null;
        } catch (Exception) {
            return null;
        }
    }

    private static void Compose(GIFImageBlock frame, Color32[] colors, int width, int height) {
        frame.Dispose(colors, width, height);
        frame.DrawTo(colors, width, height);
    }

    private static bool SamePixels(Color32[] left, Color32[] right) {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++) {
            var a = left[i];
            var b = right[i];
            if (a.r != b.r || a.g != b.g || a.b != b.b || a.a != b.a) return false;
        }

        return true;
    }
}
