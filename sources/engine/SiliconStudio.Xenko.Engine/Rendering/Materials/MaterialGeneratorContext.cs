﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using SiliconStudio.Core.Mathematics;
using SiliconStudio.Xenko.Graphics;
using SiliconStudio.Xenko.Shaders;

namespace SiliconStudio.Xenko.Rendering.Materials
{
    /// <summary>
    /// Main entry point class for generating shaders from a <see cref="MaterialDescriptor"/>
    /// </summary>
    public class MaterialGeneratorContext : ShaderGeneratorContext
    {
        public delegate void MaterialGeneratorCallback(MaterialShaderStage stage, MaterialGeneratorContext context);

        private readonly Dictionary<string, ShaderSource> registeredStreamBlend = new Dictionary<string, ShaderSource>();

        private readonly Dictionary<KeyValuePair<MaterialShaderStage, Type>, ShaderSource> finalInputStreamModifiers = new Dictionary<KeyValuePair<MaterialShaderStage, Type>, ShaderSource>();

        private readonly Dictionary<MaterialShaderStage, List<MaterialGeneratorCallback>> finalCallbacks = new Dictionary<MaterialShaderStage, List<MaterialGeneratorCallback>>();

        private readonly Stack<IMaterialDescriptor> materialStack = new Stack<IMaterialDescriptor>();

        private MaterialBlendLayerContext currentLayerContext;

        /// <summary>
        /// Initializes a new instance of <see cref="MaterialGeneratorContext"/>.
        /// </summary>
        /// <param name="material"></param>
        public MaterialGeneratorContext(Material material = null)
        {
            this.Material = material ?? new Material();
            Parameters = this.Material.Parameters;

            foreach (MaterialShaderStage stage in Enum.GetValues(typeof(MaterialShaderStage)))
            {
                finalCallbacks[stage] = new List<MaterialGeneratorCallback>();
            }

            // By default return the asset
            FindAsset = asset => ((Material)asset).Descriptor;
            GetAssetFriendlyName = asset => ((Material)asset).Descriptor != null ? ((Material)asset).Descriptor.MaterialId.ToString() : string.Empty;
        }

        /// <summary>
        /// Gets the compiled Material
        /// </summary>
        public Material Material { get; }

        public void AddFinalCallback(MaterialShaderStage stage, MaterialGeneratorCallback callback)
        {
            finalCallbacks[stage].Add(callback);
        }

        public void SetStreamFinalModifier<T>(MaterialShaderStage stage, ShaderSource shaderSource)
        {
            if (shaderSource == null)
                return;

            var typeT = typeof(T);
            finalInputStreamModifiers[new KeyValuePair<MaterialShaderStage, Type>(stage, typeT)] = shaderSource;
        }

        public ShaderSource GetStreamFinalModifier<T>(MaterialShaderStage stage)
        {
            ShaderSource shaderSource = null;
            finalInputStreamModifiers.TryGetValue(new KeyValuePair<MaterialShaderStage, Type>(stage, typeof(T)), out shaderSource);
            return shaderSource;
        }

        /// <summary>
        /// Push a material for processing.
        /// </summary>
        /// <param name="materialDescriptor">The material descriptor.</param>
        /// <param name="materialName">Friendly name of the material.</param>
        /// <returns><c>true</c> if the material is valid and can be visited, <c>false</c> otherwise.</returns>
        /// <exception cref="System.ArgumentNullException">materialDescriptor</exception>
        public bool PushMaterial(IMaterialDescriptor materialDescriptor, string materialName)
        {
            if (materialDescriptor == null) throw new ArgumentNullException(nameof(materialDescriptor));
            bool hasErrors = false;
            foreach (var previousMaterial in materialStack)
            {
                if (ReferenceEquals(previousMaterial, materialDescriptor) || previousMaterial.MaterialId == materialDescriptor.MaterialId)
                {
                    Log.Error($"The material [{materialName}] cannot be used recursively.");
                    hasErrors = true;
                }
            }

            if (!hasErrors)
            {
                materialStack.Push(materialDescriptor);
            }

            return !hasErrors;
        }

        public IMaterialDescriptor PopMaterial()
        {
            if (materialStack.Count == 0)
            {
                throw new InvalidOperationException("Cannot PopMaterial more than PushMaterial");
            }
            return materialStack.Pop();
        }

