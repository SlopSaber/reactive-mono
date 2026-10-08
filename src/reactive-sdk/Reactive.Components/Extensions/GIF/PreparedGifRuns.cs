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

    private PreparedGifRuns(GIFImage source, Frame initial) {
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

    internal sealed class Frame(int index, List<Run[]> chunks, int count, Color32[][] tiles) {
        internal readonly int Index = index;
        internal readonly List<Run[]> Chunks = chunks;
        internal readonly int Count = count;
        internal readonly Color32[][] Tiles = tiles;
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
                pixels > int.MaxValue || pixels * 12 < PacketBudget * 2)
                return null;
            for (int i = 0; i < gif.imageData.Count; i++) {
                if (!CanPrepare(gif, i, out _)) return null;
            }
            return new PreparedGifRuns(gif, Compose(Capture(gif, 0)));
        } catch (Exception) {
            return null;
        }
    }

    private static bool CanPrepare(GIFImage gif, int index, out int maximumChunks) {
        maximumChunks = 0;
        if (gif.imageData[index] is not GIFImageBlock image || image.GetType() != typeof(GIFImageBlock) ||
            image.Parent != gif || image.graphicControl == null ||
            image.graphicControl.GetType() != typeof(GIFGraphicControlExt) || image.data == null ||
            image.data.GetType() != typeof(List<byte>) || image.usedColorTable == null ||
            image.usedColorTable.Length == 0 || image.usedColorTable.Length > 256 ||
            (gif.screen.globalColorTable?.Length ?? 0) > 256 || image.width <= 0 || image.height < 8 ||
            image.xPos + image.width > gif.screen.width || image.yPos + image.height > gif.screen.height)
            return false;
        long pixels = (long)image.width * image.height;
        if (pixels < 16384 || image.data.Count < pixels) return false;
        long remaining = PacketBudget - AuxiliaryStorage - image.data.Count -
            (image.usedColorTable.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4;
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
                int color = image.data[x + y * image.width];
                int code = color == transparent ? restoreBackground ? palette.Length + 1 : -1
                    : color < palette.Length ? color : -1;
                if (code == previous) continue;
                if (previous >= 0) builder.Add(destination + start, x - start, previous);
                start = x;
                previous = code;
            }
            if (previous >= 0) builder.Add(destination + start, image.width - start, previous);
        }
        return new Frame(request.Index, builder.Chunks, builder.Count, tiles);
    }

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
