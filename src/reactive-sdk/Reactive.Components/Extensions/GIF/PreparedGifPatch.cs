using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

internal sealed class PreparedGifPatch {
    private const long StorageBudget = 32L * 1024 * 1024;
    private GIFImage? _source;
    private Frame? _initial;
    private Task<Frame>? _pending;
    private bool _retired;

    private PreparedGifPatch(GIFImage source, Frame? initial) {
        _source = source;
        _initial = initial;
        Width = source.screen.width;
        Height = source.screen.height;
        FrameCount = source.imageData.Count;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FrameCount { get; }

    internal sealed class Frame(int index, int x, int y, int width, int height, Color32[] pixels, bool[]? writeMask) {
        internal readonly int Index = index;
        internal readonly int X = x;
        internal readonly int Y = y;
        internal readonly int Width = width;
        internal readonly int Height = height;
        internal readonly Color32[] Pixels = pixels;
        internal readonly bool[]? WriteMask = writeMask;
    }

    private sealed class Request(GIFImageBlock image, int index) {
        internal readonly GIFImageBlock Image = image;
        internal readonly int Index = index;
    }

    internal static PreparedGifPatch? TryCreateForOwnedImage(GIFImage gif) {
        try {
            if (gif.screen.width <= 0 || gif.screen.height <= 0 || gif.imageData.Count == 0 ||
                (long)gif.screen.width * gif.screen.height > int.MaxValue)
                return null;
            bool hasMaterialFrame = false;
            for (int i = 0; i < gif.imageData.Count; i++) {
                if (!IsSupported(gif, i)) return null;
                if (CanPrepare(gif, i)) hasMaterialFrame = true;
            }
            if (!hasMaterialFrame) return null;
            var initial = CanPrepare(gif, 0)
                ? Compose(new Request(Capture(gif, 0), 0)) : null;
            return new PreparedGifPatch(gif, initial);
        } catch (Exception) {
            return null;
        }
    }

    private static bool IsMaterial(GIFImageBlock image) =>
        image.usedColorTable != null && (long)image.width * image.height >= 16384;

    private static bool IsSupported(GIFImage gif, int index) {
        if (gif.imageData[index] is GIFTextBlock text)
            return text.GetType() == typeof(GIFTextBlock) && text.Parent == gif &&
                text.graphicControl != null && text.graphicControl.GetType() == typeof(GIFGraphicControlExt);
        if (gif.imageData[index] is not GIFImageBlock image || image.GetType() != typeof(GIFImageBlock) ||
            image.Parent != gif || image.graphicControl == null ||
            image.graphicControl.GetType() != typeof(GIFGraphicControlExt) || image.data == null ||
            image.data.GetType() != typeof(List<byte>))
            return false;
        if (image.width == 0 || image.height == 0) return true;
        if (image.xPos + image.width > gif.screen.width || image.yPos + image.height > gif.screen.height)
            return false;
        if (image.usedColorTable == null) return gif.screen.globalColorTable == null;
        return image.usedColorTable.Length > 0 && image.usedColorTable.Length <= 256 &&
            image.data.Count >= (long)image.width * image.height;
    }

    private static bool CanPrepare(GIFImage gif, int index) {
        if (!IsSupported(gif, index) || gif.imageData[index] is not GIFImageBlock image) return false;
        if (!IsMaterial(image)) return false;
        long pixels = (long)image.width * image.height;
        long storage = pixels * (NeedsWriteMask(image) ? 5 : 4) + image.data.Count + 1024 +
            (image.usedColorTable!.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4;
        if (pixels < 16384 || pixels > int.MaxValue || image.data.Count < pixels || storage * 2 > StorageBudget)
            return false;

        return true;
    }

    private static bool NeedsWriteMask(GIFImageBlock image) {
        bool restoreBackground = image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor;
        if (restoreBackground && image.Parent.screen.globalColorTable != null &&
            image.Parent.screen.bgColorIndex < image.Parent.screen.globalColorTable.Length)
            return false;
        return image.usedColorTable!.Length < 256 ||
            (image.graphicControl.HasTransparentColorIndex && !restoreBackground);
    }

    private static GIFImageBlock Capture(GIFImage gif, int index) {
        if (!CanPrepare(gif, index)) throw new InvalidOperationException();
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
        long retainedStorage = (long)source.width * source.height * (NeedsWriteMask(source) ? 5 : 4) +
            source.data.Capacity + 1024L +
            (source.usedColorTable!.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4;
        // Only the completed private decode producer supplies this immutable raster.
        image.data = retainedStorage * 2 <= StorageBudget ? source.data : new List<byte>(source.data);
        image.usedColorTable = (Color32[])source.usedColorTable!.Clone();
        image.colorTable = image.usedColorTable;
        return image;
    }

    private static Frame Compose(object? state) {
        var request = (Request)state!;
        var image = request.Image;
        var pixels = new Color32[image.width * image.height];
        image.Dispose(pixels, image.width, image.height, -image.xPos, image.yPos);
        image.DrawTo(pixels, image.width, image.height, -image.xPos, image.yPos);
        return new Frame(request.Index, image.xPos, image.yPos, image.width, image.height, pixels,
            NeedsWriteMask(image) ? CreateWriteMask(image) : null);
    }

    private static bool[] CreateWriteMask(GIFImageBlock image) {
        var mask = new bool[image.width * image.height];
        int transparent = image.graphicControl.HasTransparentColorIndex
            ? image.graphicControl.transparentColorIndex : -1;
        bool restoreBackground = image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor;
        int paletteLength = image.usedColorTable!.Length;
        for (int y = 0; y < image.height; y++) {
            int row = image.IsInterlaced ? image.GetInterlacedIndex(y) : y;
            int destination = (image.height - row - 1) * image.width;
            for (int x = 0; x < image.width; x++) {
                int color = image.data[x + y * image.width];
                mask[destination + x] = color == transparent ? restoreBackground : color < paletteLength;
            }
        }
        return mask;
    }

    internal bool OriginalFrame(int index) =>
        !_retired && _source != null && !CanPrepare(_source, index);

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
            var request = new Request(Capture(source, index), index);
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
