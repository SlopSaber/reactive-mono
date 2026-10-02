using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using B83.Image.GIF;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.Networking;

namespace Reactive.Components;

[PublicAPI]
public static class ImageLoader {
    public static IDictionary<string, CachedImage> CachedImages => images;

    private static readonly Dictionary<string, CachedImage> images = new();
    private static readonly HttpClient client = new();

    private static readonly Dictionary<string, SemaphoreSlim> semaphores = new();
    private static readonly object semaphoresLock = new();

    /// <summary>
    /// Loads an image from the provided location. Can be either a remote url, an assembly path or a file.
    /// </summary>
    /// <param name="location">A location to load the data from.</param>
    /// <param name="token">A cancellation token.</param>
    public static async Task<CachedImage?> LoadImage(string location, CancellationToken token) {
        var semaphore = GetSemaphore(location);
        await semaphore.WaitAsync(token);

        try {
            if (images.TryGetValue(location, out var image)) {
                return image;
            }

            if (IsRemote(location)) {
                if (IsPotentiallyAnimated(location)) {
                    image = await LoadAnyRemote(location, token);
                } else {
                    // If the image isn't animated, use an optimized request version
                    // to load directly to a native texture, avoiding managed allocations
                    image = await LoadStaticRemote(location, token);
                }
            } else {
                ImageRequest request;
                if (TryGetAssembly(location, out var asm, out var asmPath)) {
                    if (asm!.GetType() == typeof(ImageLoader).Assembly.GetType()) {
                        request = new ImageRequest(asm, asmPath, null, null, null);
                    } else {
                        using var stream = asm.GetManifestResourceStream(asmPath!);
                        if (stream == null) return null;
                        image = await LoadCustomAssemblyStream(stream, token);
                        if (image != null) {
                            _cachedImages[location] = image;
                            _imageUsage.TryAdd(location, 0);
                        }
                        return image;
                    }
                } else {
                    string path;
                    try {
                        path = Path.GetFullPath(location);
                    } catch (ArgumentException) {
                        return null;
                    } catch (NotSupportedException) {
                        return null;
                    } catch (PathTooLongException) {
                        return null;
                    }
                    request = new ImageRequest(null, null, path, null, null);
                }

                image = await LoadPreparedImage(request, token);
            }

            if (image != null) {
                images[location] = image;
            }

            return image;
        }
        finally {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Loads an image from the specified buffer.
    /// </summary>
    /// <param name="bytes">A buffer to load from.</param>
    /// <param name="token">A cancellation token.</param>
    public static async Task<CachedImage?> LoadImageFromBytes(byte[] bytes, CancellationToken token) {
        if (bytes == null) throw new ArgumentNullException("buffer");
        token.ThrowIfCancellationRequested();
        var ownedBytes = (byte[])bytes.Clone();
        return await LoadPreparedImage(new ImageRequest(null, null, null, ownedBytes, null), token);
    }

    public static void RemoveCached(string location) {
        images.Remove(location);
    }

    private static SemaphoreSlim GetSemaphore(string location) {
        lock (semaphoresLock) {
            if (!semaphores.TryGetValue(location, out var semaphore)) {
                semaphore = new(1, 1);
                semaphores[location] = semaphore;
            }

            return semaphore;
        }
    }

    #region Remote

    private static readonly char[] _queryOrFragmentChars = ['?', '#'];

    private static bool IsRemote(string location) {
        return location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
    
    private static bool IsPotentiallyAnimated(string location) {
        if (string.IsNullOrWhiteSpace(location)) {
            return false;
        }
        
        var endIndex = location.IndexOfAny(_queryOrFragmentChars);
        if (endIndex == -1) {
            endIndex = location.Length;
        }

        if (endIndex < 4) {
            return false;
        }

        return string.Compare(location, endIndex - 4, ".gif", 0, 4, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static async Task<CachedImage?> LoadStaticRemote(string location, CancellationToken token) {
        using var req = UnityWebRequestTexture.GetTexture(location);
        req.timeout = 20;
        var operation = req.SendWebRequest();
        try {
            while (!operation.isDone) {
                await Task.Delay(50, token);
            }
            token.ThrowIfCancellationRequested();
        } catch (OperationCanceledException) {
            req.Abort();
            throw;
        }

        if (req.result != UnityWebRequest.Result.Success) {
            if (req.responseCode != 429 &&
                req.error?.IndexOf("Access denied", StringComparison.OrdinalIgnoreCase) >= 0) {
                var recovered = await TryLoadStaticRemoteWithHttpClient(location, token);
                if (recovered != null) {
                    return recovered;
                }
            }

            Debug.LogWarning($"Failed to load remote image [{location}]: {req.error} " +
                             $"(result: {req.result}, HTTP: {req.responseCode})");
            return null;
        }

        try {
            var tex = DownloadHandlerTexture.GetContent(req);
            var sprite = SpriteUtils.CreateSprite(tex);
            return sprite != null ? new CachedImage(sprite) : null;
        } catch (Exception ex) {
            Debug.LogWarning($"Failed to decode remote image [{location}]: {ex.Message}");
            return null;
        }
    }

    private static async Task<CachedImage?> TryLoadStaticRemoteWithHttpClient(string location, CancellationToken token) {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try {
            using var response = await client.GetAsync(location, HttpCompletionOption.ResponseContentRead, timeout.Token);
            if (!response.IsSuccessStatusCode) {
                Debug.LogWarning($"Remote image fallback [{location}] returned HTTP {(int)response.StatusCode}");
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            token.ThrowIfCancellationRequested();
            var sprite = SpriteUtils.CreateSprite(bytes);
            return sprite != null ? new CachedImage(sprite) : null;
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Debug.LogWarning($"Remote image fallback [{location}] failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<CachedImage?> LoadAnyRemote(string location, CancellationToken token) {
        using var stream = await client.GetStreamAsync(location);
        return await LoadPreparedImage(new ImageRequest(null, null, null, null, stream), token);
    }

    #endregion

    #region Assembly

    private static bool TryGetAssembly(string location, out Assembly? assembly, out string? path) {
        var parameters = location.Split(':');

        switch (parameters.Length) {
            case 1:
                path = parameters[0];
                assembly = Assembly.Load(path.Substring(0, path.IndexOf('.')));
                return true;
            case 2:
                path = parameters[1];
                assembly = Assembly.Load(parameters[0]);
                return true;
            default:
                assembly = null;
                path = null;
                return false;
        }
    }

    #endregion

    #region Preparation

    private static async Task<CachedImage?> LoadCustomAssemblyStream(Stream stream, CancellationToken token) {
        var gif = await Task.Run(() => {
            try {
                var reader = new BinaryReader(stream);
                return new GIFLoader().Load(reader);
            } catch (Exception ex) {
                Debug.LogError($"Failed to create a GIF: {ex}");
                return null;
            }
        }, token);
        if (gif != null) return new CachedImage(gif);
        stream.Position = 0;
        try {
            var contentSize = (int)stream.Length;
            var buffer = new byte[contentSize];
            var totalRead = 0;
            while (totalRead < contentSize) {
                var read = await stream.ReadAsync(buffer, totalRead, contentSize - totalRead, token);
                if (read == 0) throw new EndOfStreamException("Unexpected end of stream before expected content size.");
                totalRead += read;
            }
            return new CachedImage(SpriteUtils.CreateSprite(buffer)!);
        } catch (Exception ex) {
            Debug.LogWarning($"Failed to create a static image: {ex.Message}");
            return null;
        }
    }

    private sealed class ImageRequest(
        Assembly? assembly, string? resourcePath, string? filePath, byte[]? bytes, Stream? stream
    ) {
        public readonly Assembly? Assembly = assembly;
        public readonly string? ResourcePath = resourcePath;
        public readonly string? FilePath = filePath;
        public readonly byte[]? Bytes = bytes;
        public readonly Stream? Stream = stream;
    }

    private sealed class PreparedImage(byte[] bytes, GIFImage? gif, Exception? gifError, bool readFailed = false) {
        public readonly byte[] Bytes = bytes;
        public readonly GIFImage? Gif = gif;
        public readonly Exception? GifError = gifError;
        public readonly bool ReadFailed = readFailed;
    }

    private static PreparedImage? PrepareImage(object? state) {
        var request = (ImageRequest)state!;
        var bytes = request.Bytes;
        if (bytes == null) {
            using var stream = request.Stream ?? (request.Assembly != null
                ? request.Assembly.GetManifestResourceStream(request.ResourcePath!)
                : File.Exists(request.FilePath) ? File.OpenRead(request.FilePath!) : null);
            if (stream == null) return null;
            using var buffer = new MemoryStream();
            try {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            } catch (Exception ex) {
                return new PreparedImage([], null, ex, true);
            }
        }

        try {
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream);
            return new PreparedImage(bytes, new GIFLoader().Load(reader), null);
        } catch (Exception ex) {
            return new PreparedImage(bytes, null, ex);
        }
    }

    private static async Task<CachedImage?> LoadPreparedImage(ImageRequest request, CancellationToken token) {
        var preparation = Task.Factory.StartNew(PrepareImage, request, token,
            TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        var prepared = await preparation;
        token.ThrowIfCancellationRequested();
        if (prepared == null) return null;
        if (prepared.GifError != null) {
            Debug.LogError($"Failed to create a GIF: {prepared.GifError}");
            token.ThrowIfCancellationRequested();
        }
        if (prepared.ReadFailed) {
            Debug.LogWarning($"Failed to create a static image: {prepared.GifError!.Message}");
            return null;
        }
        if (prepared.Gif != null) return new CachedImage(prepared.Gif);

        try {
            var sprite = SpriteUtils.CreateSprite(prepared.Bytes);
            return new CachedImage(sprite!);
        } catch (Exception ex) {
            Debug.LogWarning($"Failed to create a static image: {ex.Message}");
            return null;
        }
    }

    #endregion
}