        /// <summary>
        /// Pushes a new layer with the specified blend map.
        /// </summary>
        /// <param name="blendMap">The blend map used by this layer.</param>
        public void PushLayer(IComputeScalar blendMap)
        {
            // We require a blend layer expect for the top level one.
            if (currentLayerContext != null && blendMap == null)
            {
                throw new ArgumentNullException(nameof(blendMap), "Blendmap parameter cannot be null for a child layer");
            }

            var newLayer = new MaterialBlendLayerContext(this, currentLayerContext, blendMap);
            if (currentLayerContext != null)
            {
                currentLayerContext.Children.Add(newLayer);
            }
            currentLayerContext = newLayer;
        }

        /// <summary>
        /// Pops the current layer.
        /// </summary>
        public void PopLayer()
        {
            if (currentLayerContext == null)
            {
                throw new InvalidOperationException("Cannot PopLayer when no balancing PushLayer was called");
            }

            // If we are poping the last layer, so we can process all layers
            if (currentLayerContext.Parent == null)
            {
                ProcessLayer(currentLayerContext, true);
            }
            else
            {
                currentLayerContext = currentLayerContext.Parent;
            }
        }
        public void AddShaderSource(MaterialShaderStage stage, ShaderSource shaderSource)
        {
            if (shaderSource == null) throw new ArgumentNullException(nameof(shaderSource));
            currentLayerContext.GetContextPerStage(stage).ShaderSources.Add(shaderSource);
        }

        public void Visit(IMaterialFeature feature)
        {
            // If feature is null, no-op
            feature?.Visit(this);
        }

        public bool HasShaderSources(MaterialShaderStage stage)
        {
            return currentLayerContext.GetContextPerStage(stage).ShaderSources.Count > 0;
        }

        public ShaderSource ComputeShaderSource(MaterialShaderStage stage)
        {
            return currentLayerContext.ComputeShaderSource(stage);
        }

        public ShaderSource GenerateStreamInitializers(MaterialShaderStage stage)
        {
            return currentLayerContext.GenerateStreamInitializers(stage);
        }

        public void UseStream(MaterialShaderStage stage, string stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            currentLayerContext.GetContextPerStage(stage).Streams.Add(stream);
        }

        public ShaderSource GetStreamBlendShaderSource(string stream)
        {
            ShaderSource shaderSource;
            registeredStreamBlend.TryGetValue(stream, out shaderSource);
            return shaderSource ?? new ShaderClassSource("MaterialStreamLinearBlend", stream);
        }

        public void UseStreamWithCustomBlend(MaterialShaderStage stage, string stream, ShaderSource blendStream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            UseStream(stage, stream);
            registeredStreamBlend[stream] = blendStream;
        }

        public void AddStreamInitializer(MaterialShaderStage stage, string streamInitilizerSource)
        {
            currentLayerContext.GetContextPerStage(stage).StreamInitializers.Add(streamInitilizerSource);
        }

        public void SetStream(MaterialShaderStage stage, string stream, IComputeNode computeNode, ObjectParameterKey<Texture> defaultTexturingKey, ParameterKey defaultValueKey, Color? defaultTextureValue = null)
        {
            currentLayerContext.SetStream(stage, stream, computeNode, defaultTexturingKey, defaultValueKey, defaultTextureValue);
        }

        public void SetStream(MaterialShaderStage stage, string stream, MaterialStreamType streamType, ShaderSource shaderSource)
        {
            currentLayerContext.SetStream(stage, stream, streamType, shaderSource);
        }

        public void SetStream(string stream, IComputeNode computeNode, ObjectParameterKey<Texture> defaultTexturingKey, ParameterKey defaultValueKey, Color? defaultTextureValue = null)
        {
            SetStream(MaterialShaderStage.Pixel, stream, computeNode, defaultTexturingKey, defaultValueKey, defaultTextureValue);
        }

        public void AddShading<T>(T shadingModel, ShaderSource shaderSource) where T : class, IMaterialShadingModelFeature
        {
            currentLayerContext.ShadingModels.Add(shadingModel, shaderSource);
        }

        private void ProcessLayer(MaterialBlendLayerContext layer, bool isLastLayer)
        {
            // Check if we have at least one shading model for this layer
            if (layer.ShadingModels.Count > 0)
            {
                layer.ShadingModelCount++;
            }

            // Process layers from the deepest level to lowest
            for (int i = 0; i < layer.Children.Count; i++)
            {
                var child = layer.Children[i];
                ProcessLayer(child, i + 1 == layer.Children.Count);
            }

            if (layer.Parent != null)
            {
                ProcessIntermediateLayer(layer, isLastLayer);
            }
            else
            {
                ProcessRootLayer(layer);
            }
        }

