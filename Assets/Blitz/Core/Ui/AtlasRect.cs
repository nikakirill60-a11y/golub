namespace BlitzLegacy.Core.Ui
{
    /// <summary>
    /// Godot AtlasTexture regions are Rect2 values measured from the texture's
    /// top-left corner. Unity's Sprite.Create takes a rect measured from the
    /// bottom-left, so every region needs its Y flipped exactly once.
    /// </summary>
    public static class AtlasRect
    {
        public static float FlipY(float textureHeight, float regionY, float regionHeight)
        {
            return textureHeight - (regionY + regionHeight);
        }
    }
}
