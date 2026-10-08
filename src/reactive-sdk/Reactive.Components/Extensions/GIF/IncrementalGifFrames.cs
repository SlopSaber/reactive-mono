using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

internal sealed class IncrementalGifFrames {
    private const long StorageBudget = 32L * 1024 * 1024;
    private readonly GIFImageBlock[] _frames;
    private readonly int _frameCount;
    private GIFImage? _sourceGif;
    private Frame? _initial;
    private Task<Frame>? _pending;
    private Color32[]? _nextCanvas;
    private bool _retired;

    private IncrementalGifFrames(int width, int height, GIFImageBlock[] frames, Frame initial) {
        Width = width;
        Height = height;
        _frames = frames;
        _frameCount = frames.Length;
        _initial = initial;
    }

    private IncrementalGifFrames(int width, int height, GIFImage sourceGif, Frame initial)
        : this(width, height, Array.Empty<GIFImageBlock>(), initial) {
        _sourceGif = sourceGif;
        _frameCount = sourceGif.imageData.Count;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal int FrameCount => _frameCount;

    internal sealed class Frame(int index, Color32[] pixels, Color32[] nextCanvas,
        ExceptionDispatchInfo? disposeError, bool drawFailed) {
        internal readonly int Index = index;
        internal readonly Color32[] Pixels = pixels;
        internal readonly Color32[] NextCanvas = nextCanvas;
        internal readonly ExceptionDispatchInfo? DisposeError = disposeError;
        internal readonly bool DrawFailed = drawFailed;
    }

    private sealed class Request(GIFImageBlock image, Color32[] canvas, int width, int height, int index) {
        internal readonly GIFImageBlock Image = image;
        internal readonly Color32[] Canvas = canvas;
        internal readonly int Width = width;
        internal readonly int Height = height;
        internal readonly int Index = index;
    }

    internal static IncrementalGifFrames? TryCreate(GIFImage gif) {
        try {
            int width = gif.screen.width;
            int height = gif.screen.height;
            long pixels = (long)width * height;
            long used = pixels * 12;
            int count = gif.imageData.Count;
            if (width <= 0 || height <= 0 || count <= 0 || pixels > int.MaxValue || used >= StorageBudget)
                return null;

            var parent = new GIFImage { screen = gif.screen, BackgroundTransparent = gif.BackgroundTransparent };
            if (gif.screen.globalColorTable != null) {
                used += gif.screen.globalColorTable.LongLength * 4 + 32;
                if (used > StorageBudget) return null;
                parent.screen.globalColorTable = (Color32[])gif.screen.globalColorTable.Clone();
            }

            used += (long)count * 8 + 32;
            if (used > StorageBudget) return null;
            var frames = new GIFImageBlock[count];
            for (int i = 0; i < count; i++) {
                if (gif.imageData[i] is not GIFImageBlock source || source.GetType() != typeof(GIFImageBlock) ||
                    source.Parent != gif || source.graphicControl == null ||
                    source.graphicControl.GetType() != typeof(GIFGraphicControlExt) || source.data == null ||
                    source.xPos + source.width > width || source.yPos + source.height > height)
                    return null;

                used += source.data.Count + 192L + (source.usedColorTable?.LongLength ?? 0) * 4;
                if (used > StorageBudget) return null;
                var frame = source.CloneForPreparation();
                frame.Parent = parent;
                frame.graphicControl = new GIFGraphicControlExt(parent) {
                    flags = source.graphicControl.flags,
                    delay = source.graphicControl.delay,
                    transparentColorIndex = source.graphicControl.transparentColorIndex
                };
                frame.data = new List<byte>(source.data);
                frame.usedColorTable = source.usedColorTable == null ? null : (Color32[])source.usedColorTable.Clone();
                frame.colorTable = frame.usedColorTable;
                frames[i] = frame;
            }

            var initial = Compose(new Request(frames[0], new Color32[(int)pixels], width, height, 0));
            return new IncrementalGifFrames(width, height, frames, initial);
        } catch (Exception) {
            return null;
        }
    }

    internal static IncrementalGifFrames? TryCreateForOwnedImage(GIFImage gif) {
        try {
            int width = gif.screen.width;
            int height = gif.screen.height;
            long pixels = (long)width * height;
            int count = gif.imageData.Count;
            if (width <= 0 || height <= 0 || count <= 0 || pixels > int.MaxValue)
                return TryCreate(gif);

            long globalPalette = (gif.screen.globalColorTable?.LongLength ?? 0) * 4;
            long canvasStorage = pixels * 12 + 96;
            long wholeStorage = pixels * 12 + (gif.screen.globalColorTable == null ? 0 : globalPalette + 32) + (long)count * 8 + 32;
            for (int i = 0; i < count; i++) {
                if (gif.imageData[i] is not GIFImageBlock source || source.GetType() != typeof(GIFImageBlock) ||
                    source.Parent != gif || source.graphicControl == null ||
                    source.graphicControl.GetType() != typeof(GIFGraphicControlExt) || source.data == null ||
                    source.data.GetType() != typeof(List<byte>) ||
                    source.xPos + source.width > width || source.yPos + source.height > height ||
                    (long)source.width * source.height < 16384)
                    return TryCreate(gif);

                long palette = (source.usedColorTable?.LongLength ?? 0) * 4;
                long packetStorage = source.data.Count + palette + globalPalette + 1024;
                if (canvasStorage + packetStorage * 2 > StorageBudget)
                    return TryCreate(gif);
                wholeStorage += source.data.Count + palette + 192;
            }

            if (wholeStorage <= StorageBudget) return TryCreate(gif);

            var initialImage = CaptureCurrentFrame(gif, 0, width, height);
            var initial = Compose(new Request(initialImage, new Color32[(int)pixels], width, height, 0));
            return new IncrementalGifFrames(width, height, gif, initial);
        } catch (Exception) {
            return null;
        }
    }

    private static GIFImageBlock CaptureCurrentFrame(GIFImage gif, int index, int width, int height) {
        if (gif.screen.width != width || gif.screen.height != height ||
            gif.imageData[index] is not GIFImageBlock source || source.GetType() != typeof(GIFImageBlock) ||
            source.Parent != gif || source.graphicControl == null ||
            source.graphicControl.GetType() != typeof(GIFGraphicControlExt) || source.data == null ||
            source.data.GetType() != typeof(List<byte>) ||
            source.xPos + source.width > width || source.yPos + source.height > height ||
            (long)source.width * source.height < 16384)
            throw new InvalidOperationException();

        long globalPalette = (gif.screen.globalColorTable?.LongLength ?? 0) * 4;
        long packetStorage = source.data.Count + (source.usedColorTable?.LongLength ?? 0) * 4 + globalPalette + 1024;
        if ((long)width * height * 12 + 96 + packetStorage * 2 > StorageBudget)
            throw new InvalidOperationException();

        var parent = new GIFImage { screen = gif.screen, BackgroundTransparent = gif.BackgroundTransparent };
        if (gif.screen.globalColorTable != null)
            parent.screen.globalColorTable = (Color32[])gif.screen.globalColorTable.Clone();
        var frame = source.CloneForPreparation();
        frame.Parent = parent;
        frame.graphicControl = new GIFGraphicControlExt(parent) {
            flags = source.graphicControl.flags,
            delay = source.graphicControl.delay,
            transparentColorIndex = source.graphicControl.transparentColorIndex
        };
        frame.data = new List<byte>(source.data);
        frame.usedColorTable = source.usedColorTable == null ? null : (Color32[])source.usedColorTable.Clone();
        frame.colorTable = frame.usedColorTable;
        return frame;
    }

    private static Frame Compose(object? state) {
        var request = (Request)state!;
        ExceptionDispatchInfo? disposeError = null;
        bool drawFailed = false;
        try {
            request.Image.Dispose(request.Canvas, request.Width, request.Height, 0, 0);
        } catch (Exception error) {
            disposeError = ExceptionDispatchInfo.Capture(error);
        }
        if (disposeError == null) {
            try {
                request.Image.DrawTo(request.Canvas, request.Width, request.Height, 0, 0);
            } catch (Exception) {
                drawFailed = true;
            }
        }

        // Published pixels must never become the next worker's writable canvas.
        return new Frame(request.Index, request.Canvas, (Color32[])request.Canvas.Clone(), disposeError, drawFailed);
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
        if (frame.Index != index) {
            Retire();
            frame = null;
            return false;
        }
        _nextCanvas = frame.NextCanvas;
        return true;
    }

    internal void Prefetch(int index) {
        if (_retired || _pending != null || _nextCanvas == null) return;
        try {
            if (_sourceGif != null && _sourceGif.imageData.Count != _frameCount) {
                Retire();
                return;
            }
            var image = _sourceGif == null ? _frames[index] : CaptureCurrentFrame(_sourceGif, index, Width, Height);
            var request = new Request(image, _nextCanvas, Width, Height, index);
            if (ExecutionContext.IsFlowSuppressed()) {
                Start(request);
            } else {
                using (ExecutionContext.SuppressFlow()) {
                    Start(request);
                }
            }
            _nextCanvas = null;
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
        _sourceGif = null;
        _initial = null;
        _nextCanvas = null;
        _pending = null;
    }
}
