using Dalamud.Game.Agent.AgentArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace clib.Extensions;

public static unsafe class AgentReceiveEventArgsExtensions {
    extension(AgentReceiveEventArgs args) {
        public Span<AtkValue> AtkValues => new((void*)args.AtkValues, (int)args.ValueCount);
    }
}
