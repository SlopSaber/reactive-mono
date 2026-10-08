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

    private PreparedGifPatch(GIFImage source, Frame initial) {
        _source = source;
        _initial = initial;
        Width = source.screen.width;
        Height = source.screen.height;
        FrameCount = source.imageData.Count;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FrameCount { get; }

    internal sealed class Frame(int index, int x, int y, int width, int height, Color32[] pixels) {
        internal readonly int Index = index;
        internal readonly int X = x;
        internal readonly int Y = y;
        internal readonly int Width = width;
        internal readonly int Height = height;
        internal readonly Color32[] Pixels = pixels;
    }

    private sealed class Request(GIFImageBlock image, int index) {
        internal readonly GIFImageBlock Image = image;
        internal readonly int Index = index;
    }

    internal static PreparedGifPatch? TryCreateForOwnedImage(GIFImage gif) {
        try {
            if (gif.screen.width <= 0 || gif.screen.height <= 0 || gif.imageData.Count == 0 ||
                (long)gif.screen.width * gif.screen.height * 12 < StorageBudget)
                return null;
            for (int i = 0; i < gif.imageData.Count; i++) {
                if (!CanPrepare(gif, i)) return null;
            }
            return new PreparedGifPatch(gif, Compose(new Request(Capture(gif, 0), 0)));
        } catch (Exception) {
            return null;
        }
    }

    private static bool CanPrepare(GIFImage gif, int index) {
        if (gif.imageData[index] is not GIFImageBlock image || image.GetType() != typeof(GIFImageBlock) ||
            image.Parent != gif || image.graphicControl == null ||
            image.graphicControl.GetType() != typeof(GIFGraphicControlExt) || image.data == null ||
            image.data.GetType() != typeof(List<byte>) || image.usedColorTable?.Length != 256 ||
            image.width <= 0 || image.height < 8 || image.xPos + image.width > gif.screen.width ||
            image.yPos + image.height > gif.screen.height)
            return false;

        long pixels = (long)image.width * image.height;
        long storage = pixels * 4 + image.data.Count + 1024 +
            (image.usedColorTable.LongLength + (gif.screen.globalColorTable?.LongLength ?? 0)) * 4;
        if (pixels < 16384 || pixels > int.MaxValue || image.data.Count < pixels || storage * 2 > StorageBudget)
            return false;

        // Transparent pixels need a complete current-frame background, never a borrowed prior canvas.
        return !image.graphicControl.HasTransparentColorIndex ||
            (image.graphicControl.DisposalMethod == EDisposalMethod.RestoreBackgroundColor &&
                gif.screen.globalColorTable != null && gif.screen.bgColorIndex < gif.screen.globalColorTable.Length);
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
        image.data = new List<byte>(source.data);
        image.usedColorTable = (Color32[])source.usedColorTable.Clone();
        image.colorTable = image.usedColorTable;
        return image;
    }

    private static Frame Compose(object? state) {
        var request = (Request)state!;
        var image = request.Image;
        var pixels = new Color32[image.width * image.height];
        image.Dispose(pixels, image.width, image.height, -image.xPos, image.yPos);
        image.DrawTo(pixels, image.width, image.height, -image.xPos, image.yPos);
        return new Frame(request.Index, image.xPos, image.yPos, image.width, image.height, pixels);
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