        /// <summary>
        /// Processes the intermediate layer.
        /// </summary>
        /// <param name="layer">The layer.</param>
        /// <param name="isLastLayer">if set to <c>true</c> the layer is the last children of the parent layer.</param>
        private void ProcessIntermediateLayer(MaterialBlendLayerContext layer, bool isLastLayer)
        {
            // note: SM = Shading Model
            var parent = layer.Parent;

            // Check if SM is changing relative to the state of the parent layer
            bool sameShadingModel = true;
            if (parent.ShadingModelCount > 0)
            {
                sameShadingModel = layer.ShadingModels.Equals(parent.ShadingModels);
            }
            else if (layer.ShadingModelCount > 0)
            {
                // If the current layer has a SM, copy it to the parent
                layer.ShadingModels.CopyTo(parent.ShadingModels);
                layer.ShadingModels.Clear();
                parent.ShadingModelCount++;
            }

            // If SM is the same, we can blend attributes
            if (sameShadingModel)
            {
                // If shading model is not changing, we generate the BlendStream shaders
                BlendStreams(layer);
            }
            else
            {
                parent.ShadingModelCount++;
            }

            var parentPixelLayerContext = parent.GetContextPerStage(MaterialShaderStage.Pixel);
            var pendingPixelLayerContext = parent.PendingPixelLayerContext;

            // --------------------------------------------
            // Copy streams to parent, but not for the PixelLayer if SM is changing
            // --------------------------------------------
            foreach (MaterialShaderStage stage in Enum.GetValues(typeof(MaterialShaderStage)))
            {
                var stageContext = layer.GetContextPerStage(stage);
                var parentStageContext = parent.GetContextPerStage(stage);

                // the Initializers
                parentStageContext.StreamInitializers.AddRange(stageContext.StreamInitializers);

                // skip pixel shader if shading model need to be blended
                if (stage == MaterialShaderStage.Pixel)
                {
                    if (!sameShadingModel)
                    {
                        continue;
                    }
                    // If same shading model, use temporarely the ParentPixelLayerContext
                    parentStageContext = pendingPixelLayerContext;
                }

                // Add shaders except Pixels if we have a different ShadingModel
                parentStageContext.ShaderSources.AddRange(stageContext.ShaderSources);
            }

            // --------------------------------------------
            // Apply shading: with 1) blending or 2) no blending
            // --------------------------------------------
            
            // Check if we need to force shading
            var forceShading = isLastLayer && (parent.ShadingModelCount > 1 || !sameShadingModel);
            if (!sameShadingModel || forceShading)
            {
                // true if the current layer has been blended already
                bool currentLayerAlreadyBlended = false;
               
                // If we need to shade but there is not yet a blend map setup (e.g: a single layer with a new SM != from parent SM)
                if (forceShading && parent.BlendMapForShadingModel == null)
                {
                    // Do we have a pending SM, if yes, we need to perform shading for the pending SM
                    if (!sameShadingModel)
                    {
                        foreach (var shaderSource in pendingPixelLayerContext.ShaderSources)
                        {
                            parentPixelLayerContext.ShaderSources.Add(shaderSource);
                        }
                        pendingPixelLayerContext.Reset();

                        foreach (var shaderSource in parent.ShadingModels.Generate(this))
                        {
                            parentPixelLayerContext.ShaderSources.Add(shaderSource);
                        }
                        parent.ShadingModels.Clear();
                        layer.ShadingModels.CopyTo(parent.ShadingModels);
                    }

                    // Setup a blend map so that we will blend SM just after
                    parent.BlendMapForShadingModel = layer.BlendMap;

                    // Copy pixel shaders to pending so it will be picked up by BlendShadingModel
                    var currentPixelLayerContext = layer.GetContextPerStage(MaterialShaderStage.Pixel);
                    pendingPixelLayerContext.ShaderSources.AddRange(currentPixelLayerContext.ShaderSources);
                    currentLayerAlreadyBlended = true;
                }

                // Do we need to blend shading model?
                if (parent.BlendMapForShadingModel != null)
                {
                    BlendShadingModel(parent, pendingPixelLayerContext, parentPixelLayerContext);
                }
                else
                {
                    // Else, we just expect to shade the current SM
                    foreach (var shaderSource in pendingPixelLayerContext.ShaderSources)
                    {
                        parentPixelLayerContext.ShaderSources.Add(shaderSource);
                    }
                    pendingPixelLayerContext.Reset();

                    foreach (var shaderSource in parent.ShadingModels.Generate(this))
                    {
                        parentPixelLayerContext.ShaderSources.Add(shaderSource);
                    }
                    parent.ShadingModels.Clear();
                }

                // If we changed the SM and the current layer has not been already blended
                if (!sameShadingModel && !currentLayerAlreadyBlended)
                {
                    // Save the BlendMap of the current layer for future blending
                    parent.BlendMapForShadingModel = layer.BlendMap;

                    // Copy the SM of the current layer to the parent layer
                    parent.ShadingModels.Clear();
                    layer.ShadingModels.CopyTo(parent.ShadingModels);

                    // If the shading model is different, the current attributes of the layer are not part of this blending
                    // So they will contribute to the next blending
                    var currentPixelLayerContext = layer.GetContextPerStage(MaterialShaderStage.Pixel);
                    pendingPixelLayerContext.ShaderSources.AddRange(currentPixelLayerContext.ShaderSources);

                    // If this is the last layer and we have more than 1 SM already, we force to blend the shading models
                    if (isLastLayer && parent.ShadingModelCount > 1)
                    {
                        BlendShadingModel(parent, pendingPixelLayerContext, parentPixelLayerContext);
                    }
                }
            }
        }

