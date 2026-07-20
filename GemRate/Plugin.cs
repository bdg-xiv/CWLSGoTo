using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GemRate;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/gemrate";
    private const uint BicolorGemstoneItemId = 26807;
    private const int GemstoneCap = 1500;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    private bool windowOpen;
    private int lastCount = -1;
    private ulong lastCharacter;
    private int sessionGained;
    private DateTime? sessionStart;
    private readonly Queue<(DateTime At, int Amount)> recentGains = new();
    private DateTime nextPoll = DateTime.MinValue;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Svc.Commands.AddHandler(CommandName, new CommandInfo((_, _) => windowOpen = !windowOpen)
        {
            HelpMessage = "Toggles the bicolor gemstone rate window."
        });

        Svc.Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.Draw -= DrawWindow;
        Svc.Framework.Update -= OnFrameworkUpdate;
        Svc.Commands.RemoveHandler(CommandName);
        ECommonsMain.Dispose();
    }

    private void ToggleWindow() => windowOpen = !windowOpen;

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll)
            return;
        nextPoll = now.AddMilliseconds(500);

        if (!Svc.ClientState.IsLoggedIn)
        {
            lastCount = -1;
            return;
        }

        // Currency is per character; switching characters must not count as a gain.
        var character = Svc.PlayerState.ContentId;
        if (character != lastCharacter)
        {
            lastCharacter = character;
            lastCount = -1;
            ResetSession();
        }

        var manager = InventoryManager.Instance();
        if (manager == null)
            return;

        var count = manager->GetInventoryItemCount(BicolorGemstoneItemId);
        if (lastCount < 0)
        {
            lastCount = count;
            return;
        }

        if (count > lastCount)
        {
            var gained = count - lastCount;
            sessionGained += gained;
            // Start the clock at the first gain so idle time before farming
            // doesn't drag the rate down.
            sessionStart ??= now;
            recentGains.Enqueue((now, gained));
        }

        lastCount = count;

        while (recentGains.Count > 0 && now - recentGains.Peek().At > RecentWindow)
            recentGains.Dequeue();
    }

    private void ResetSession()
    {
        sessionGained = 0;
        sessionStart = null;
        recentGains.Clear();
    }

    private void DrawWindow()
    {
        if (!windowOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(260, 0), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Bicolor Gemstones###GemRate", ref windowOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var now = DateTime.UtcNow;

            ImGui.Text($"Current: {Math.Max(lastCount, 0):N0} / {GemstoneCap:N0}");
            ImGui.Separator();

            if (sessionStart == null)
            {
                ImGui.TextDisabled("Waiting for the first gemstones...");
            }
            else
            {
                var elapsed = now - sessionStart.Value;
                var overallRate = sessionGained / Math.Max(elapsed.TotalHours, 1.0 / 3600);

                // Rate over the last 15 minutes (or the whole session while shorter),
                // which tracks the current farming pace instead of the whole average.
                var recentSpan = elapsed < RecentWindow ? elapsed : RecentWindow;
                var recentGained = recentGains.Sum(g => g.Amount);
                var recentRate = recentGained / Math.Max(recentSpan.TotalHours, 1.0 / 3600);

                ImGui.Text($"Session: +{sessionGained:N0} in {elapsed:h\\:mm\\:ss}");
                ImGui.Text($"Rate: {overallRate:N0} / hour");
                ImGui.Text($"Last 15 min: {recentRate:N0} / hour");

                var rateForEta = recentRate > 0 ? recentRate : overallRate;
                var remaining = GemstoneCap - lastCount;
                if (remaining > 0 && rateForEta > 0)
                {
                    var eta = TimeSpan.FromHours(remaining / rateForEta);
                    ImGui.Text($"Cap in: ~{(int)eta.TotalHours}h {eta.Minutes:D2}m");
                }
                else if (remaining <= 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.8f, 0.2f, 1f), "At the cap!");
                }
            }

            ImGui.Separator();
            if (ImGui.Button("Reset"))
                ResetSession();
        }
        ImGui.End();
    }
}
