using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

internal static class JoinedGifFrame {
    private sealed class Request(GIFImageBlock image, Color32[] pixels, int width, int height) {
        internal readonly GIFImageBlock Image = image;
        internal readonly Color32[] Pixels = pixels;
        internal readonly int Width = width;
        internal readonly int Height = height;
    }

    internal static bool TryApply(GIFImageBlock image, Color32[] pixels, int width, int height, out bool drawFailed) {
        drawFailed = false;
        var request = new Request(image, pixels, width, height);
        Task<bool>? task = null;
        ExceptionDispatchInfo? dispatchError = null;
        try {
            if (ExecutionContext.IsFlowSuppressed()) {
                task = Start(request);
            } else {
                using (ExecutionContext.SuppressFlow()) {
                    task = Start(request);
                }
            }
        } catch (Exception error) {
            if (task == null) return false;
            dispatchError = ExceptionDispatchInfo.Capture(error);
        }

        ExceptionDispatchInfo? interruption = null;
        while (true) {
            try {
                drawFailed = task!.GetAwaiter().GetResult();
                break;
            } catch (ThreadInterruptedException error) {
                if (task!.IsCompleted) throw;
                // Interruption cannot return ownership while the worker still writes these pixels.
                interruption ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        dispatchError?.Throw();
        interruption?.Throw();
        return true;
    }

    private static Task<bool> Start(Request request) =>
        Task.Factory.StartNew(Compose, request, CancellationToken.None,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    private static bool Compose(object? state) {
        var request = (Request)state!;
        request.Image.Dispose(request.Pixels, request.Width, request.Height, 0, 0);
        try {
            request.Image.DrawTo(request.Pixels, request.Width, request.Height, 0, 0);
        } catch (Exception) {
            return true;
        }
        return false;
    }
}
