using System;
using B83.Image.GIF;
using UnityEngine;

namespace Reactive.Components;

public class CachedImage {
    #region Constructors

    public readonly Sprite Sprite;
    public readonly bool IsAnimated;

    private readonly GIFImage? _gifImage;
    private readonly RenderTexture? _renderTexture;
    private Color32[]? _colors;
    private PreparedGifFrames? _preparedFrames;
    private IncrementalGifFrames? _incrementalFrames;
    private PreparedGifPatch? _patch;
    private PreparedGifRuns? _runs;
    private int _currentIndex;
    private float _deltaAccumulated;
    private bool _hasLooped;

    internal CachedImage(GIFImage gifImage) : this(gifImage, null) { }

    internal CachedImage(GIFImage gifImage, PreparedGifFrames? preparedFrames) : this(gifImage, preparedFrames, null) { }

    internal CachedImage(GIFImage gifImage, PreparedGifFrames? preparedFrames, IncrementalGifFrames? incrementalFrames)
        : this(gifImage, preparedFrames, incrementalFrames, null) { }

    internal CachedImage(GIFImage gifImage, PreparedGifFrames? preparedFrames, IncrementalGifFrames? incrementalFrames, PreparedGifPatch? patch)
        : this(gifImage, preparedFrames, incrementalFrames, patch, null) { }

    internal CachedImage(GIFImage gifImage, PreparedGifFrames? preparedFrames, IncrementalGifFrames? incrementalFrames, PreparedGifPatch? patch, PreparedGifRuns? runs) {
        IsAnimated = true;

        _renderTexture = new RenderTexture(gifImage.screen.width, gifImage.screen.height, 0, RenderTextureFormat.Default, 10);
        _renderTexture.Create();
        
        Sprite = SpriteUtils.CreateSprite(_renderTexture)!;

        _colors = Sprite.texture.GetPixels32();
        _colors.Initialize();

        _gifImage = gifImage;
        _preparedFrames = preparedFrames;
        _incrementalFrames = incrementalFrames;
        _patch = patch;
        _runs = runs;
    }

    internal CachedImage(Sprite sprite) {
        IsAnimated = false;
        Sprite = sprite;
    }

    ~CachedImage() {
        _renderTexture?.Release();
    }

    #endregion

    #region Playback

    public void ManualUpdate(float timeDelta) {
        if (!IsAnimated || _gifImage!.imageData.Count == 0) {
            return;
        }

        var originalTexture = Sprite.texture;
        var frame = _gifImage.imageData[_currentIndex];

        if (_deltaAccumulated == 0) {
            bool drawFailed = false;
            if (!TryUsePreparedFrame(originalTexture)) {
                if (!TryUseIncrementalFrame(originalTexture, out drawFailed) && !TryUsePatch(originalTexture) && !TryUseRuns(originalTexture, out drawFailed)) {
                    frame.Dispose(_colors!, originalTexture.width, originalTexture.height);
                    try {
                        frame.DrawTo(_colors!, _renderTexture!.width, _renderTexture.height);
                    } catch (Exception) {
                        originalTexture.SetPixels32(_colors);
                        originalTexture.Apply();
                        Graphics.Blit(originalTexture, _renderTexture);
                        return;
                    }
                }
            }

            originalTexture.SetPixels32(_colors);
            originalTexture.Apply();
            Graphics.Blit(originalTexture, _renderTexture);
            if (drawFailed) return;
        }

        _deltaAccumulated += timeDelta;
        if (_deltaAccumulated >= frame.graphicControl.fdelay) {
            _deltaAccumulated = 0;
            if (_currentIndex < _gifImage.imageData.Count - 1) {
                _currentIndex++;
            } else {
                _currentIndex = 0;
                _hasLooped = true;
            }
        }
        if (_incrementalFrames != null) {
            int nextIndex = _deltaAccumulated == 0 ? _currentIndex : (_currentIndex + 1) % _gifImage.imageData.Count;
            _incrementalFrames.Prefetch(nextIndex);
        }
        if (_patch != null) {
            int nextIndex = _deltaAccumulated == 0 ? _currentIndex : (_currentIndex + 1) % _gifImage.imageData.Count;
            _patch.Prefetch(nextIndex);
        }
        if (_runs != null) {
            int nextIndex = _deltaAccumulated == 0 ? _currentIndex : (_currentIndex + 1) % _gifImage.imageData.Count;
            _runs.Prefetch(nextIndex);
        }
    }

