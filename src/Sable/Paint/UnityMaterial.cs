using System.Globalization;
using System.Text.RegularExpressions;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Writes a texture as a Unity URP Lit material: <c>&lt;name&gt;.png</c> (the colour), <c>&lt;name&gt;_mask.png</c> (the
/// smoothness, when there's a mask) in the Lit shader's Metallic Alpha layout (R metallic, A smoothness), and
/// <c>&lt;name&gt;.mat</c> using them, each with a .meta so the material can point at the images by GUID. Images are
/// imported for pixel art: point filtering, no mipmaps, uncompressed, no resizing to a power of two.
/// </summary>
/// <remarks>
/// Exporting again over an earlier export keeps every GUID already in the .meta files, so whatever uses the material
/// in Unity keeps working; in an image's .meta only the import settings above are rewritten, and a .mat's .meta is
/// left as it is. The .mat itself is written anew.
/// </remarks>
public static class UnityMaterial
{
    /// <summary>URP's Lit shader (Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader).</summary>
    public const string LitShaderGuid = "933532a4fcc9baf4fa0491de14d08ed7";

    public sealed record Result(string MaterialPath, string AlbedoPath, string? MaskPath, string MaterialGuid, string AlbedoGuid, string? MaskGuid);

    /// <summary>The name of the smoothness image beside the material.</summary>
    public static string MaskPathFor(string materialPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(materialPath))!, Path.GetFileNameWithoutExtension(materialPath) + "_mask.png");

    /// <summary>
    /// Writes the material at <paramref name="materialPath"/> and its images beside it.
    /// </summary>
    /// <param name="albedo">The colour as saved (flattened, edge padded).</param>
    /// <param name="smoothness">The smoothness map's pixels (smoothness in alpha), or null for a texture without a
    /// mask: the material is then matte.</param>
    public static Result Export(string materialPath, int width, int height, Color[] albedo, Color[]? smoothness)
    {
        materialPath = Path.GetFullPath(materialPath);
        string folder = Path.GetDirectoryName(materialPath)!;
        string name = Path.GetFileNameWithoutExtension(materialPath);
        Directory.CreateDirectory(folder);

        string albedoPath = Path.Combine(folder, name + ".png");
        File.WriteAllBytes(albedoPath, LayerFile.EncodePng(albedo, width, height));
        string albedoGuid = WriteTextureMeta(albedoPath, linear: false, Math.Max(width, height));

        string? maskPath = null, maskGuid = null;
        if (smoothness != null)
        {
            maskPath = MaskPathFor(materialPath);
            File.WriteAllBytes(maskPath, LayerFile.EncodePng(smoothness, width, height));
            maskGuid = WriteTextureMeta(maskPath, linear: true, Math.Max(width, height));
        }

        string materialGuid = GuidIn(materialPath + ".meta") ?? NewGuid();
        if (!File.Exists(materialPath + ".meta")) WriteText(materialPath + ".meta", MaterialMeta(materialGuid));
        WriteText(materialPath, Material(name, albedoGuid, maskGuid));
        return new Result(materialPath, albedoPath, maskPath, materialGuid, albedoGuid, maskGuid);
    }

    /// <summary>The <c>guid:</c> a .meta file gives its asset, or null when there's no such file (or no GUID in it).</summary>
    public static string? GuidIn(string metaPath)
    {
        if (!File.Exists(metaPath)) return null;
        var match = Regex.Match(File.ReadAllText(metaPath), @"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static string NewGuid() => Guid.NewGuid().ToString("N");

    /// <summary>Unity's own line endings (LF), no byte order mark.</summary>
    private static void WriteText(string path, string text) => File.WriteAllText(path, text.Replace("\r\n", "\n"));

    /// <summary>
    /// Writes an image's .meta (or updates the one there) for pixel art and returns its GUID. <paramref name="linear"/>
    /// is for data rather than colour (the smoothness map): no sRGB conversion, and alpha isn't transparency.
    /// </summary>
    private static string WriteTextureMeta(string imagePath, bool linear, int size)
    {
        string metaPath = imagePath + ".meta";
        int maxSize = 2048;
        while (maxSize < size && maxSize < 16384) maxSize *= 2;
        if (GuidIn(metaPath) is not { } guid)
        {
            guid = NewGuid();
            WriteText(metaPath, TextureMeta(guid, linear, maxSize));
            return guid;
        }

        // Only the settings this export is about; everything else set in Unity stays.
        string text = File.ReadAllText(metaPath);
        string Set(string input, string key, string value) =>
            Regex.Replace(input, $@"^(\s*{key}:) *-?\d+ *$", $"$1 {value}", RegexOptions.Multiline);
        text = Set(text, "filterMode", "0");
        text = Set(text, "enableMipMap", "0");
        text = Set(text, "textureCompression", "0");
        text = Set(text, "nPOTScale", "0");
        text = Set(text, "textureType", "0");
        text = Set(text, "sRGBTexture", linear ? "0" : "1");
        if (linear) text = Set(text, "alphaIsTransparency", "0");
        // Raise a size limit smaller than the image, so Unity doesn't scale it down.
        text = Regex.Replace(text, @"^(\s*maxTextureSize:) *(\d+) *$",
            m => int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) < size ? $"{m.Groups[1].Value} {maxSize}" : m.Value, RegexOptions.Multiline);
        WriteText(metaPath, text);
        return guid;
    }

    private static string MaterialMeta(string guid) => $$"""
        fileFormatVersion: 2
        guid: {{guid}}
        NativeFormatImporter:
          externalObjects: {}
          mainObjectFileID: 2100000
          userData:
          assetBundleName:
          assetBundleVariant:

        """;

    /// <summary>A TextureImporter .meta as Unity 6 writes one, set up for pixel art.</summary>
    private static string TextureMeta(string guid, bool linear, int maxSize) => $$"""
        fileFormatVersion: 2
        guid: {{guid}}
        TextureImporter:
          internalIDToNameTable: []
          externalObjects: {}
          serializedVersion: 13
          mipmaps:
            mipMapMode: 0
            enableMipMap: 0
            sRGBTexture: {{(linear ? 0 : 1)}}
            linearTexture: 0
            fadeOut: 0
            borderMipMap: 0
            mipMapsPreserveCoverage: 0
            alphaTestReferenceValue: 0.5
            mipMapFadeDistanceStart: 1
            mipMapFadeDistanceEnd: 3
          bumpmap:
            convertToNormalMap: 0
            externalNormalMap: 0
            heightScale: 0.25
            normalMapFilter: 0
            flipGreenChannel: 0
          isReadable: 0
          streamingMipmaps: 0
          streamingMipmapsPriority: 0
          vTOnly: 0
          ignoreMipmapLimit: 0
          grayScaleToAlpha: 0
          generateCubemap: 6
          cubemapConvolution: 0
          seamlessCubemap: 0
          textureFormat: 1
          maxTextureSize: 2048
          textureSettings:
            serializedVersion: 2
            filterMode: 0
            aniso: 1
            mipBias: 0
            wrapU: 0
            wrapV: 0
            wrapW: 0
          nPOTScale: 0
          lightmap: 0
          compressionQuality: 50
          spriteMode: 0
          spriteExtrude: 1
          spriteMeshType: 1
          alignment: 0
          spritePivot: {x: 0.5, y: 0.5}
          spritePixelsToUnits: 100
          spriteBorder: {x: 0, y: 0, z: 0, w: 0}
          spriteGenerateFallbackPhysicsShape: 1
          alphaUsage: 1
          alphaIsTransparency: 0
          spriteTessellationDetail: -1
          textureType: 0
          textureShape: 1
          singleChannelComponent: 0
          flipbookRows: 1
          flipbookColumns: 1
          maxTextureSizeSet: 0
          compressionQualitySet: 0
          textureFormatSet: 0
          ignorePngGamma: 0
          applyGammaDecoding: 0
          swizzle: 50462976
          cookieLightType: 0
          platformSettings:
          - serializedVersion: 4
            buildTarget: DefaultTexturePlatform
            maxTextureSize: {{maxSize}}
            resizeAlgorithm: 0
            textureFormat: -1
            textureCompression: 0
            compressionQuality: 50
            crunchedCompression: 0
            allowsAlphaSplitting: 0
            overridden: 0
            ignorePlatformSupport: 0
            androidETC2FallbackOverride: 0
            forceMaximumCompressionQuality_BC6H_BC7: 0
          spriteSheet:
            serializedVersion: 2
            sprites: []
            outline: []
            customData:
            physicsShape: []
            bones: []
            spriteID:
            internalID: 0
            vertices: []
            indices:
            edges: []
            weights: []
            secondaryTextures: []
            spriteCustomMetadata:
              entries: []
            nameFileIdTable: {}
          mipmapLimitGroupName:
          pSDRemoveMatte: 0
          userData:
          assetBundleName:
          assetBundleVariant:

        """;

    private static string TextureSlot(string slot, string? guid)
    {
        string texture = guid == null ? "{fileID: 0}" : $"{{fileID: 2800000, guid: {guid}, type: 3}}";
        return $$"""
                - {{slot}}:
                    m_Texture: {{texture}}
                    m_Scale: {x: 1, y: 1}
                    m_Offset: {x: 0, y: 0}
            """;
    }

    /// <summary>
    /// A URP Lit material (metallic workflow, opaque) on the albedo, with the smoothness map as its Metallic map
    /// (smoothness from its alpha, scaled by _Smoothness 1) when there is one; without it, Smoothness 0.
    /// </summary>
    private static string Material(string name, string albedoGuid, string? maskGuid)
    {
        string Slots(params (string Slot, string? Guid)[] slots) => string.Join("\n", slots.Select(s => TextureSlot(s.Slot, s.Guid)));
        string keywords = maskGuid != null ? "\n  - _METALLICSPECGLOSSMAP" : " []";
        string smoothness = (maskGuid != null ? 1f : 0f).ToString(CultureInfo.InvariantCulture);
        return $$"""
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!21 &2100000
            Material:
              serializedVersion: 8
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_Name: {{name}}
              m_Shader: {fileID: 4800000, guid: {{LitShaderGuid}}, type: 3}
              m_Parent: {fileID: 0}
              m_ModifiedSerializedProperties: 0
              m_ValidKeywords:{{keywords}}
              m_InvalidKeywords: []
              m_LightmapFlags: 4
              m_EnableInstancingVariants: 0
              m_DoubleSidedGI: 0
              m_CustomRenderQueue: -1
              stringTagMap:
                RenderType: Opaque
              disabledShaderPasses:
              - MOTIONVECTORS
              m_LockedProperties:
              m_SavedProperties:
                serializedVersion: 3
                m_TexEnvs:
            {{Slots(("_BaseMap", albedoGuid), ("_BumpMap", null), ("_DetailAlbedoMap", null), ("_DetailMask", null), ("_DetailNormalMap", null),
                ("_EmissionMap", null), ("_MainTex", albedoGuid), ("_MetallicGlossMap", maskGuid), ("_OcclusionMap", null), ("_ParallaxMap", null),
                ("_SpecGlossMap", null), ("unity_Lightmaps", null), ("unity_LightmapsInd", null), ("unity_ShadowMasks", null))}}
                m_Ints: []
                m_Floats:
                - _AddPrecomputedVelocity: 0
                - _AlphaClip: 0
                - _AlphaToMask: 0
                - _Blend: 0
                - _BlendModePreserveSpecular: 1
                - _BumpScale: 1
                - _ClearCoatMask: 0
                - _ClearCoatSmoothness: 0
                - _Cull: 2
                - _Cutoff: 0.5
                - _DetailAlbedoMapScale: 1
                - _DetailNormalMapScale: 1
                - _DstBlend: 0
                - _DstBlendAlpha: 0
                - _EnvironmentReflections: 1
                - _GlossMapScale: 0
                - _Glossiness: 0
                - _GlossyReflections: 0
                - _Metallic: 0
                - _OcclusionStrength: 1
                - _Parallax: 0.005
                - _QueueOffset: 0
                - _ReceiveShadows: 1
                - _Smoothness: {{smoothness}}
                - _SmoothnessTextureChannel: 0
                - _SpecularHighlights: 1
                - _SrcBlend: 1
                - _SrcBlendAlpha: 1
                - _Surface: 0
                - _WorkflowMode: 1
                - _ZWrite: 1
                m_Colors:
                - _BaseColor: {r: 1, g: 1, b: 1, a: 1}
                - _Color: {r: 1, g: 1, b: 1, a: 1}
                - _EmissionColor: {r: 0, g: 0, b: 0, a: 1}
                - _SpecColor: {r: 0.19999996, g: 0.19999996, b: 0.19999996, a: 1}
              m_BuildTextureStacks: []
              m_AllowLocking: 1
            --- !u!114 &5331877296124699694
            MonoBehaviour:
              m_ObjectHideFlags: 11
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 0}
              m_Enabled: 1
              m_EditorHideFlags: 0
              m_Script: {fileID: 11500000, guid: d0353a89b1f911e48b9e16bdc9f2e058, type: 3}
              m_Name:
              m_EditorClassIdentifier:
              version: 10

            """;
    }
}
