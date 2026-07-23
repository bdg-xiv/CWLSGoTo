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
    private readonly Dictionary<uint, float> mapSizeFactors = [];

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

        ref var areaMap = ref addon->AreaMap;
        var pin = areaMap.PlayerPin;
        if (pin == null || (pin->AtkResNode.NodeFlags & NodeFlags.Visible) == 0)
            return; // the pin hides while browsing another zone's map

        var component = areaMap.ComponentMap;
        var agent = AgentMap.Instance();
        if (component == null || agent == null)
            return;

        // The game keeps the player pin positioned on the map every frame; ring
        // around it, scaled by texture px/yalm (SizeFactor/100) x zoom x ui scale.
        var rootScale = addon->AtkUnitBase.Scale;
        ref var pinNode = ref pin->AtkResNode;
        var center = new Vector2(pinNode.ScreenX, pinNode.ScreenY)
                     + new Vector2(pinNode.Width, pinNode.Height) * pinNode.ScaleX * rootScale / 2f;
        var radius = config.SonarRingRadius * SizeFactor(agent->SelectedMapId) * areaMap.MapScale * rootScale;

        ref var viewNode = ref component->OwnerNode->AtkResNode;
        var viewMin = new Vector2(viewNode.ScreenX, viewNode.ScreenY);
        var viewMax = viewMin + new Vector2(viewNode.Width, viewNode.Height) * rootScale;

        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(viewMin, viewMax, true);
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
        var radius = config.SonarRingRadius * SizeFactor(agent->CurrentMapId) * naviScale * zoom;

        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(min, min + size, true);
        drawList.AddCircle(center, radius, color, 0, config.SonarRingThickness);
        drawList.PopClipRect();
    }

    private float SizeFactor(uint mapId)
    {
        if (mapSizeFactors.TryGetValue(mapId, out var cached))
            return cached;

        float factor = Svc.Data.GetExcelSheet<Map>().GetRowOrDefault(mapId)?.SizeFactor ?? 100;
        return mapSizeFactors[mapId] = (factor == 0 ? 100 : factor) / 100f;
    }
}
