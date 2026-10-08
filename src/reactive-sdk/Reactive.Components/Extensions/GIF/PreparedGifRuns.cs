using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

internal sealed class PreparedGifRuns {
    private const long PacketBudget = 16L * 1024 * 1024;
    private const long AuxiliaryStorage = 256L * 1024;
    private const int ChunkSize = 4096;
    private const int TileSize = 128;
    private GIFImage? _source;
    private Frame? _initial;
    private Task<Frame>? _pending;
    private bool _retired;

    private PreparedGifRuns(GIFImage source, Frame? initial) {
        _source = source;
        _initial = initial;
        Width = source.screen.width;
        Height = source.screen.height;
        FrameCount = source.imageData.Count;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FrameCount { get; }
    internal const int RunsPerChunk = ChunkSize;

    internal readonly struct Run(int destination, int length, int color) {
        internal readonly int Destination = destination;
        internal readonly int Length = length;
        internal readonly int Color = color;
    }

    internal sealed class Frame(int index, List<Run[]> chunks, int count, Color32[][] tiles, bool drawFailed) {
        internal readonly int Index = index;
        internal readonly List<Run[]> Chunks = chunks;
        internal readonly int Count = count;
        internal readonly Color32[][] Tiles = tiles;
        internal readonly bool DrawFailed = drawFailed;
    }

    private sealed class Request(GIFImageBlock image, int width, int height, int index, int maximumChunks) {
        internal readonly GIFImageBlock Image = image;
        internal readonly int Width = width;
        internal readonly int Height = height;
        internal readonly int Index = index;
        internal readonly int MaximumChunks = maximumChunks;
    }

    private sealed class RunBuilder(int maximumChunks) {
        internal readonly List<Run[]> Chunks = new();
        internal int Count;

        internal void AddClipped(int destination, int length, int color, int canvasLength) {
            long start = Math.Max(0L, destination);
            long end = Math.Min((long)canvasLength, (long)destination + length);
            if (end > start) Add((int)start, (int)(end - start), color);
        }

        internal void Add(int destination, int length, int color) {
            int offset = Count % ChunkSize;
            if (offset == 0) {
                if (Chunks.Count == maximumChunks) throw new InvalidOperationException();
                Chunks.Add(new Run[ChunkSize]);
            }
            Chunks[Chunks.Count - 1][offset] = new Run(destination, length, color);
            Count++;
        }
    }

    internal static PreparedGifRuns? TryCreateForOwnedImage(GIFImage gif) {
        try {
            long pixels = (long)gif.screen.width * gif.screen.height;
            if (gif.screen.width <= 0 || gif.screen.height <= 0 || gif.imageData.Count == 0 ||
                pixels > int.MaxValue)
                return null;
            bool hasMaterialFrame = false;
            for (int i = 0; i < gif.imageData.Count; i++) {
                if (!IsSupported(gif, i)) return null;
                if (CanPrepare(gif, i, out _)) hasMaterialFrame = true;
            }
            if (!hasMaterialFrame) return null;
            var initial = CanPrepare(gif, 0, out _) ? Compose(Capture(gif, 0)) : null;
            return new PreparedGifRuns(gif, initial);
        } catch (Exception) {
            return null;
        }
    }

    private static bool IsMaterial(GIFImageBlock image) {
        if (image.usedColorTable == null || !HasSafeGeometry(image)) return false;
        long pixels = (long)image.width * image.height;
        if (image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor &&
            image.Parent.screen.globalColorTable != null &&
            image.Parent.screen.bgColorIndex < image.Parent.screen.globalColorTable.Length)
            return pixels >= 16384;
        return Math.Min(pixels, image.data.Count) >= 16384;
    }

    private static bool HasSafeGeometry(GIFImageBlock image) {
        if (image.width == 0 || image.height == 0) return false;
        var screen = image.Parent.screen;
        long pixels = (long)image.width * image.height;
        long lowestRow = (long)screen.height - image.yPos - image.height;
        long highestRow = (long)screen.height - image.yPos - 1;
        long lowestProduct = lowestRow * screen.width;
        long highestProduct = highestRow * screen.width;
        long first = lowestProduct + image.xPos;
        long last = highestProduct + image.xPos + image.width - 1;
        if (pixels > int.MaxValue || lowestProduct < int.MinValue || highestProduct > int.MaxValue ||
            first < int.MinValue || last > int.MaxValue)
            return false;
        bool uncheckedWrites = image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor &&
            (image.graphicControl.HasTransparentColorIndex ||
                (screen.globalColorTable != null && screen.bgColorIndex < screen.globalColorTable.Length));
        return !uncheckedWrites || (first >= 0 && last < (long)screen.width * screen.height);
    }

    private static bool IsSupported(GIFImage gif, int index) {
        if (gif.imageData[index] is GIFTextBlock text)
            return text.GetType() == typeof(GIFTextBlock) && text.Parent == gif &&
                text.graphicControl != null && text.graphicControl.GetType() == typeof(GIFGraphicControlExt);
        if (gif.imageData[index] is not GIFImageBlock image || image.GetType() != typeof(GIFImageBlock) ||
            image.Parent != gif || image.graphicControl == null ||
            image.graphicControl.GetType() != typeof(GIFGraphicControlExt) || image.data == null ||
            image.data.GetType() != typeof(List<byte>) ||
            (gif.screen.globalColorTable?.Length ?? 0) > 256)
            return false;
        if (image.usedColorTable == null) return gif.screen.globalColorTable == null;
        return image.usedColorTable.Length > 0 && image.usedColorTable.Length <= 256;
    }

    private static bool CanPrepare(GIFImage gif, int index, out int maximumChunks) {
        maximumChunks = 0;
        if (!IsSupported(gif, index) || gif.imageData[index] is not GIFImageBlock image) return false;
        if (!IsMaterial(image)) return false;
        long remaining = PacketBudget - AuxiliaryStorage - image.data.Count -
            (image.usedColorTable!.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4;
        maximumChunks = (int)(remaining / (ChunkSize * 12L + 32));
        return maximumChunks > 0;
    }

    private static Request Capture(GIFImage gif, int index) {
        if (!CanPrepare(gif, index, out int maximumChunks)) throw new InvalidOperationException();
        var source = (GIFImageBlock)gif.imageData[index];
        var parent = new GIFImage { screen = gif.screen, BackgroundTransparent = gif.BackgroundTransparent };
        if (gif.screen.globalColorTable != null)
            parent.screen.globalColorTable = (Color32[])gif.screen.globalColorTable.Clone();
        var image = source.CloneForPreparation();
        image.Parent = parent;
        image.graphicControl = new GIFGraphicControlExt(parent) {
            flags = source.graphicControl.flags,
            delay = source.graphicControl.delay,
            transparentColorIndex = source.graphicControl.transparentColorIndex
        };
        long retainedStorage = source.data.Capacity + AuxiliaryStorage +
            (source.usedColorTable!.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4 +
            maximumChunks * (ChunkSize * 12L + 32);
        // Only the completed private decode producer supplies this immutable raster.
        image.data = retainedStorage <= PacketBudget ? source.data : new List<byte>(source.data);
        image.usedColorTable = (Color32[])source.usedColorTable!.Clone();
        image.colorTable = image.usedColorTable;
        return new Request(image, gif.screen.width, gif.screen.height, index, maximumChunks);
    }

    private static Frame Compose(object? state) {
        var request = (Request)state!;
        var image = request.Image;
        var palette = image.usedColorTable!;
        var screen = image.Parent.screen;
        var global = screen.globalColorTable;
        bool hasBackground = global != null && screen.bgColorIndex < global.Length;
        var disposalColor = hasBackground ? global![screen.bgColorIndex] : new Color32(0, 0, 0, 0);
        var drawColor = hasBackground && screen.HasGlobalColorTable ? disposalColor : new Color32(0, 0, 0, 0);
        if (image.Parent.BackgroundTransparent) {
            disposalColor.a = 0;
            drawColor.a = 0;
        }
        var tiles = new Color32[palette.Length + 2][];
        for (int i = 0; i < tiles.Length; i++) {
            var color = i < palette.Length ? palette[i] : i == palette.Length ? disposalColor : drawColor;
            var tile = new Color32[TileSize];
            for (int x = 0; x < tile.Length; x++) tile[x] = color;
            tiles[i] = tile;
        }
        var builder = new RunBuilder(request.MaximumChunks);
        int canvasLength = request.Width * request.Height;
        bool restoreBackground = image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor;
        if (restoreBackground && hasBackground) {
            for (int y = 0; y < image.height; y++) {
                int destination = image.xPos + (request.Height - y - image.yPos - 1) * request.Width;
                builder.Add(destination, image.width, palette.Length);
            }
        }
        int transparent = image.graphicControl.HasTransparentColorIndex
            ? image.graphicControl.transparentColorIndex : -1;
        if (image.IsInterlaced) image.CalcInterlacedLimits();
        for (int y = 0; y < image.height; y++) {
            int row = image.IsInterlaced ? image.GetInterlacedIndex(y) : y;
            int destination = image.xPos + (request.Height - row - image.yPos - 1) * request.Width;
            int start = 0;
            int previous = -1;
            for (int x = 0; x < image.width; x++) {
                int source = x + y * image.width;
                if (source >= image.data.Count) {
                    if (previous >= 0) builder.AddClipped(destination + start, x - start, previous, canvasLength);
                    return new Frame(request.Index, builder.Chunks, builder.Count, tiles, true);
                }
                int color = image.data[source];
                int code = color == transparent ? restoreBackground ? palette.Length + 1 : -1
                    : color < palette.Length ? color : -1;
                if (code == previous) continue;
                if (previous >= 0) builder.AddClipped(destination + start, x - start, previous, canvasLength);
                start = x;
                previous = code;
            }
            if (previous >= 0) builder.AddClipped(destination + start, image.width - start, previous, canvasLength);
        }
        return new Frame(request.Index, builder.Chunks, builder.Count, tiles, false);
    }

    internal bool OriginalFrame(int index) =>
        !_retired && _source != null && !CanPrepare(_source, index, out _);

    internal bool TryTake(int index, out Frame? frame) {
        frame = null;
        if (_retired) return false;
        if (_initial != null) {
            frame = _initial;
            _initial = null;
        } else {
            var pending = _pending;
            if (pending == null || !pending.IsCompleted) return false;
            _pending = null;
            try {
                frame = pending.GetAwaiter().GetResult();
            } catch (Exception) {
                Retire();
                return false;
            }
        }
        if (frame.Index == index) return true;
        Retire();
        frame = null;
        return false;
    }

    internal void Prefetch(int index) {
        if (_retired || _pending != null) return;
        try {
            var source = _source!;
            if (source.imageData.Count != FrameCount || source.screen.width != Width || source.screen.height != Height) {
                Retire();
                return;
            }
            if (OriginalFrame(index)) return;
            var request = Capture(source, index);
            if (ExecutionContext.IsFlowSuppressed()) {
                Start(request);
            } else {
                using (ExecutionContext.SuppressFlow()) {
                    Start(request);
                }
            }
        } catch (Exception) {
            Retire();
        }
    }

    private void Start(Request request) {
        _pending = Task.Factory.StartNew(Compose, request, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        _ = _pending.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void Retire() {
        _retired = true;
        _source = null;
        _initial = null;
        _pending = null;
    }
}
