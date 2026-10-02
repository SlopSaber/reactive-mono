using System;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEngine;

namespace Reactive.Components;

[PublicAPI]
public class ImageLoaderModule(ISpriteRenderer renderer) : IReactiveModule {
    public CachedImage? LoadedImage;

    private CancellationTokenSource? _tokenSource;
    private Sprite? _initialSprite;
    private string? _url;
    private Task? _loadTask;
    private long _revision;
    private bool _bound = true;

    public void StopLoading() {
        _revision++;
        var source = _tokenSource;
        _tokenSource = null;
        source?.Cancel();
        source?.Dispose();
        _url = null;
        LoadedImage = null;
    }

    public void LoadRemote(string url, Action? onStart, Action<bool>? onFinish) {
        if (!_bound || !IsRendererAlive() || url == _url) {
            return;
        }

        StopLoading();

        LoadedImage = null;
        _initialSprite = renderer.Sprite;
        _url = url;
        var source = new CancellationTokenSource();
        _tokenSource = source;
        _loadTask = LoadRemoteInternal(url, onStart, onFinish, source, _revision);
    }

    private async Task LoadRemoteInternal(
        string url,
        Action? onStart,
        Action<bool>? onFinish,
        CancellationTokenSource source,
        long revision
    ) {
        var token = source.Token;
        try {
            onStart?.Invoke();
            if (!IsCurrent(revision, token)) return;

            var image = await ImageLoader.LoadImage(url, token);
            if (!IsCurrent(revision, token)) return;

            if (image == null) {
                Debug.LogError("Remote picture has failed to load");
                if (!IsCurrent(revision, token)) return;
                onFinish?.Invoke(false);
                return;
            }

            if (_initialSprite == renderer.Sprite) {
                renderer.Sprite = image.Sprite;
            }
            if (!IsCurrent(revision, token)) return;
            LoadedImage = image;

            onFinish?.Invoke(true);
        } catch (OperationCanceledException) {
            // do nothing
        } catch (Exception ex) {
            if (!IsCurrent(revision, token)) return;
            Debug.LogError($"Image loading has failed: {ex}");
            if (!IsCurrent(revision, token)) return;
            onFinish?.Invoke(false);
        } finally {
            if (ReferenceEquals(_tokenSource, source)) _tokenSource = null;
            source.Dispose();
        }
    }

    private bool IsCurrent(long revision, CancellationToken token) {
        return _bound && revision == _revision && !token.IsCancellationRequested && IsRendererAlive();
    }

    private bool IsRendererAlive() {
        return renderer is not ReactiveComponent component ||
            (!component.IsDestroyed && (!component.IsInitialized || component.Content));
    }

    public void OnUpdate() {
        if (_bound && IsRendererAlive() && (LoadedImage?.IsAnimated ?? false)) {
            LoadedImage.ManualUpdate(Time.deltaTime);
        }
    }

    public void OnBind() { _bound = true; }

    public void OnUnbind() {
        _bound = false;
        StopLoading();
    }
}