        private void BlendShadingModel(MaterialBlendLayerContext parent, MaterialBlendLayerPerStageContext pendingPixelLayerContext, MaterialBlendLayerPerStageContext parentPixelLayerContext)
        {
            var shaderBlendingSource = new ShaderMixinSource();
            shaderBlendingSource.Mixins.Add(new ShaderClassSource("MaterialSurfaceShadingBlend"));

            parent.SetStreamBlend(MaterialShaderStage.Pixel, parent.BlendMapForShadingModel);

            // Add shader source already setup for the parent
            foreach (var shaderSource in pendingPixelLayerContext.ShaderSources)
            {
                shaderBlendingSource.AddCompositionToArray("layers", shaderSource);
            }

            // Add shader source generated for blending this layer
            foreach (var shaderSource in parent.ShadingModels.Generate(this))
            {
                shaderBlendingSource.AddCompositionToArray("layers", shaderSource);
            }
            parent.ShadingModels.Clear();

            parentPixelLayerContext.ShaderSources.Add(shaderBlendingSource);

            pendingPixelLayerContext.Reset();
            parent.BlendMapForShadingModel = null;
        }

        private void ProcessRootLayer(MaterialBlendLayerContext layer)
        {
            // Make sure that any pending source are actually copied to the current Pixel ShaderSources
            var currentPixelLayerContext = layer.GetContextPerStage(MaterialShaderStage.Pixel);
            if (layer.PendingPixelLayerContext.ShaderSources.Count > 0)
            {
                currentPixelLayerContext.ShaderSources.AddRange(layer.PendingPixelLayerContext.ShaderSources);
                layer.PendingPixelLayerContext.Reset();
            }

            // Need to merge top level layer last
            if (layer.ShadingModels.Count > 0)
            {
                // Add shading
                foreach (var shaderSource in layer.ShadingModels.Generate(this))
                {
                    currentPixelLayerContext.ShaderSources.Add(shaderSource);
                }
            }

            foreach (var modifierKey in finalInputStreamModifiers.Keys)
            {
                currentLayerContext.GetContextPerStage(modifierKey.Key).ShaderSources.Add(finalInputStreamModifiers[modifierKey]);
            }

            // Clear final callback
            foreach (var callbackKeyPair in finalCallbacks)
            {
                var stage = callbackKeyPair.Key;
                var callbacks = callbackKeyPair.Value;
                foreach (var callback in callbacks)
                {
                    callback(stage, this);
                }
                callbacks.Clear();
            }
        }

        private void BlendStreams(MaterialBlendLayerContext layer)
        {
            // Generate Vertex and Pixel surface shaders
            foreach (MaterialShaderStage stage in Enum.GetValues(typeof(MaterialShaderStage)))
            {
                // If we don't have any stream set, we have nothing to blend
                var stageContext = layer.GetContextPerStage(stage);
                if (stageContext.Streams.Count == 0)
                {
                    continue;
                }

                // Blend setup for this layer
                layer.SetStreamBlend(stage, layer.BlendMap);

                // Generate a dynamic shader name
                // Create a mixin
                var shaderMixinSource = new ShaderMixinSource();
                shaderMixinSource.Mixins.Add(new ShaderClassSource("MaterialSurfaceStreamsBlend"));

                // Add all streams that we need to blend
                foreach (var stream in stageContext.Streams)
                {
                    shaderMixinSource.AddCompositionToArray("blends", GetStreamBlendShaderSource(stream));
                }
                stageContext.Streams.Clear();

                // Squash all ShaderSources to a single shader source
                var materialBlendLayerMixin = stageContext.ComputeShaderSource();
                stageContext.ShaderSources.Clear();

                // Add the shader to the mixin
                shaderMixinSource.AddComposition("layer", materialBlendLayerMixin);

                // Squash the shader sources
                stageContext.ShaderSources.Add(shaderMixinSource);
            }
        }
    }
}
