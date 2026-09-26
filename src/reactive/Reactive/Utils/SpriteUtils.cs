using System;
using JetBrains.Annotations;
using UnityEngine; 

namespace Reactive {
    /// <summary>
    /// A class for managing sprites and textures.
    /// </summary>
    [PublicAPI]
    public static class SpriteUtils {
        #region Sprites

        public static Texture2D CreateTexture(RenderTexture rt) {
            var active = RenderTexture.active;
            var texture2D = new Texture2D(rt.width, rt.height);
            try {
                RenderTexture.active = rt;
                texture2D.wrapMode = rt.wrapMode;
                texture2D.ReadPixels(new Rect(0.0f, 0.0f, (float) texture2D.width, (float) texture2D.height), 0, 0);
                texture2D.Apply();
                return texture2D;
            } catch {
                UnityEngine.Object.Destroy(texture2D);
                throw;
            } finally {
                RenderTexture.active = active;
            }
        }
        
        public static Texture2D? CreateTexture(byte[] bytes) {
            if (bytes.Length == 0) return null;
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, false);
            
            try {
                if (!texture.LoadImage(bytes)) {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }
            } catch (Exception ex) {
                UnityEngine.Object.Destroy(texture);
                Debug.LogError($"Failed to create a texture:\n{ex}");
                return null;
            }
            
            return texture;
        }

        public static Sprite? CreateSprite(byte[] image) {
            return CreateTexture(image) is { } texture ? CreateSprite(texture) : null;
        }

        public static Sprite? CreateSprite(Texture2D texture) {
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(texture.width / 2f, texture.height / 2f)
            );
        }
        
        public static Sprite? CreateSprite(RenderTexture rendTexture) {
            var texture = CreateTexture(rendTexture);

            return CreateSprite(texture);
        }

        #endregion
    }
}
