using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Numerics;
using static ECommons.GenericHelpers;

namespace FaloopScreener;

/// <summary>Rings around the player on the vanilla map and minimap showing the range
/// at which the client streams in mobs (~100y in the overworld) - the distance at
/// which Sonar, which relays what the local client sees, can detect a mark.</summary>
internal sealed unsafe class SonarRing(Configuration config)
{
    private readonly Dictionary<uint, (float Factor, Vector2 Offset)> mapInfoCache = [];

    /// <summary>Set via "/windows ringdebug": logs one frame of the map transform
    /// inputs so a misplaced ring can be diagnosed from /xllog.</summary>
    public bool DumpNextFrame;

    public void Draw()
    {
        if ((!config.SonarRingOnMap && !config.SonarRingOnMinimap) || !Svc.ClientState.IsLoggedIn || Svc.GameGui.GameUiHidden)
            return;

        var color = ImGui.ColorConvertFloat4ToU32(config.SonarRingColor);
        if (config.SonarRingOnMap)
            DrawOnAreaMap(color);
        if (config.SonarRingOnMinimap)
            DrawOnNaviMap(color);
    }

    private void DrawOnAreaMap(uint color)
    {
        if (!TryGetAddonByName<AddonAreaMap>("AreaMap", out var addon) || !addon->AtkUnitBase.IsVisible)
            return;

        var agent = AgentMap.Instance();
        if (agent == null || agent->SelectedMapId != agent->CurrentMapId
            || ECommons.GameHelpers.Player.Object is not { } player)
            return;

        ref var areaMap = ref addon->AreaMap;
        var component = areaMap.ComponentMap;
        if (component == null)
            return;

        // The map view transform, as replicated from KamiToolKit's AreaMap overlay:
        // the 2048x2048 texture holds the world at
        //   texture = world * factor + sheetOffset * (factor - 1) + 1024,
        // the view pans by MapOffset (drag) + SelectedOffset and zooms by MapScale,
        // with the content anchored at viewSize/2 + (18, 46) inside the component.
        var (factor, sheetOffset) = MapInfo(agent->SelectedMapId);
        var textureP = new Vector2(player.Position.X, player.Position.Z) * factor
                       + sheetOffset * (factor - 1f) + new Vector2(1024f);

        ref var viewNode = ref component->OwnerNode->AtkResNode;
        var rootScale = addon->AtkUnitBase.Scale;
        var viewMin = new Vector2(viewNode.ScreenX, viewNode.ScreenY);
        var viewSize = new Vector2(viewNode.Width, viewNode.Height);

        var pan = new Vector2(areaMap.MapOffsetX, areaMap.MapOffsetY)
                  + new Vector2(agent->SelectedOffsetX, agent->SelectedOffsetY)
                  + new Vector2(1024f);
        var center = viewMin + (viewSize / 2f + new Vector2(18f, 46f) + (textureP - pan) * areaMap.MapScale) * rootScale;
        var radius = config.SonarRingRadius * factor * areaMap.MapScale * rootScale;

        if (DumpNextFrame)
        {
            DumpNextFrame = false;
            var pin = areaMap.PlayerPin;
            Svc.Log.Information($"[SonarRing] player=({player.Position.X:F1},{player.Position.Z:F1}) factor={factor} sheetOffset={sheetOffset} "
                + $"mapScale={areaMap.MapScale} rootScale={rootScale} mapOffset=({areaMap.MapOffsetX},{areaMap.MapOffsetY}) "
                + $"selOffset=({agent->SelectedOffsetX},{agent->SelectedOffsetY}) viewMin={viewMin} viewSize={viewSize} "
                + $"textureP={textureP} center={center} radius={radius} "
                + $"playerMarker=({areaMap.PlayerMarkerX},{areaMap.PlayerMarkerY}) "
                + (pin != null ? $"pinScreen=({pin->AtkResNode.ScreenX},{pin->AtkResNode.ScreenY}) pinSize=({pin->AtkResNode.Width},{pin->AtkResNode.Height})" : "pin=null"));
        }

        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(viewMin, viewMin + viewSize * rootScale, true);
        drawList.AddCircle(center, radius, color, 0, config.SonarRingThickness);
        drawList.PopClipRect();
    }

    private void DrawOnNaviMap(uint color)
    {
        if (!TryGetAddonByName<AtkUnitBase>("_NaviMap", out var addon) || !addon->IsVisible)
            return;

        var agent = AgentMap.Instance();
        if (agent == null || agent->CurrentMapId == 0)
            return;

        // The minimap is always centered on the player (so its rotation does not
        // matter for a circle); the zoom lives on the zoom button component's
        // image node - same trick MiniMappingway uses.
        var zoom = 1f;
        var zoomContainer = addon->GetNodeById(18);
        if (zoomContainer != null && zoomContainer->GetComponent() != null)
        {
            var image = zoomContainer->GetComponent()->GetImageNodeById(6);
            if (image != null)
                zoom = image->AtkResNode.ScaleX;
        }

        var naviScale = addon->Scale;
        var size = new Vector2(218f * naviScale);
        var min = new Vector2(addon->X, addon->Y);
        var center = min + size / 2f;
        var radius = config.SonarRingRadius * MapInfo(agent->CurrentMapId).Factor * naviScale * zoom;

        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(min, min + size, true);
        drawList.AddCircle(center, radius, color, 0, config.SonarRingThickness);
        drawList.PopClipRect();
    }

    private (float Factor, Vector2 Offset) MapInfo(uint mapId)
    {
        if (mapInfoCache.TryGetValue(mapId, out var cached))
            return cached;

        var row = Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(mapId);
        float factor = row?.SizeFactor > 0 ? row.Value.SizeFactor : 100;
        var offset = new Vector2(row?.OffsetX ?? 0, row?.OffsetY ?? 0);
        return mapInfoCache[mapId] = (factor / 100f, offset);
    }
}
