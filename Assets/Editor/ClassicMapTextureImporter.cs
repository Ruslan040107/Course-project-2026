using System;
using UnityEditor;

// Local map textures include decorative signs and large foliage cards viewed from a distance.
public class ClassicMapTextureImporter : AssetPostprocessor
{
    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith("Assets/Models/Maps/", StringComparison.Ordinal) ||
            !assetPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
        var importer = (TextureImporter)assetImporter;
        importer.maxTextureSize = 1024;
        importer.alphaIsTransparency = System.IO.Path.GetFileName(assetPath).Contains("_C_");
    }
}
