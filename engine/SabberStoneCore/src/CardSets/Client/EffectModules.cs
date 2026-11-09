using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SabberStoneCore.Model;

namespace SabberStoneCore.CardSets.Client
{
    /// <summary>Typed card modules keyed by dbfId, applied after legacy definitions are loaded.</summary>
    public static class EffectModules
    {
        private static readonly Dictionary<int, string> Modules = new Dictionary<int, string>();
        private static readonly IReadOnlyDictionary<int, string> Registry = new ReadOnlyDictionary<int, string>(Modules);
        public static IReadOnlyDictionary<int, string> ByDbfId { get { _ = Cards.Count; return Registry; } }

        internal static void Apply(Dictionary<string, Card> cards)
        {
            void Register(int dbfId, string id, Action<Card> configure = null)
            {
                var card = cards[id];
                if (card.AssetId != dbfId) throw new InvalidOperationException("Effect module identity mismatch: " + id);
                configure?.Invoke(card);
                if (card.Dormant != null) Actions.DormantRules.Validate(card, cards);
                Modules.Add(dbfId, id);
            }
            Register(BuildABeast.DbfId, "ICC_828p", BuildABeast.Configure);

            ClassHeroes.Apply(Register);
            // Solo-adventure cards the multiplayer run plays with (bosses, drafted hero powers, treasures).
            DelveCards.Apply(Register);

            Sets.LocationCards.Apply(Register);
            // Per-set registries also define the collectible stress corpus.
            void RegisterStandard(int dbfId, string id, Action<Card> configure)
            {
                Register(dbfId, id, configure);
                StandardSets.Record(cards[id]);
            }
            Sets.BECards.Apply(RegisterStandard);
            Sets.CataclysmCards.Apply(RegisterStandard);
            Sets.EmeraldDreamCards.Apply(RegisterStandard);
            Sets.EscapeFromVioletHoldCards.Apply(RegisterStandard);
            Sets.TheLostCityCards.Apply(RegisterStandard);
            Sets.TimeTravelCards.Apply(RegisterStandard);
            // Current CORE cards whose legacy originals are unscripted register like Standard set cards.
            Sets.CoreNeutralACards.Apply(RegisterStandard);
            Sets.CoreNeutralBCards.Apply(RegisterStandard);
            Sets.CorePriestPaladinCards.Apply(RegisterStandard);
            Sets.CoreMageWarriorShamanCards.Apply(RegisterStandard);
            Sets.CoreDemonHunterCards.Apply(RegisterStandard);
            Sets.CoreHunterRogueCards.Apply(RegisterStandard);
            Sets.CoreDruidWarlockCards.Apply(RegisterStandard);
            Sets.CoreDeathKnightCards.Apply(RegisterStandard);

            StandardCardAttribute.Apply(RegisterStandard);

            // Components retain their data and legacy dynamic-card composition, not empty replacement powers.
            foreach (var component in BuildABeast.Components) Register(component.Key, component.Value);
        }
    }
}
