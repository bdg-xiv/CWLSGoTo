using clib.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace clib.Extensions;

public static class ContentFinderConditionExtensions {
    extension(ContentFinderCondition row) {
        public string NameFormatted => Svc.SeStringEvaluator.EvaluateFromAddon(163, [row.Name]).ToString();
        public bool IsInRoulette
            => row.LevelingRoulette || row.HighLevelRoulette || row.MSQRoulette || row.GuildHestRoulette || row.ExpertRoulette || row.TrialRoulette || row.DailyFrontlineChallenge || row.LevelCapRoulette || row.MentorRoulette || row.AllianceRoulette || row.FeastTeamRoulette || row.NormalRaidRoulette || row.CrystallineConflictCasualRoulette || row.CrystallineConflictRankedRoulette;

        public unsafe void QueueDuty(bool levelSync) {
            if (!row.IsInDutyFinder)
                return;

            var queueInfo = ContentsFinder.Instance()->GetQueueInfo();
            if (queueInfo->QueueState is ContentsFinderQueueState.Pending or ContentsFinderQueueState.Queued)
                queueInfo->CancelQueue();

            ContentsFinder.Instance()->ResetFlags();
            if (levelSync)
                ContentsFinder.Instance()->IsLevelSync = true;
            else
                ContentsFinder.Instance()->IsUnrestrictedParty = true;

            var duty = row.RowId;
            // TODO: roulette
            queueInfo->QueueDuties(&duty, 1);
        }
    }
}
