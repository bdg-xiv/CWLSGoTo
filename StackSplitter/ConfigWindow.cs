using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Numerics;

namespace StackSplitter.Windows;

public class ConfigWindow : Window
{
    private readonly Configuration configuration;

    public ConfigWindow(Configuration configuration) : base("Stack Splitter###StackSplitterSettings")
    {
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize;
        Size = new Vector2(340, 120);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.configuration = configuration;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Right-clicking a stacked item in your bags offers to split it into stacks of this size.");
        ImGui.Separator();

        var stackSize = configuration.StackSize;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("Stack size", ref stackSize))
        {
            configuration.StackSize = Math.Clamp(stackSize, Configuration.MinStackSize, Configuration.MaxStackSize);
            configuration.Save();
        }

        ImGui.TextDisabled($"Between {Configuration.MinStackSize} and {Configuration.MaxStackSize}.\n"
            + "The menu entry only shows up on stacks larger than this.");
    }
}
