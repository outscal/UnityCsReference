// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Experimental.TerrainAPI
{
    public class PaintContext
    {
        // initialized by constructor
        public Terrain originTerrain { get; }     // the terrain that defines the coordinate system and world space position of this PaintContext
        public RectInt pixelRect { get; }         // the rectangle, in target texture pixels on the originTerrain, that this paint context represents
        public int targetTextureWidth { get; }    // the size of the target texture, per terrain tile
        public int targetTextureHeight { get; }   // the size of the target texture, per terrain tile
        public Vector2 pixelSize { get; }         // size of a paint context pixel in object/terrain/world space

        // initialized by CreateRenderTargets()
        public RenderTexture sourceRenderTexture { get; private set; }       // the original data
        public RenderTexture destinationRenderTexture { get; private set; }  // the modified data (you render to this)
        public RenderTexture oldRenderTexture { get; private set; }          // active render texture at the time CreateRenderTargets() is called, restored on Cleanup()

        public int terrainCount { get { return m_TerrainTiles.Count; } }
        public Terrain GetTerrain(int terrainIndex)
        {
            return m_TerrainTiles[terrainIndex].terrain;
        }

        public RectInt GetClippedPixelRectInTerrainPixels(int terrainIndex)
        {
            return m_TerrainTiles[terrainIndex].clippedTerrainPixels;
        }

        public RectInt GetClippedPixelRectInRenderTexturePixels(int terrainIndex)
        {
            return m_TerrainTiles[terrainIndex].clippedPCPixels;
        }

        // initialized by constructor
        private List<TerrainTile> m_TerrainTiles;              // all terrain tiles touched by this paint context

        public interface ITerrainInfo
        {
            Terrain terrain                 { get; }            // the terrain tile
            RectInt clippedTerrainPixels    { get; }            // the region modified by the PaintContext, in target texture pixels
            RectInt clippedPCPixels         { get; }            // the region modified by the PaintContext, in PaintContext.sourceRenderTexture or destinationRenderTexture pixels
            bool gatherEnable               { get; set; }       // user tools can disable gathering of this terrain tile by setting this flag (default true)
            bool scatterEnable              { get; set; }       // user tools can disable scattering to this terrain tile by setting this flag (default true)
            object userData                 { get; set; }       // user tools can use this to associate data with the terrain
        }

        private class TerrainTile : ITerrainInfo
        {
            public Terrain terrain;                 // the terrain object for this tile
            public Vector2Int tileOriginPixels;     // coordinates of this terrain tile in originTerrain target texture pixels

            public RectInt clippedTerrainPixels;    // the tile pixels touched by this PaintContext (in terrain-local target texture pixels)
            public RectInt clippedPCPixels;         // the tile pixels touched by this PaintContext (in PaintContext/source/destRenderTexture pixels)

            public object userData;                 // user data stash
            public bool gatherEnable;                 // user controls for read/write
            public bool scatterEnable;

            Terrain ITerrainInfo.terrain                 { get { return terrain; } }
            RectInt ITerrainInfo.clippedTerrainPixels    { get { return clippedTerrainPixels; } }
            RectInt ITerrainInfo.clippedPCPixels         { get { return clippedPCPixels; } }
            bool ITerrainInfo.gatherEnable               { get { return gatherEnable; } set { gatherEnable = value; } }
            bool ITerrainInfo.scatterEnable              { get { return scatterEnable; } set { scatterEnable = value; } }
            object ITerrainInfo.userData                 { get { return userData; } set { userData = value; } }

            public static TerrainTile Make(Terrain terrain, int tileOriginPixelsX, int tileOriginPixelsY, RectInt pixelRect, int targetTextureWidth, int targetTextureHeight)
            {
                var tile = new TerrainTile()
                {
                    terrain = terrain,
                    gatherEnable = true,
                    scatterEnable = true,
                    tileOriginPixels = new Vector2Int(tileOriginPixelsX, tileOriginPixelsY),
                    clippedTerrainPixels = new RectInt()
                    {
                        x = Mathf.Max(0, pixelRect.x - tileOriginPixelsX),
                        y = Mathf.Max(0, pixelRect.y - tileOriginPixelsY),
                        xMax = Mathf.Min(targetTextureWidth, pixelRect.xMax - tileOriginPixelsX),
                        yMax = Mathf.Min(targetTextureHeight, pixelRect.yMax - tileOriginPixelsY)
                    },
                };
                tile.clippedPCPixels = new RectInt(
                    tile.clippedTerrainPixels.x + tile.tileOriginPixels.x - pixelRect.x,
                    tile.clippedTerrainPixels.y + tile.tileOriginPixels.y - pixelRect.y,
                    tile.clippedTerrainPixels.width,
                    tile.clippedTerrainPixels.height);

                if (tile.clippedTerrainPixels.width == 0 || tile.clippedTerrainPixels.height == 0)
                {
                    tile.gatherEnable = false;
                    tile.scatterEnable = false;
                    Debug.LogError("PaintContext.ClipTerrainTiles found 0 content rect");       // we really shouldn't ever have this..
                }

                return tile;
            }
        }

        private class SplatmapUserData             // splatmap operation data per Terrain tile
        {
            public TerrainLayer terrainLayer;       // the terrain layer we are concerned with
            public int terrainLayerIndex;           // the terrain layer index on this Terrain
            public int mapIndex;                    // the splatmap index on this Terrain containing the desired TerrainLayer weight
            public int channelIndex;                // the channel on the splatmap containing the desired TerrainLayer weight
        };

        [Flags]
        internal enum ToolAction
        {
            None = 0,
            PaintHeightmap = 1 << 0,
            PaintTexture = 1 << 1,
            PaintSurfaceMask = 1 << 2
        }

        // TerrainPaintUtilityEditor hooks to this event to do automatic undo
        internal static event Action<PaintContext.ITerrainInfo, ToolAction, string /*editorUndoName*/> onTerrainTileBeforePaint;

        public PaintContext(Terrain terrain, RectInt pixelRect, int targetTextureWidth, int targetTextureHeight)
        {
            this.originTerrain = terrain;
            this.pixelRect = pixelRect;
            this.targetTextureWidth = targetTextureWidth;
            this.targetTextureHeight = targetTextureHeight;
            TerrainData terrainData = terrain.terrainData;
            this.pixelSize = new Vector2(
                terrainData.size.x / (targetTextureWidth - 1.0f),
                terrainData.size.z / (targetTextureHeight - 1.0f));

            FindTerrainTiles();
        }

        public static PaintContext CreateFromBounds(Terrain terrain, Rect boundsInTerrainSpace, int inputTextureWidth, int inputTextureHeight, int extraBorderPixels = 0)
        {
            return new PaintContext(
                terrain,
                TerrainPaintUtility.CalcPixelRectFromBounds(terrain, boundsInTerrainSpace, inputTextureWidth, inputTextureHeight, extraBorderPixels),
                inputTextureWidth, inputTextureHeight);
        }

        internal void FindTerrainTiles()
        {
            m_TerrainTiles = new List<PaintContext.TerrainTile>();

            Terrain left = originTerrain.leftNeighbor;
            Terrain right = originTerrain.rightNeighbor;
            Terrain top = originTerrain.topNeighbor;
            Terrain bottom = originTerrain.bottomNeighbor;

            bool wantLeft = (pixelRect.x < 0);
            bool wantRight = (pixelRect.xMax > (targetTextureWidth - 1));
            bool wantTop = (pixelRect.yMax > (targetTextureHeight - 1));
            bool wantBottom = (pixelRect.y < 0);

            if (wantLeft && wantRight)
            {
                Debug.LogWarning("PaintContext pixelRect is too large!  It should touch a maximum of 2 Terrains horizontally.");
                wantRight = false;
            }

            if (wantTop && wantBottom)
            {
                Debug.LogWarning("PaintContext pixelRect is too large!  It should touch a maximum of 2 Terrains vertically.");
                wantBottom = false;
            }

            // add center tile
            m_TerrainTiles.Add(TerrainTile.Make(originTerrain, 0, 0, pixelRect, targetTextureWidth, targetTextureHeight));

            // add horizontal and vertical neighbors
            Terrain horiz = null;
            Terrain vert = null;
            Terrain cornerTerrain = null;

            int horizTileDelta = 0;     // how many tiles to move horizontally
            int vertTileDelta = 0;      // how many tiles to move vertically

            if (wantLeft)
            {
                horizTileDelta = -1;
                horiz = left;
            }
            else if (wantRight)
            {
                horizTileDelta = 1;
                horiz = right;
            }

            if (wantTop)
            {
                vertTileDelta = 1;
                vert = top;
            }
            else if (wantBottom)
            {
                vertTileDelta = -1;
                vert = bottom;
            }

            if (horiz)
            {
                m_TerrainTiles.Add(TerrainTile.Make(horiz, horizTileDelta * (targetTextureWidth - 1), 0, pixelRect, targetTextureWidth, targetTextureHeight));

                // add corner, if we have a link
                if (wantTop && horiz.topNeighbor)
                    cornerTerrain = horiz.topNeighbor;
                else if (wantBottom && horiz.bottomNeighbor)
                    cornerTerrain = horiz.bottomNeighbor;
            }

            if (vert)
            {
                m_TerrainTiles.Add(TerrainTile.Make(vert, 0, vertTileDelta * (targetTextureHeight - 1), pixelRect, targetTextureWidth, targetTextureHeight));

                // add corner, if we have a link
                if (wantLeft && vert.leftNeighbor)
                    cornerTerrain = vert.leftNeighbor;
                else if (wantRight && vert.rightNeighbor)
                    cornerTerrain = vert.rightNeighbor;
            }

            if (cornerTerrain != null)
                m_TerrainTiles.Add(TerrainTile.Make(cornerTerrain, horizTileDelta * (targetTextureWidth - 1), vertTileDelta * (targetTextureHeight - 1), pixelRect, targetTextureWidth, targetTextureHeight));
        }

        public void CreateRenderTargets(RenderTextureFormat colorFormat)
        {
            sourceRenderTexture = RenderTexture.GetTemporary(pixelRect.width, pixelRect.height, 0, colorFormat, RenderTextureReadWrite.Linear);
            destinationRenderTexture = RenderTexture.GetTemporary(pixelRect.width, pixelRect.height, 0, colorFormat, RenderTextureReadWrite.Linear);
            sourceRenderTexture.wrapMode = TextureWrapMode.Clamp;
            sourceRenderTexture.filterMode = FilterMode.Point;
            oldRenderTexture = RenderTexture.active;
        }

        public void Cleanup(bool restoreRenderTexture = true)
        {
            if (restoreRenderTexture)
                RenderTexture.active = oldRenderTexture;
            RenderTexture.ReleaseTemporary(sourceRenderTexture);
            RenderTexture.ReleaseTemporary(destinationRenderTexture);
            sourceRenderTexture = null;
            destinationRenderTexture = null;
            oldRenderTexture = null;
        }

        private void GatherInternal(
            Func<ITerrainInfo, Texture> terrainToTexture,
            Color defaultColor,
            string operationName,
            Material blitMaterial = null,
            int blitPass = 0,
            Action<ITerrainInfo> beforeBlit = null,
            Action<ITerrainInfo> afterBlit = null)
        {
            if (blitMaterial == null)
                blitMaterial = TerrainPaintUtility.GetBlitMaterial();

            RenderTexture.active = sourceRenderTexture;
            GL.Clear(false, true, defaultColor);

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, pixelRect.width, 0, pixelRect.height);
            for (int i = 0; i < m_TerrainTiles.Count; i++)
            {
                TerrainTile terrainTile = m_TerrainTiles[i];
                if (!terrainTile.gatherEnable)
                    continue;

                Texture sourceTexture = terrainToTexture(terrainTile);
                if ((sourceTexture == null) || (!terrainTile.gatherEnable))   // double check gatherEnable in case terrainToTexture modified it
                    continue;

                if ((sourceTexture.width != targetTextureWidth) || (sourceTexture.height != targetTextureHeight))
                {
                    Debug.LogWarning(operationName + " requires the same resolution texture for all Terrains - mismatched Terrains are ignored.", terrainTile.terrain);
                    continue;
                }

                beforeBlit?.Invoke(terrainTile);
                if (!terrainTile.gatherEnable) // check again, beforeBlit may have modified it
                    continue;

                FilterMode oldFilterMode = sourceTexture.filterMode;
                sourceTexture.filterMode = FilterMode.Point;

                blitMaterial.SetTexture("_MainTex", sourceTexture);
                blitMaterial.SetPass(blitPass);
                TerrainPaintUtility.DrawQuad(terrainTile.clippedPCPixels, terrainTile.clippedTerrainPixels, sourceTexture);

                sourceTexture.filterMode = oldFilterMode;

                afterBlit?.Invoke(terrainTile);
            }
            GL.PopMatrix();
            RenderTexture.active = oldRenderTexture;
        }

        private void ScatterInternal(
            Func<ITerrainInfo, RenderTexture> terrainToRT,
            string operationName,
            Material blitMaterial = null,
            int blitPass = 0,
            Action<ITerrainInfo> beforeBlit = null,
            Action<ITerrainInfo> afterBlit = null)
        {
            var oldRT = RenderTexture.active;

            if (blitMaterial == null)
                blitMaterial = TerrainPaintUtility.GetBlitMaterial();

            for (int i = 0; i < m_TerrainTiles.Count; i++)
            {
                TerrainTile terrainTile = m_TerrainTiles[i];
                if (!terrainTile.scatterEnable)
                    continue;

                RenderTexture target = terrainToRT(terrainTile);
                if ((target == null) || (!terrainTile.scatterEnable)) // double check scatterEnable in case terrainToRT modified it
                    continue;

                if ((target.width != targetTextureWidth) || (target.height != targetTextureHeight))
                {
                    Debug.LogWarning(operationName + " requires the same resolution for all Terrains - mismatched Terrains are ignored.", terrainTile.terrain);
                    continue;
                }

                beforeBlit?.Invoke(terrainTile);
                if (!terrainTile.scatterEnable)   // check again, beforeBlit may have modified it
                    continue;

                RenderTexture.active = target;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, target.width, 0, target.height);
                {
                    FilterMode oldFilterMode = destinationRenderTexture.filterMode;
                    destinationRenderTexture.filterMode = FilterMode.Point;

                    blitMaterial.SetTexture("_MainTex", destinationRenderTexture);
                    blitMaterial.SetPass(blitPass);
                    TerrainPaintUtility.DrawQuad(terrainTile.clippedTerrainPixels, terrainTile.clippedPCPixels, destinationRenderTexture);

                    destinationRenderTexture.filterMode = oldFilterMode;
                }
                GL.PopMatrix();

                afterBlit?.Invoke(terrainTile);
            }

            RenderTexture.active = oldRT;
        }

        public void Gather(Func<ITerrainInfo, Texture> terrainSource, Color defaultColor, Material blitMaterial = null, int blitPass = 0, Action<ITerrainInfo> beforeBlit = null, Action<ITerrainInfo> afterBlit = null)
        {
            if (terrainSource != null)
                GatherInternal(terrainSource, defaultColor, "PaintContext.Gather", blitMaterial, blitPass, beforeBlit, afterBlit);
        }

        public void Scatter(Func<ITerrainInfo, RenderTexture> terrainDest, Material blitMaterial = null, int blitPass = 0, Action<ITerrainInfo> beforeBlit = null, Action<ITerrainInfo> afterBlit = null)
        {
            if (terrainDest != null)
                ScatterInternal(terrainDest, "PaintContext.Scatter", blitMaterial, blitPass, beforeBlit, afterBlit);
        }

        public void GatherHeightmap()
        {
            GatherInternal(
                t => t.terrain.terrainData.heightmapTexture,
                new Color(0.0f, 0.0f, 0.0f, 0.0f),
                "PaintContext.GatherHeightmap");
        }

        public void ScatterHeightmap(string editorUndoName)
        {
            ScatterInternal(
                t =>
                {
                    // doing our own copy back here, and skipping the ScatterInternal's blit by returning null
                    onTerrainTileBeforePaint?.Invoke(t, ToolAction.PaintHeightmap, editorUndoName);
                    RenderTexture.active = destinationRenderTexture;
                    t.terrain.terrainData.CopyActiveRenderTextureToHeightmap(t.clippedPCPixels, t.clippedTerrainPixels.min, t.terrain.drawInstanced ? TerrainHeightmapSyncControl.None : TerrainHeightmapSyncControl.HeightOnly);
                    OnTerrainPainted(t, ToolAction.PaintHeightmap);
                    return null;
                },
                "PaintContext.ScatterHeightmap");
        }

        public void GatherSurfaceMask()
        {
            GatherInternal(
                t => t.terrain.terrainData.surfaceMaskTexture,
                new Color(0.0f, 0.0f, 0.0f, 0.0f),
                "PaintContext.GatherSurfaceMask");
        }

        public void ScatterSurfaceMask(string editorUndoName)
        {
            ScatterInternal(
                t =>
                {
                    onTerrainTileBeforePaint?.Invoke(t, ToolAction.PaintSurfaceMask, editorUndoName);
                    t.terrain.terrainData.CopyActiveRenderTextureToTexture(TerrainData.SurfaceMaskTextureName, 0, t.clippedPCPixels, t.clippedTerrainPixels.min, true);
                    OnTerrainPainted(t, ToolAction.PaintSurfaceMask);
                    return null;
                },
                "PaintContext.ScatterSurfaceMask");
        }

        public void GatherNormals()
        {
            GatherInternal(
                t => t.terrain.normalmapTexture,
                new Color(0.5f, 0.5f, 0.5f, 0.5f),
                "PaintContext.GatherNormals");
        }

        private SplatmapUserData GetTerrainLayerUserData(ITerrainInfo context, TerrainLayer terrainLayer = null, bool addLayerIfDoesntExist = false)
        {
            // look up existing user data, if any
            SplatmapUserData userData = (context.userData as SplatmapUserData);
            if (userData != null)
            {
                // check if it is appropriate, return if so
                if ((terrainLayer == null) || (terrainLayer == userData.terrainLayer))
                    return userData;
                else
                    userData = null;
            }

            // otherwise let's build it
            if (userData == null)
            {
                int tileLayerIndex = -1;
                if (terrainLayer != null)
                {
                    // look for the layer on the terrain
                    tileLayerIndex = TerrainPaintUtility.FindTerrainLayerIndex(context.terrain, terrainLayer);
                    if ((tileLayerIndex == -1) && (addLayerIfDoesntExist))
                    {
                        tileLayerIndex = TerrainPaintUtility.AddTerrainLayer(context.terrain, terrainLayer);
                    }
                }

                // if we found the layer, build user data
                if (tileLayerIndex != -1)
                {
                    userData = new SplatmapUserData();
                    userData.terrainLayer = terrainLayer;
                    userData.terrainLayerIndex = tileLayerIndex;
                    userData.mapIndex = tileLayerIndex >> 2;
                    userData.channelIndex = tileLayerIndex & 0x3;
                }
                context.userData = userData;
            }
            return userData;
        }

        public void GatherAlphamap(TerrainLayer inputLayer, bool addLayerIfDoesntExist = true)
        {
            if (inputLayer == null)
                return;

            Material copyTerrainLayerMaterial = TerrainPaintUtility.GetCopyTerrainLayerMaterial();
            Vector4[] layerMasks = { new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 0, 0, 1) };

            GatherInternal(
                t =>
                {   // return the texture to be gathered from this terrain tile
                    SplatmapUserData userData = GetTerrainLayerUserData(t, inputLayer, addLayerIfDoesntExist);
                    if (userData != null)
                        return TerrainPaintUtility.GetTerrainAlphaMapChecked(t.terrain, userData.mapIndex);
                    else
                        return null;
                },

                new Color(0.0f, 0.0f, 0.0f, 0.0f),
                "PaintContext.GatherAlphamap",
                copyTerrainLayerMaterial, 0,
                t =>
                {   // before blit -- setup layer mask in the material
                    SplatmapUserData userData = GetTerrainLayerUserData(t);
                    copyTerrainLayerMaterial.SetVector("_LayerMask", layerMasks[userData.channelIndex]);
                });
        }

        public void ScatterAlphamap(string editorUndoName)
        {
            Vector4[] layerMasks = { new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0), new Vector4(0, 0, 1, 0), new Vector4(0, 0, 0, 1) };
            Material copyTerrainLayerMaterial = TerrainPaintUtility.GetCopyTerrainLayerMaterial();

            var rtdesc = new RenderTextureDescriptor(destinationRenderTexture.width, destinationRenderTexture.height, RenderTextureFormat.ARGB32);
            rtdesc.sRGB = false;
            rtdesc.useMipMap = false;
            rtdesc.autoGenerateMips = false;
            RenderTexture tempTarget = RenderTexture.GetTemporary(rtdesc);

            ScatterInternal(
                t => // We're going to do ALL of the work in this terrainToRT function, as it is very custom, and we'll just return null to skip the ScatterInternal rendering
                {
                    SplatmapUserData userData = GetTerrainLayerUserData(t);
                    if (userData != null)
                    {
                        onTerrainTileBeforePaint?.Invoke(t, ToolAction.PaintTexture, editorUndoName);

                        int targetAlphamapIndex = userData.mapIndex;
                        int targetChannelIndex = userData.channelIndex;
                        Texture2D targetAlphamapTexture = t.terrain.terrainData.alphamapTextures[targetAlphamapIndex];

                        destinationRenderTexture.filterMode = FilterMode.Point;
                        sourceRenderTexture.filterMode = FilterMode.Point;

                        // iterate all alphamaps to modify them (have to modify all to renormalize)
                        for (int i = 0; i <= t.terrain.terrainData.alphamapTextureCount; i++)   // NOTE: this is a non-standard for loop
                        {
                            // modify the target index last, (skip it the first time)
                            if (i == targetAlphamapIndex)
                                continue;
                            int alphamapIndex = (i == t.terrain.terrainData.alphamapTextureCount) ? targetAlphamapIndex : i;

                            Texture2D alphamapTexture = t.terrain.terrainData.alphamapTextures[alphamapIndex];
                            if ((alphamapTexture.width != targetTextureWidth) || (alphamapTexture.height != targetTextureHeight))
                            {
                                Debug.LogWarning("PaintContext alphamap operations must use the same resolution for all Terrains - mismatched Terrains are ignored.", t.terrain);
                                continue;
                            }

                            RenderTexture.active = tempTarget;
                            GL.PushMatrix();
                            GL.LoadPixelMatrix(0, tempTarget.width, 0, tempTarget.height);
                            {
                                copyTerrainLayerMaterial.SetTexture("_MainTex", destinationRenderTexture);
                                copyTerrainLayerMaterial.SetTexture("_OldAlphaMapTexture", sourceRenderTexture);
                                copyTerrainLayerMaterial.SetTexture("_OriginalTargetAlphaMap", targetAlphamapTexture);

                                copyTerrainLayerMaterial.SetTexture("_AlphaMapTexture", alphamapTexture);
                                copyTerrainLayerMaterial.SetVector("_LayerMask", alphamapIndex == targetAlphamapIndex ? layerMasks[targetChannelIndex] : Vector4.zero);
                                copyTerrainLayerMaterial.SetVector("_OriginalTargetAlphaMask", layerMasks[targetChannelIndex]);
                                copyTerrainLayerMaterial.SetPass(1);

                                TerrainPaintUtility.DrawQuad2(t.clippedPCPixels, t.clippedPCPixels, destinationRenderTexture, t.clippedTerrainPixels, alphamapTexture);
                            }
                            GL.PopMatrix();

                            t.terrain.terrainData.CopyActiveRenderTextureToTexture(TerrainData.AlphamapTextureName, alphamapIndex, t.clippedPCPixels, t.clippedTerrainPixels.min, true);
                        }

                        RenderTexture.active = null;
                        OnTerrainPainted(t, ToolAction.PaintTexture);
                    }
                    return null;
                },
                "PaintContext.ScatterAlphamap",
                copyTerrainLayerMaterial, 0);

            RenderTexture.ReleaseTemporary(tempTarget);
        }

        // Collects modified terrain so that we can update some deferred operations at the mouse up event
        private struct PaintedTerrain
        {
            public Terrain terrain;
            public ToolAction action;
        };
        private static List<PaintedTerrain> s_PaintedTerrain = new List<PaintedTerrain>();

        private static void OnTerrainPainted(ITerrainInfo tile, ToolAction action)
        {
            for (int i = 0; i < s_PaintedTerrain.Count; ++i)
            {
                if (tile.terrain == s_PaintedTerrain[i].terrain)
                {
                    var pt = s_PaintedTerrain[i];       // round-about assignment here because of struct copy semantics
                    pt.action |= action;
                    s_PaintedTerrain[i] = pt;
                    return;
                }
            }
            s_PaintedTerrain.Add(new PaintedTerrain { terrain = tile.terrain, action = action });
        }

        public static void ApplyDelayedActions()
        {
            for (int i = 0; i < s_PaintedTerrain.Count; ++i)
            {
                var pt = s_PaintedTerrain[i];
                var terrainData = pt.terrain.terrainData;
                if (terrainData == null)
                    continue;
                if ((pt.action & ToolAction.PaintHeightmap) != 0)
                {
                    terrainData.SyncHeightmap();
                    pt.terrain.editorRenderFlags = TerrainRenderFlags.All;
                }
                if ((pt.action & ToolAction.PaintSurfaceMask) != 0)
                {
                    terrainData.SyncTexture(TerrainData.SurfaceMaskTextureName);
                    pt.terrain.editorRenderFlags = TerrainRenderFlags.All;
                }
                if ((pt.action & ToolAction.PaintTexture) != 0)
                {
                    terrainData.SetBaseMapDirty();
                    terrainData.SyncTexture(TerrainData.AlphamapTextureName);
                }
            }
            s_PaintedTerrain.Clear();
        }
    }
}