    private bool TryUseRuns(Texture2D texture, out bool drawFailed) {
        drawFailed = false;
        var runs = _runs;
        if (runs == null) return false;
        try {
            if (texture.width == runs.Width && texture.height == runs.Height &&
                _renderTexture!.width == runs.Width && _renderTexture.height == runs.Height &&
                _gifImage!.imageData.Count == runs.FrameCount && _colors!.LongLength == (long)runs.Width * runs.Height) {
                if (runs.OriginalFrame(_currentIndex)) return false;
                if (!runs.TryTake(_currentIndex, out var frame)) {
                    runs.Retire();
                    _runs = null;
                    return false;
                }
                for (int i = 0; i < frame!.Count; i++) {
                    var run = frame.Chunks[i / PreparedGifRuns.RunsPerChunk][i % PreparedGifRuns.RunsPerChunk];
                    var tile = frame.Tiles[run.Color];
                    int destination = run.Destination;
                    int remaining = run.Length;
                    while (remaining > 0) {
                        int count = Math.Min(remaining, tile.Length);
                        Array.Copy(tile, 0, _colors, destination, count);
                        destination += count;
                        remaining -= count;
                    }
                }
                drawFailed = frame.DrawFailed;
                if (drawFailed) {
                    runs.Retire();
                    _runs = null;
                }
                return true;
            }
        } catch (Exception) {
        }
        runs.Retire();
        _runs = null;
        return false;
    }

    private bool TryUsePatch(Texture2D texture) {
        var patch = _patch;
        if (patch == null) return false;
        try {
            if (texture.width == patch.Width && texture.height == patch.Height &&
                _renderTexture!.width == patch.Width && _renderTexture.height == patch.Height &&
                _gifImage!.imageData.Count == patch.FrameCount && _colors!.LongLength == (long)patch.Width * patch.Height) {
                if (patch.OriginalFrame(_currentIndex)) return false;
                if (!patch.TryTake(_currentIndex, out var frame)) {
                    patch.Retire();
                    _patch = null;
                    return false;
                }
                for (int y = 0; y < frame!.Height; y++) {
                    int destination = frame.X + (patch.Height - frame.Y - frame.Height + y) * patch.Width;
                    int source = y * frame.Width;
                    var mask = frame.WriteMask;
                    if (mask == null) {
                        Array.Copy(frame.Pixels, source, _colors, destination, frame.Width);
                        continue;
                    }
                    int x = 0;
                    while (x < frame.Width) {
                        while (x < frame.Width && !mask[source + x]) x++;
                        int start = x;
                        while (x < frame.Width && mask[source + x]) x++;
                        if (x > start)
                            Array.Copy(frame.Pixels, source + start, _colors, destination + start, x - start);
                    }
                }
                return true;
            }
        } catch (Exception) {
        }
        patch.Retire();
        _patch = null;
        return false;
    }

    private bool TryUseIncrementalFrame(Texture2D texture, out bool drawFailed) {
        drawFailed = false;
        var frames = _incrementalFrames;
        if (frames == null) return false;
        bool compatible;
        try {
            compatible = texture.width == frames.Width && texture.height == frames.Height &&
                _renderTexture!.width == frames.Width && _renderTexture.height == frames.Height &&
                _gifImage!.imageData.Count == frames.FrameCount;
        } catch (Exception) {
            compatible = false;
        }
        if (!compatible || !frames.TryTake(_currentIndex, out var frame)) {
            frames.Retire();
            _incrementalFrames = null;
            return false;
        }
        _colors = frame!.Pixels;
        if (frame.DisposeError != null) {
            frames.Retire();
            _incrementalFrames = null;
            frame.DisposeError.Throw();
        }
        drawFailed = frame.DrawFailed;
        if (drawFailed) {
            frames.Retire();
            _incrementalFrames = null;
        }
        return true;
    }

    private bool TryUsePreparedFrame(Texture2D texture) {
        if (_preparedFrames == null) return false;
        try {
            if (texture.width == _preparedFrames.Width && texture.height == _preparedFrames.Height &&
                _renderTexture!.width == _preparedFrames.Width && _renderTexture.height == _preparedFrames.Height &&
                _gifImage!.imageData.Count == _preparedFrames.FrameCount) {
                _colors = _preparedFrames.GetFrame(_currentIndex, _hasLooped);
                return true;
            }
        } catch (Exception) {
            // Preserve the original disposal/draw path when native readiness cannot be probed.
        }

        _preparedFrames = null;
        return false;
    }

    #endregion
}
