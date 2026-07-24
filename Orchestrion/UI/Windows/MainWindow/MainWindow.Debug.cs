using System.Numerics;
using CheapLoc;
using Dalamud.Bindings.ImGui;
using Orchestrion.Audio;
using Orchestrion.BGMSystem;

namespace Orchestrion.UI.Windows.MainWindow;

public partial class MainWindow
{
	private void DrawDebugTab()
	{
		var addr = BGMAddressResolver.BGMSceneManager;
		if (addr == IntPtr.Zero) return;
		var addrStr = $"{addr.ToInt64():X}";
		ImGui.Text(addrStr);
		if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
			ImGui.SetClipboardText(addrStr);
		ImGui.Text($"streaming enabled: {BGMAddressResolver.StreamingEnabled}");
		ImGui.Separator();
		ImGui.Text($"OldSongId: {BGMManager.OldSongId}");
		ImGui.Text($"OldScene: {BGMManager.OldScene}");
		ImGui.Dummy(new Vector2(10, 10));
		ImGui.Text($"CurrentSongId: {BGMManager.CurrentSongId}");
		ImGui.Text($"CurrentScene: {BGMManager.CurrentScene}");
		ImGui.Dummy(new Vector2(10, 10));
		ImGui.Text($"SecondSongId: {BGMManager.SecondSongId}");
		ImGui.Text($"SecondScene: {BGMManager.SecondScene}");
		ImGui.Dummy(new Vector2(10, 10));
		ImGui.Text($"PlayingSongId: {BGMManager.PlayingSongId}");
		ImGui.Text($"PlayingScene: {BGMManager.PlayingScene}");
		ImGui.Dummy(new Vector2(10, 10));
		ImGui.Text($"Audible: {BGMManager.CurrentAudibleSong}");
		ImGui.Separator();
		ImGui.Text($"Inn: {BGMManager.InnMusicActive()}");
		ImGui.Separator();
		ImGui.Text($"DD: {BGMManager.DeepDungeonModeActive()}");
		ImGui.Separator();
		if (ImGui.Button("export loc"))
		{
			Loc.ExportLocalizable(true);
		}
		
		
	}
}