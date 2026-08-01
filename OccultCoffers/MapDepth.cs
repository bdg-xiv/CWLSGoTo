using System;
using System.Reflection;
using ECommons.DalamudServices;
using static ECommons.GenericHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.MapOverlay;
using KamiToolKit.Nodes;

namespace OccultCoffers;

/// <summary>
/// Keeps our marker layer above everyone else's.
///
/// KamiToolKit hangs its overlay off map node 53 with NodePosition.AfterTarget, so any other
/// plugin doing the same lands in the same sibling chain and whoever attached last wins. The
/// chain runs through PrevSiblingNode with the far end drawn on top, so being on top means
/// having no PrevSibling - which is cheap to check and cheap to fix by re-emplacing at the
/// end of the chain.
/// </summary>
internal sealed class MapDepth
{
    private static readonly FieldInfo? ContainerField = typeof(MapOverlayController)
        .GetField("clippingContainerNode", BindingFlags.Instance | BindingFlags.NonPublic);

    private const uint MapContainerNodeId = 53;

    private bool givenUp;
    private bool warned;

    /// <summary>Puts the layer back on top if something has stacked on top of it.</summary>
    public unsafe void Enforce(MapOverlayController controller)
    {
        if (givenUp || ContainerField == null)
        {
            WarnOnce("KamiToolKit no longer exposes the map overlay container; markers will draw in load order.");
            return;
        }

        try
        {
            if (ContainerField.GetValue(controller) is not ResNode container || container.Node == null)
                return;

            // No PrevSibling means nothing is drawn over us - already where we want to be.
            if (container.Node->PrevSiblingNode == null)
                return;

            if (!TryGetAddonByName<AtkUnitBase>("AreaMap", out var addon) || !IsAddonReady(addon))
                return;

            var anchor = addon->GetNodeById(MapContainerNodeId);
            if (anchor == null)
                return;

            // Unlink before re-emplacing: attaching a node that is still wired into its old
            // position is how the tree gets corrupted rather than reordered.
            container.DetachNode();
            container.AttachNode(anchor, NodePosition.AfterAllSiblings);
        }
        catch (Exception ex)
        {
            givenUp = true;
            Svc.Log.Error(ex, "Could not reorder the map overlay; leaving it in load order");
        }
    }

    private void WarnOnce(string message)
    {
        if (warned)
            return;

        warned = true;
        givenUp = true;
        Svc.Log.Warning($"[OccultCoffers] {message}");
    }
}
