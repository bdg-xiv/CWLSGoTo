using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System.Threading.Tasks;

namespace ComplexTweaks.Tweaks;

public class ARQuestingConfiguration {
    [BoolConfig] public bool ReturnHome = true;
}

[Tweak]
[Requires(Ipc.AutoRetainer | Ipc.Lifestream | Ipc.Questionable)]
public class ARQuesting : ARTweak<ARQuestingConfiguration> {
    public override string Name => "AutoRetainer x Questionable";
    public override string Description => "On CharacterPostProcess, do any seasonal quests that are available.";

    private List<string> _quests = [];

    public override void OnCharacterPostProcessStep() {
        if (Service.Questionable.GetCurrentlyActiveEventQuests() is { Count: > 0 } quests) {
            _quests = quests;
            AutoRetainer.RequestCharacterPostprocess();
        }
        else
            Log("Skipping post process for character: no seasonal quests available.");
    }

    public override void OnCharacterReadyToPostProcess() => Svc.Automation.Start(new RunQuestionable(_quests, Config.ReturnHome), AutoRetainer.FinishCharacterPostProcess);

    private sealed class RunQuestionable(List<string> questIds, bool returnHome) : TaskBase {
        protected override async Task Execute() {
            foreach (var quest in questIds) {
                Status = $"Doing quest #{quest}";
                if (Service.Questionable.StartSingleQuest(quest))
                    await WaitWhile(() => !IsQuestComplete(quest), $"QuestionableWaitForFinish{quest}", 120);
                else
                    Error($"Failed to start quest #{quest}");
            }
            if (returnHome) {
                Status = "Going home";
                Service.Lifestream.ExecuteCommand("auto");
                await WaitUntilThenFalse(() => Service.Lifestream.IsBusy(), "LifestreamWaitForFinish");
            }
        }

        private unsafe bool IsQuestComplete(string questId) {
            if (uint.TryParse(questId, out var id) && QuestManager.IsQuestComplete(id))
                return true;
            if (questId.StartsWith('U') && ushort.TryParse(questId.AsSpan(1), out var unlockLinkId) && UIState.Instance()->IsUnlockLinkUnlocked(unlockLinkId))
                return true;
            return false;
        }
    }
}
