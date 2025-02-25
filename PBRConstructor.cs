// ---------------------------------------------------------
// This script is licensed under the MIT License.
// Copyright (c) 2024-2025 Tobias Löwdin
//
// Repository: https://github.com/Tolo00/Unity-PBR-Constructor
// Full License: https://github.com/Tolo00/Unity-PBR-Constructor/blob/main/LICENSE
//
// Permission is granted to use, copy, modify, merge, publish, distribute, 
// sublicense, and/or sell copies of this software under the MIT License.
// The above copyright notice and this permission notice shall be included 
// in all copies or substantial portions of the Software.
//
// This software is provided "as is", without warranty of any kind.
// ---------------------------------------------------------

using UnityEngine;
using UnityEditor;
using System.IO;

public class PBRConstructor : EditorWindow
{
    // Default Values
    private const float HeightMapStrength = 0.005f;
    private const float OcclusionStrength = 1f;

    // Properites
    private Texture2D colorMap;
    private Texture2D metallicMap;
    private Texture2D roughnessMap;
    private Texture2D normalMap;
    private Texture2D heightMap;
    private Texture2D AOMap;
    private Texture2D emissionMap;

    private Texture2D[] textures;
    private Texture2D packedMap;

    [MenuItem("Tools/PBR Constructor")]
    public static void ShowWindow()
    {
        GetWindow<PBRConstructor>("PBR Constructor");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        // Map Properties
        colorMap = (Texture2D)EditorGUILayout.ObjectField("Color", colorMap, typeof(Texture2D), false);
        metallicMap = (Texture2D)EditorGUILayout.ObjectField("Metallic", metallicMap, typeof(Texture2D), false);
        roughnessMap = (Texture2D)EditorGUILayout.ObjectField("Roughness", roughnessMap, typeof(Texture2D), false);
        normalMap = (Texture2D)EditorGUILayout.ObjectField("Normal", normalMap, typeof(Texture2D), false);
        heightMap = (Texture2D)EditorGUILayout.ObjectField("Height", heightMap, typeof(Texture2D), false);
        AOMap = (Texture2D)EditorGUILayout.ObjectField("Ambient Occlusion", AOMap, typeof(Texture2D), false);
        emissionMap = (Texture2D)EditorGUILayout.ObjectField("Emission", emissionMap, typeof(Texture2D), false);

        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        if (GUILayout.Button("Create PBR Material (URP)"))
        {
            textures = new Texture2D[] { colorMap, metallicMap, roughnessMap, normalMap, heightMap, AOMap };

            if (colorMap == null) {
                Debug.LogWarning("Textures required: Color");
                return;
            }

            // Get location of color map file - Used to start file explorer window in the same location
            string texturePath = AssetDatabase.GetAssetPath(colorMap);
            string absolutePath = Path.Combine(Application.dataPath, texturePath.Substring("Assets/".Length));
            string directoryPath = Path.GetDirectoryName(absolutePath);
            if (!Directory.Exists(directoryPath)) {
                directoryPath = Application.dataPath; // Default to "Assets/" if path is invalid
            }

            // Get location to save packed map texture in
            string textureOutputPath = EditorUtility.SaveFilePanel("Choose Save Location", directoryPath, "_PackedMap", "png");
            if (string.IsNullOrEmpty(textureOutputPath) || !textureOutputPath.StartsWith(Application.dataPath)) {
                Debug.LogError("Please select a location within the Assets folder.");
                return;
            }
            // Get location to save material in
            string materialOutputPath = EditorUtility.SaveFilePanel("Choose Save Location", directoryPath, "Material", "mat");
            if (string.IsNullOrEmpty(materialOutputPath) || !materialOutputPath.StartsWith(Application.dataPath)) {
                Debug.LogError("Please select a location within the Assets folder.");
                return;
            }
            
            // Combine and save packed map to texture
            packedMap = CombineIntoPackedMap(metallicMap, AOMap, roughnessMap);
            packedMap = SaveTexture(packedMap, textureOutputPath);

            // Configure normal map (if it exists)
            TextureImporter importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(normalMap)) as TextureImporter;
            if (importer != null) {
                importer.textureType = TextureImporterType.NormalMap;
                importer.normalmapFilter = TextureImporterNormalFilter.Sobel; // Optional: Set the normal map filter
                importer.SaveAndReimport();
            }

            Material PBRMaterial = CreateURPMaterial();
            SaveMaterial(PBRMaterial, materialOutputPath.Substring(materialOutputPath.IndexOf("Assets/")));

            // Cleanup
            textures = null;
            packedMap = null;
        }
    }


    private Material CreateURPMaterial() {
        // Create a new material (URP Lit shader)
        Shader urpShader = Shader.Find("Universal Render Pipeline/Lit");
        if (urpShader == null) {
            Debug.LogError("URP Lit Shader not found! Make sure URP is installed.");
            return null;
        }

        Material mat = new Material(urpShader);

        // Apply available textures
        if (colorMap != null) mat.SetTexture("_BaseMap", colorMap);
        
        mat.SetTexture("_MetallicGlossMap", packedMap);
        if (roughnessMap != null) mat.EnableKeyword("_METALLICSPECGLOSSMAP"); // Make sure smoothness uses packed map alpha

        if (normalMap != null) {
            mat.SetTexture("_BumpMap", normalMap);
            mat.EnableKeyword("_NORMALMAP"); // Enable normal map
        }

        if (heightMap != null) {
            mat.SetTexture("_ParallaxMap", heightMap);
            mat.SetFloat("_Parallax", HeightMapStrength); // Default parallax effect intensity
        }

        if (AOMap != null) {
            mat.SetTexture("_OcclusionMap", packedMap);
            mat.SetFloat("_OcclusionStrength", OcclusionStrength); // Default strength of AO effect
        }

        if (emissionMap != null) {
            mat.EnableKeyword("_EMISSION");
            mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            mat.SetTexture("_EmissionMap", emissionMap);
            mat.SetColor("_EmissionColor", Color.white * 2f); // Adjust: Default emission color
        }

        EditorUtility.SetDirty(mat);
        return mat;
    }

    private Texture2D CombineIntoPackedMap(Texture2D metallic, Texture2D AO, Texture2D roughness) {
        if (!HasSameSize(textures)) {
            throw new System.Exception("Textures need to be of same resolution.");
        }

        // Allow data to be read from file
        if (metallic != null) EnableReadWrite(metallic); 
        if (AO != null) EnableReadWrite(AO); 
        if (roughness != null) EnableReadWrite(roughness); 

        // Convert roughness to smoothness texture
        Texture2D smoothness = InvertTexture(roughness);
        
        // Create texture of same size
        Texture2D packedMap = new Texture2D(textures[0].width, textures[0].height);

        // Combine texture from each map into seperate channels (r = metallic, g = occlusion, b = none, a = smoothness)
        for (int y = 0; y < packedMap.height; y++) {
            for (int x = 0; x < packedMap.width; x++) {
                Color metallicPixel = metallic != null ? metallic.GetPixel(x, y) : new Color(0,0,0);        // Default: Black, not metallic
                Color AOPixel = AO != null ? AO.GetPixel(x, y) : new Color(255,255,255);                    // Default: White, no occlusion
                Color smoothnessPixel = smoothness != null ? smoothness.GetPixel(x, y) : new Color(0,0,0);  // Default: Gray, balance between rough and smooth

                Color newPixel = new Color(metallicPixel.r, AOPixel.r, 0, smoothnessPixel.r);
                packedMap.SetPixel(x, y, newPixel);
            }
        }

        return packedMap;
    }

    private Texture2D InvertTexture(Texture2D texture)
    {
        Texture2D invertedTexture = new Texture2D(texture.width, texture.height);

        for (int y = 0; y < texture.height; y++) {
            for (int x = 0; x < texture.width; x++) {
                Color pixel = texture.GetPixel(x, y);
                Color invertedPixel = new Color(1 - pixel.r, 1 - pixel.g, 1 - pixel.b, pixel.a);
                invertedTexture.SetPixel(x, y, invertedPixel);
            }
        }
        
        invertedTexture.Apply();
        return invertedTexture;
    }

    private void EnableReadWrite(Texture2D texture)
    {
        string path = AssetDatabase.GetAssetPath(texture);
        if (!string.IsNullOrEmpty(path))
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer != null)
            {
                importer.isReadable = true;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            else
            {
                throw new System.Exception("Could not get TextureImporter for path: " + path);
            }
        }
        else
        {
            throw new System.Exception("Texture is not an asset or has no valid path.");
        }
    }

    private bool HasSameSize(Texture2D[] textures) {
        int width = 0;
        int height = 0;
        foreach (Texture2D texture in textures) {
            if (texture == null) continue;
            if (width == 0) width = texture.width;
            if (height == 0) height = texture.height;
            
            if (texture.width != width || texture.height != height) {
                return false;
            }
        }
        return true;
    }

    private Texture2D SaveTexture(Texture2D texture, string localPath) {
        byte[] bytes = texture.EncodeToPNG();
        File.WriteAllBytes(localPath, bytes);
        AssetDatabase.Refresh();
 
        Debug.Log("Texture saved to: " + localPath);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(localPath.Substring(localPath.IndexOf("Assets/")));
    }

    private void SaveMaterial(Material material, string localPath) {
        AssetDatabase.CreateAsset(material, localPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("Material created at: " + localPath);
    }
}
