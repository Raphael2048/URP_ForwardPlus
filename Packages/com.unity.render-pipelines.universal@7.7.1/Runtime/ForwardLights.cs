using UnityEngine.Experimental.GlobalIllumination;
using Unity.Collections;
// using System;
// using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal.Internal
{
    /// <summary>
    /// Computes and submits lighting data to the GPU.
    /// </summary>
    public class ForwardLights
    {
        static class LightConstantBuffer
        {
            public static int _MainLightPosition;
            public static int _MainLightColor;

            public static int _AdditionalLightsCount;
            public static int _AdditionalLightsPosition;
            public static int _AdditionalLightsColor;
            public static int _AdditionalLightsAttenuation;
            public static int _AdditionalLightsSpotDir;

            public static int _AdditionalLightOcclusionProbeChannel;
        }
        
        int m_AdditionalLightsBufferId;
        int m_AdditionalLightsIndicesId;

        const string k_SetupLightConstants = "Setup Light Constants";
        MixedLightingSetup m_MixedLightingSetup;

        // Holds light direction for directional lights or position for punctual lights.
        // When w is set to 1.0, it means it's a punctual light.
        Vector4 k_DefaultLightPosition = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
        Vector4 k_DefaultLightColor = Color.black;

        // Default light attenuation is setup in a particular way that it causes
        // directional lights to return 1.0 for both distance and angle attenuation
        Vector4 k_DefaultLightAttenuation = new Vector4(0.0f, 1.0f, 0.0f, 1.0f);
        Vector4 k_DefaultLightSpotDirection = new Vector4(0.0f, 0.0f, 1.0f, 0.0f);
        Vector4 k_DefaultLightsProbeChannel = new Vector4(-1.0f, 1.0f, -1.0f, -1.0f);

        Vector4[] m_AdditionalLightPositions;
        Vector4[] m_AdditionalLightColors;
        Vector4[] m_AdditionalLightAttenuations;
        Vector4[] m_AdditionalLightSpotDirections;
        Vector4[] m_AdditionalLightOcclusionProbeChannels;

        private ComputeBuffer _numCulled, _lightData;

        bool m_UseStructuredBuffer;
        
        public ForwardLights()
        {
            m_UseStructuredBuffer = RenderingUtils.useStructuredBuffer;

            LightConstantBuffer._MainLightPosition = Shader.PropertyToID("_MainLightPosition");
            LightConstantBuffer._MainLightColor = Shader.PropertyToID("_MainLightColor");
            LightConstantBuffer._AdditionalLightsCount = Shader.PropertyToID("_AdditionalLightsCount");

            if (m_UseStructuredBuffer)
            {
                m_AdditionalLightsBufferId = Shader.PropertyToID("_AdditionalLightsBuffer");
                m_AdditionalLightsIndicesId = Shader.PropertyToID("_AdditionalLightsIndices");
            }
            else
            {
	            LightConstantBuffer._AdditionalLightsPosition = Shader.PropertyToID("_AdditionalLightsPosition");
	            LightConstantBuffer._AdditionalLightsColor = Shader.PropertyToID("_AdditionalLightsColor");
	            LightConstantBuffer._AdditionalLightsAttenuation = Shader.PropertyToID("_AdditionalLightsAttenuation");
	            LightConstantBuffer._AdditionalLightsSpotDir = Shader.PropertyToID("_AdditionalLightsSpotDir");
	            LightConstantBuffer._AdditionalLightOcclusionProbeChannel = Shader.PropertyToID("_AdditionalLightsOcclusionProbes");

	            int maxLights = UniversalRenderPipeline.maxVisibleAdditionalLights;
	            m_AdditionalLightPositions = new Vector4[maxLights];
	            m_AdditionalLightColors = new Vector4[maxLights];
	            m_AdditionalLightAttenuations = new Vector4[maxLights];
	            m_AdditionalLightSpotDirections = new Vector4[maxLights];
	            m_AdditionalLightOcclusionProbeChannels = new Vector4[maxLights];
            }
        }

        public void Setup(ScriptableRenderContext context, ref RenderingData renderingData, ComputeShader lightGridComputer)
        {
            int additionalLightsCount = renderingData.lightData.additionalLightsCount;
            LightRenderingMode additionalLightsMode = renderingData.lightData.shadeAdditionalLightsMode;
            if ((lightGridComputer == null || !SystemInfo.supportsComputeShaders) && additionalLightsMode == LightRenderingMode.ForwardPlus)
            {
                additionalLightsMode = LightRenderingMode.PerPixel;
            }
            CommandBuffer cmd = CommandBufferPool.Get(k_SetupLightConstants);
            SetupShaderLightConstants(cmd, ref renderingData);

            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsVertex,
                additionalLightsCount > 0 && additionalLightsMode == LightRenderingMode.PerVertex);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsPixel,
                additionalLightsCount > 0 && additionalLightsMode == LightRenderingMode.PerPixel);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.AdditionalLightsForwardPlus,
                additionalLightsCount > 0 && additionalLightsMode == LightRenderingMode.ForwardPlus);
            CoreUtils.SetKeyword(cmd, ShaderKeywordStrings.MixedLightingSubtractive,
                renderingData.lightData.supportsMixedLighting &&
                m_MixedLightingSetup == MixedLightingSetup.Subtractive);
            if (additionalLightsMode == LightRenderingMode.ForwardPlus)
            {
                SetupLightGridCompute(cmd, lightGridComputer, renderingData);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        private static int LIGHT_GRID_PIXEL_SIZE = 64;
        private static int LIGHT_GRID_DEPTH = 32;
        private static float LIGHT_NEAR_PLANE_OFFSET = .095f;
        private static float LIGHT_MAX_FAR_PLANE = 500.0f;

        Vector4 GetLightGridZParams(float NearPlane, float FarPlane, int GLightGridSizeZ)
        {
            // S = distribution scale
            // B, O are solved for given the z distances of the first+last slice, and the # of slices.
            //
            // slice = log2(z*B + O) * S

            // Don't spend lots of resolution right in front of the near plane
            float NearOffset = LIGHT_NEAR_PLANE_OFFSET;
            // Space out the slices so they aren't all clustered at the near plane
            float S = 4.05f;

            float N = NearPlane + NearOffset;
            float F = Mathf.Min(FarPlane, LIGHT_MAX_FAR_PLANE);

            float O = (F - N * Mathf.Pow(2.0f, (GLightGridSizeZ) / S)) / (F - N);
            float B = (1 - O) / N;

            return new Vector4(B, O, S, NearPlane);
        }

        public static readonly int lightGridSize = Shader.PropertyToID("_LightGridSize");
        public static readonly int rwNumCulledLightsGrid = Shader.PropertyToID("_RWNumCulledLightsGrid");
        public static readonly int rwCulledLightDataGrid = Shader.PropertyToID("_RWCulledLightDataGrid");
        public static readonly int lightGridZParams = Shader.PropertyToID("_LightGridZParams");
        public static readonly int numCulledLightsGrid = Shader.PropertyToID("_NumCulledLightsGrid");
        public static readonly int culledLightDataGrid = Shader.PropertyToID("_CulledLightDataGrid");

        void SetupLightGridCompute(CommandBuffer cmd, ComputeShader compute, RenderingData renderingData)
        {
            var gridSize = new Vector3Int((renderingData.cameraData.pixelWidth + LIGHT_GRID_PIXEL_SIZE - 1) / LIGHT_GRID_PIXEL_SIZE,
                (renderingData.cameraData.pixelHeight + LIGHT_GRID_PIXEL_SIZE - 1) / LIGHT_GRID_PIXEL_SIZE, LIGHT_GRID_DEPTH);
            if (_numCulled == null || _numCulled.count != (gridSize.x * gridSize.y * gridSize.z))
            {
                if (_numCulled != null)
                {
                    _numCulled.Release();
                }
                if (_lightData != null)
                {
                    _lightData.Release();
                }
                _numCulled = new ComputeBuffer(gridSize.x * gridSize.y * gridSize.z, 4, ComputeBufferType.Default);
                _lightData = new ComputeBuffer(gridSize.x * gridSize.y * gridSize.z * renderingData.lightData.maxPerClusterAdditionalLightsCount / 4, 4, ComputeBufferType.Default);
            }

            // RenderTextureDescriptor desc = new RenderTextureDescriptor();
            // desc.width = gridSize.x;
            // desc.height = gridSize.y;
            // desc.volumeDepth = gridSize.z;
            // desc.dimension = TextureDimension.Tex3D;
            // desc.enableRandomWrite = true;
            // desc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
            // desc.msaaSamples = 1;
            // cmd.GetTemporaryRT(_debugTex.id, desc);

            // var invProjection = cameraData.GetGPUProjectionMatrix().inverse;
            // cmd.SetComputeMatrixParam(compute, "_InvProjection", invProjection);
            bool fastAttenuation = Application.isMobilePlatform || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Switch;
            cmd.SetGlobalVector(lightGridSize, new Vector4(gridSize.x, gridSize.y, gridSize.z, fastAttenuation ? -0.36f : 1.0f)); // (0.8f * 0.8f - 1.0f) = -0.36f
            cmd.SetComputeBufferParam(compute, 0, rwNumCulledLightsGrid, _numCulled);
            cmd.SetComputeBufferParam(compute, 0, rwCulledLightDataGrid, _lightData);
            // cmd.SetComputeTextureParam(compute, 0, "_DebugTex", _debugTex.Identifier());
            Camera camera = renderingData.cameraData.camera;
            var lightGridZParamsData = GetLightGridZParams(camera.nearClipPlane, camera.farClipPlane, gridSize.z);

            cmd.SetGlobalVector(lightGridZParams, lightGridZParamsData);
            cmd.DispatchCompute(compute, 0, (gridSize.x + 3) / 4, (gridSize.y + 3) / 4, (gridSize.z + 3) / 4);
            cmd.SetGlobalBuffer(numCulledLightsGrid, _numCulled);
            cmd.SetGlobalBuffer(culledLightDataGrid, _lightData);
        }

        void InitializeLightConstants(NativeArray<VisibleLight> lights, int lightIndex, out Vector4 lightPos, out Vector4 lightColor, out Vector4 lightAttenuation, out Vector4 lightSpotDir, out Vector4 lightOcclusionProbeChannel)
        {
            lightPos = k_DefaultLightPosition;
            lightColor = k_DefaultLightColor;
            lightAttenuation = k_DefaultLightAttenuation;
            lightSpotDir = k_DefaultLightSpotDirection;
            lightOcclusionProbeChannel = k_DefaultLightsProbeChannel;

            // When no lights are visible, main light will be set to -1.
            // In this case we initialize it to default values and return
            if (lightIndex < 0)
                return;

            VisibleLight lightData = lights[lightIndex];
            if (lightData.lightType == LightType.Directional)
            {
                Vector4 dir = -lightData.localToWorldMatrix.GetColumn(2);
                lightPos = new Vector4(dir.x, dir.y, dir.z, 0.0f);
            }
            else
            {
                Vector4 pos = lightData.localToWorldMatrix.GetColumn(3);
                lightPos = new Vector4(pos.x, pos.y, pos.z, 1.0f);
            }

            // VisibleLight.finalColor already returns color in active color space
            lightColor = lightData.finalColor;

            // Directional Light attenuation is initialize so distance attenuation always be 1.0
            if (lightData.lightType != LightType.Directional)
            {
                // Light attenuation in universal matches the unity vanilla one.
                // attenuation = 1.0 / distanceToLightSqr
                // We offer two different smoothing factors.
                // The smoothing factors make sure that the light intensity is zero at the light range limit.
                // The first smoothing factor is a linear fade starting at 80 % of the light range.
                // smoothFactor = (lightRangeSqr - distanceToLightSqr) / (lightRangeSqr - fadeStartDistanceSqr)
                // We rewrite smoothFactor to be able to pre compute the constant terms below and apply the smooth factor
                // with one MAD instruction
                // smoothFactor =  distanceSqr * (1.0 / (fadeDistanceSqr - lightRangeSqr)) + (-lightRangeSqr / (fadeDistanceSqr - lightRangeSqr)
                //                 distanceSqr *           oneOverFadeRangeSqr             +              lightRangeSqrOverFadeRangeSqr

                // The other smoothing factor matches the one used in the Unity lightmapper but is slower than the linear one.
                // smoothFactor = (1.0 - saturate((distanceSqr * 1.0 / lightrangeSqr)^2))^2
                float lightRangeSqr = lightData.range * lightData.range;
                float fadeStartDistanceSqr = 0.8f * 0.8f * lightRangeSqr;
                float fadeRangeSqr = (fadeStartDistanceSqr - lightRangeSqr);
                float oneOverFadeRangeSqr = 1.0f / fadeRangeSqr;
                float lightRangeSqrOverFadeRangeSqr = -lightRangeSqr / fadeRangeSqr;
                float oneOverLightRangeSqr = 1.0f / Mathf.Max(0.0001f, lightData.range * lightData.range);

                // On mobile and Nintendo Switch: Use the faster linear smoothing factor (SHADER_HINT_NICE_QUALITY).
                // On other devices: Use the smoothing factor that matches the GI.
                lightAttenuation.x = Application.isMobilePlatform || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Switch ? oneOverFadeRangeSqr : oneOverLightRangeSqr;
                lightAttenuation.y = lightRangeSqrOverFadeRangeSqr;
            }

            if (lightData.lightType == LightType.Spot)
            {
                Vector4 dir = lightData.localToWorldMatrix.GetColumn(2);
                lightSpotDir = new Vector4(-dir.x, -dir.y, -dir.z, 0.0f);

                float lightAngle = Mathf.Deg2Rad * lightData.spotAngle * 0.5f;
                lightSpotDir.w = Mathf.Tan(lightAngle);

                // Spot Attenuation with a linear falloff can be defined as
                // (SdotL - cosOuterAngle) / (cosInnerAngle - cosOuterAngle)
                // This can be rewritten as
                // invAngleRange = 1.0 / (cosInnerAngle - cosOuterAngle)
                // SdotL * invAngleRange + (-cosOuterAngle * invAngleRange)
                // If we precompute the terms in a MAD instruction
                float cosOuterAngle = Mathf.Cos(lightAngle);
                // We neeed to do a null check for particle lights
                // This should be changed in the future
                // Particle lights will use an inline function
                float cosInnerAngle;
                if (lightData.light != null)
                    cosInnerAngle = Mathf.Cos(lightData.light.innerSpotAngle * Mathf.Deg2Rad * 0.5f);
                else
                    cosInnerAngle = Mathf.Cos((2.0f * Mathf.Atan(Mathf.Tan(lightData.spotAngle * 0.5f * Mathf.Deg2Rad) * (64.0f - 18.0f) / 64.0f)) * 0.5f);
                float smoothAngleRange = Mathf.Max(0.001f, cosInnerAngle - cosOuterAngle);
                float invAngleRange = 1.0f / smoothAngleRange;
                float add = -cosOuterAngle * invAngleRange;
                lightAttenuation.z = invAngleRange;
                lightAttenuation.w = add;
            }

            Light light = lightData.light;

            // Set the occlusion probe channel.
            int occlusionProbeChannel = light != null ? light.bakingOutput.occlusionMaskChannel : -1;

            // If we have baked the light, the occlusion channel is the index we need to sample in 'unity_ProbesOcclusion'
            // If we have not baked the light, the occlusion channel is -1.
            // In case there is no occlusion channel is -1, we set it to zero, and then set the second value in the
            // input to one. We then, in the shader max with the second value for non-occluded lights.
            lightOcclusionProbeChannel.x = occlusionProbeChannel == -1 ? 0f : occlusionProbeChannel;
            lightOcclusionProbeChannel.y = occlusionProbeChannel == -1 ? 1f : 0f;

            // TODO: Add support to shadow mask
            if (light != null && light.bakingOutput.mixedLightingMode == MixedLightingMode.Subtractive && light.bakingOutput.lightmapBakeType == LightmapBakeType.Mixed)
            {
                if (m_MixedLightingSetup == MixedLightingSetup.None && lightData.light.shadows != LightShadows.None)
                {
                    m_MixedLightingSetup = MixedLightingSetup.Subtractive;
                }
            }
        }

        void SetupShaderLightConstants(CommandBuffer cmd, ref RenderingData renderingData)
        {
            m_MixedLightingSetup = MixedLightingSetup.None;

            // Main light has an optimized shader path for main light. This will benefit games that only care about a single light.
            // Universal pipeline also supports only a single shadow light, if available it will be the main light.
            SetupMainLightConstants(cmd, ref renderingData.lightData);
            SetupAdditionalLightConstants(cmd, ref renderingData);
        }

        void SetupMainLightConstants(CommandBuffer cmd, ref LightData lightData)
        {
            Vector4 lightPos, lightColor, lightAttenuation, lightSpotDir, lightOcclusionChannel;
            InitializeLightConstants(lightData.visibleLights, lightData.mainLightIndex, out lightPos, out lightColor, out lightAttenuation, out lightSpotDir, out lightOcclusionChannel);

            cmd.SetGlobalVector(LightConstantBuffer._MainLightPosition, lightPos);
            cmd.SetGlobalVector(LightConstantBuffer._MainLightColor, lightColor);
        }

        void SetupAdditionalLightConstants(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref LightData lightData = ref renderingData.lightData;
            var cullResults = renderingData.cullResults;
            var lights = lightData.visibleLights;
            int maxAdditionalLightsCount = UniversalRenderPipeline.maxVisibleAdditionalLights;
            int additionalLightsCount = SetupPerObjectLightIndices(cullResults, ref lightData);
            if (additionalLightsCount > 0)
            {
                if (m_UseStructuredBuffer)
                {
                    NativeArray<ShaderInput.LightData> additionalLightsData = new NativeArray<ShaderInput.LightData>(additionalLightsCount, Allocator.Temp);
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        VisibleLight light = lights[i];
                        if (lightData.mainLightIndex != i)
                        {
                            ShaderInput.LightData data;
                            InitializeLightConstants(lights, i,
                                out data.position, out data.color, out data.attenuation,
                                out data.spotDirection, out data.occlusionProbeChannels);
                            additionalLightsData[lightIter] = data;
                            lightIter++;
                        }
                    }

                    var lightDataBuffer = ShaderData.instance.GetLightDataBuffer(additionalLightsCount);
                    lightDataBuffer.SetData(additionalLightsData);

                    int lightIndices = cullResults.lightAndReflectionProbeIndexCount;
                    var lightIndicesBuffer = ShaderData.instance.GetLightIndicesBuffer(lightIndices);
                    
                    cmd.SetGlobalBuffer(m_AdditionalLightsBufferId, lightDataBuffer);
                    cmd.SetGlobalBuffer(m_AdditionalLightsIndicesId, lightIndicesBuffer);

                    additionalLightsData.Dispose();
                }
                else
                {
                    for (int i = 0, lightIter = 0; i < lights.Length && lightIter < maxAdditionalLightsCount; ++i)
                    {
                        VisibleLight light = lights[i];
                        if (lightData.mainLightIndex != i)
                        {
                            InitializeLightConstants(lights, i, out m_AdditionalLightPositions[lightIter],
                                out m_AdditionalLightColors[lightIter],
                                out m_AdditionalLightAttenuations[lightIter],
                                out m_AdditionalLightSpotDirections[lightIter],
                                out m_AdditionalLightOcclusionProbeChannels[lightIter]);
                            lightIter++;
                        }
                    }

                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsPosition, m_AdditionalLightPositions);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsColor, m_AdditionalLightColors);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsAttenuation, m_AdditionalLightAttenuations);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightsSpotDir, m_AdditionalLightSpotDirections);
                    cmd.SetGlobalVectorArray(LightConstantBuffer._AdditionalLightOcclusionProbeChannel, m_AdditionalLightOcclusionProbeChannels);
                }

                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, new Vector4(lightData.maxPerObjectAdditionalLightsCount, lightData.additionalLightsCount, lightData.maxPerClusterAdditionalLightsCount, 0.0f));
            }
            else
            {
                cmd.SetGlobalVector(LightConstantBuffer._AdditionalLightsCount, Vector4.zero);
            }
        }
        
        int SetupPerObjectLightIndices(CullingResults cullResults, ref LightData lightData)
        {
            if (lightData.additionalLightsCount == 0)
                return lightData.additionalLightsCount;

            var visibleLights = lightData.visibleLights;
            var perObjectLightIndexMap = cullResults.GetLightIndexMap(Allocator.Temp);
            int globalDirectionalLightsCount = 0;
            int additionalLightsCount = 0;

            // Disable all directional lights from the perobject light indices
            // Pipeline handles main light globally and there's no support for additional directional lights atm.
            for (int i = 0; i < visibleLights.Length; ++i)
            {
                if (additionalLightsCount >= UniversalRenderPipeline.maxVisibleAdditionalLights)
                    break;

                VisibleLight light = visibleLights[i];
                if (i == lightData.mainLightIndex)
                {
                    perObjectLightIndexMap[i] = -1;
                    ++globalDirectionalLightsCount;
                }
                else
                {
                    perObjectLightIndexMap[i] -= globalDirectionalLightsCount;
                    ++additionalLightsCount;
                }
            }

            // Disable all remaining lights we cannot fit into the global light buffer.
            for (int i = globalDirectionalLightsCount + additionalLightsCount; i < perObjectLightIndexMap.Length; ++i)
                perObjectLightIndexMap[i] = -1;

            cullResults.SetLightIndexMap(perObjectLightIndexMap);

            if (m_UseStructuredBuffer && additionalLightsCount > 0)
            {
                int lightAndReflectionProbeIndices = cullResults.lightAndReflectionProbeIndexCount;
                Assertions.Assert.IsTrue(lightAndReflectionProbeIndices > 0, "Pipelines configures additional lights but per-object light and probe indices count is zero.");
                cullResults.FillLightAndReflectionProbeIndices(ShaderData.instance.GetLightIndicesBuffer(lightAndReflectionProbeIndices));
            }
            
            perObjectLightIndexMap.Dispose();
            return additionalLightsCount;
        }
    }
}
