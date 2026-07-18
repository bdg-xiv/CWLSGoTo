using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using ECommons;
using ECommons.DalamudServices;
using ECommons.Reflection;
using ModSnap.Windows;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ModSnap;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => PluginInterface.Manifest.Name;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static Dalamud.Plugin.Services.IContextMenu ContextMenu { get; private set; } = null!;

    private const string CommandName = "/modsnap";

    public Configuration Configuration { get; }
    public bool PenumbraAvailable { get; private set; }
    public bool SnapshotInProgress { get; private set; }
    public string? LastResult { get; set; }
    public string? LastPcpFile { get; private set; }

    // Optional label used as the PCP "note": it becomes the suffix of the mod,
    // collection and Glamourer design names. Empty means a timestamp is used.
    public string NextLabel = string.Empty;

    public readonly WindowSystem WindowSystem = new("ModSnap");
    private readonly MainWindow mainWindow;

    private readonly ApiVersion penumbraApiVersion;
    private readonly InstallMod installMod;

    private DateTime lastAvailabilityCheck = DateTime.MinValue;

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        penumbraApiVersion = new ApiVersion(PluginInterface);
        installMod = new InstallMod(PluginInterface);

        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(mainWindow);

        ContextMenu.OnMenuOpened += OnMenuOpened;

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Snapshots your current target's mods + appearance into Penumbra/Glamourer. \"/modsnap self\" snapshots you, \"/modsnap cfg\" opens the window.",
        });
    }

    private void ToggleMainWindow() => mainWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();
        if (arg.Equals("cfg", StringComparison.OrdinalIgnoreCase) || arg.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ToggleMainWindow();
            return;
        }

        ICharacter? character;
        if (arg.Equals("self", StringComparison.OrdinalIgnoreCase))
            character = Svc.Objects.LocalPlayer;
        else
            character = Svc.Targets.Target as ICharacter;

        if (character == null)
        {
            Svc.Chat.PrintError("[ModSnap] No valid target. Target a character (or use \"/modsnap self\").");
            return;
        }

        SnapshotCharacter(character.ObjectIndex, character.Name.TextValue);
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!Configuration.ContextMenuEnabled)
            return;
        if (args.MenuType != ContextMenuType.Default)
            return;
        if (args.Target is not MenuTargetDefault target)
            return;

        // The actor must actually be rendered nearby: the snapshot reads the model's
        // currently loaded resources, so a party member on another map is no use.
        if (target.TargetObject is not ICharacter character)
            return;
        if (Configuration.PlayersOnly && character is not IPlayerCharacter)
            return;

        var objectIndex = character.ObjectIndex;
        var name = character.Name.TextValue;
        args.AddMenuItem(new MenuItem
        {
            Name = "Save Mods + Appearance",
            UseDefaultPrefix = true,
            OnClicked = _ => SnapshotCharacter(objectIndex, name),
        });
    }

    public void SnapshotCharacter(ushort objectIndex, string displayName)
    {
        if (SnapshotInProgress)
        {
            Svc.Chat.PrintError("[ModSnap] A snapshot is already in progress.");
            return;
        }

        if (!CheckPenumbraAvailable(force: true))
        {
            Svc.Chat.PrintError("[ModSnap] Penumbra is not available.");
            return;
        }

        var label = NextLabel.Trim();
        if (label.Length == 0)
            label = DateTime.Now.ToString("yyyy-MM-dd HH_mm");

        SnapshotInProgress = true;
        Svc.Chat.Print($"[ModSnap] Snapshotting {displayName}...");
        Task.Run(async () =>
        {
            try
            {
                await DoSnapshot(objectIndex, displayName, label);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "Snapshot failed");
                ReportFailure($"Snapshot failed: {ex.Message}");
            }
            finally
            {
                SnapshotInProgress = false;
            }
        });
    }

    private async Task DoSnapshot(ushort objectIndex, string displayName, string label)
    {
        var (success, result) = await InvokeCreatePcp(objectIndex, label);
        if (!success)
        {
            ReportFailure(result);
            return;
        }

        LastPcpFile = result;

        var ec = await Svc.Framework.RunOnFrameworkThread(() => installMod.Invoke(result));
        if (ec != PenumbraApiEc.Success)
        {
            ReportFailure($"Penumbra refused to install the created pack ({ec}). File kept at: {result}");
            return;
        }

        LastResult = $"Saved {displayName} ({label}).";
        Svc.Chat.Print($"[ModSnap] Saved {displayName}: Penumbra is installing the mod into its PCP folder (with a PCP/{displayName} collection), " +
                       "and Glamourer should add a matching design in its PCP folder. The .pcp file is kept in Penumbra's export folder.");
    }

    private void ReportFailure(string message)
    {
        LastResult = message;
        Svc.Chat.PrintError($"[ModSnap] {message}");
    }

    // The PCP export itself is not exposed over IPC (only installing packs is), so this
    // drives Penumbra's internal PcpService.CreatePcp via reflection - the same feature
    // behind "Export Character Pack" on Penumbra's On-Screen tab. Penumbra then gathers
    // the actor's files, meta manipulations and (via its IPC events) the Glamourer
    // design and Customize+ profile into one .pcp file.
    private async Task<(bool Success, string Result)> InvokeCreatePcp(ushort objectIndex, string note)
    {
        if (!DalamudReflector.TryGetDalamudPlugin("Penumbra", out var penumbra, out _, true, true) || penumbra == null)
            return (false, "Penumbra is not loaded.");

        object? services = null;
        foreach (var field in penumbra.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (field.FieldType.Name == "ServiceManager")
            {
                services = field.GetValue(penumbra);
                break;
            }
        }

        if (services == null)
            return (false, "Could not find Penumbra's service manager. A Penumbra update may have broken Mod Snap.");

        var pcpType = penumbra.GetType().Assembly.GetType("Penumbra.Services.PcpService");
        if (pcpType == null)
            return (false, "This Penumbra version has no Character Pack (PCP) support. Update Penumbra.");

        object? pcpService = null;
        if (services.GetType().GetProperty("Provider")?.GetValue(services) is IServiceProvider provider)
            pcpService = provider.GetService(pcpType);
        if (pcpService == null)
        {
            var getService = services.GetType().GetMethods()
                .FirstOrDefault(m => m is { Name: "GetService", IsGenericMethodDefinition: true } && m.GetParameters().Length == 0);
            if (getService != null)
                pcpService = getService.MakeGenericMethod(pcpType).Invoke(services, null);
        }

        if (pcpService == null)
            return (false, "Could not resolve Penumbra's PCP service. A Penumbra update may have broken Mod Snap.");

        var method = pcpType.GetMethod("CreatePcp");
        if (method == null)
            return (false, "Penumbra's PCP service has no CreatePcp method. A Penumbra update may have broken Mod Snap.");

        // CreatePcp(ObjectIndex objectIndex, string? modPath, string note = "", CancellationToken cancel = default).
        // ObjectIndex is Penumbra's wrapper struct around the ushort index; a null path
        // means the file is written to Penumbra's configured export directory.
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (i == 0)
                args[i] = Activator.CreateInstance(p.ParameterType, objectIndex);
            else if (p.ParameterType == typeof(string) && !p.HasDefaultValue)
                args[i] = null;
            else if (p.ParameterType == typeof(string))
                args[i] = note;
            else if (p.ParameterType == typeof(CancellationToken))
                args[i] = CancellationToken.None;
            else
                args[i] = p.HasDefaultValue ? p.DefaultValue : null;
        }

        if (method.Invoke(pcpService, args) is not Task task)
            return (false, "Penumbra's CreatePcp did not return a task. A Penumbra update may have broken Mod Snap.");

        await task.ConfigureAwait(false);

        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        if (result == null)
            return (false, "Penumbra's CreatePcp returned no result. A Penumbra update may have broken Mod Snap.");

        var resultType = result.GetType();
        if (resultType.GetField("Item1")?.GetValue(result) is not bool ok ||
            resultType.GetField("Item2")?.GetValue(result) is not string text)
            return (false, "Penumbra's CreatePcp result had an unexpected shape. A Penumbra update may have broken Mod Snap.");

        return (ok, text);
    }

    public bool CheckPenumbraAvailable(bool force = false)
    {
        if (!force && DateTime.UtcNow - lastAvailabilityCheck < TimeSpan.FromSeconds(2))
            return PenumbraAvailable;

        lastAvailabilityCheck = DateTime.UtcNow;
        try
        {
            PenumbraAvailable = penumbraApiVersion.Invoke().Breaking == 5;
        }
        catch
        {
            PenumbraAvailable = false;
        }

        return PenumbraAvailable;
    }

    public void Dispose()
    {
        ContextMenu.OnMenuOpened -= OnMenuOpened;

        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainWindow;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();

        Svc.Commands.RemoveHandler(CommandName);

        ECommonsMain.Dispose();
    }
}
