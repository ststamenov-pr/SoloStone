// SPDX-License-Identifier: AGPL-3.0-or-later
// Solo-adventure cards used by the multiplayer run: boss hero powers, the extra hero powers a player can draft,
// and passive treasures. The card data (names, text, numbers) already ships in the facts file; the adventure
// scripts were removed from this engine, so the ones the run needs are written here.
using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Auras;
using SabberStoneCore.Conditions;
using SabberStoneCore.Enchants;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;
using SabberStoneCore.Tasks;
using SabberStoneCore.Tasks.SimpleTasks;
using SabberStoneCore.Triggers;

namespace SabberStoneCore.CardSets.Client
{
    public static class DelveCards
    {
        // Passives whose whole effect happens once, when the game is set up.
        private static readonly Dictionary<string, Action<Controller>> Setups = new Dictionary<string, Action<Controller>>(StringComparer.Ordinal)
        {
            // Potion of Vitality: Double your starting Health.
            ["LOOTA_800"] = c => { c.Hero.BaseHealth = c.Hero.BaseHealth * 2; },
            // Crystal Gem: Start with an extra Mana Crystal.
            ["LOOTA_801"] = c => { c.BaseMana = Math.Min(10, c.BaseMana + 1); },
            // Small Backpacks: At the start of the game, draw 2 cards.
            ["LOOTA_804"] = c => { Actions.Generic.Draw(c); Actions.Generic.Draw(c); },
            // Robe of the Magi: Spell Damage +3.
            ["LOOTA_825"] = c => { c.Hero[GameTag.SPELLPOWER] = c.Hero[GameTag.SPELLPOWER] + 3; },
            // Battle Totem: Your Battlecries trigger twice.
            ["LOOTA_846"] = c => { c[GameTag.EXTRA_BATTLECRIES_BASE] = 1; },
        };

        /// <summary>Every passive this file implements (treasures and passive boss hero powers).</summary>
        public static IReadOnlyCollection<string> Passives { get; } = new[]
        {
            "LOOTA_800", "LOOTA_801", "LOOTA_804", "LOOTA_824", "LOOTA_825", "LOOTA_828", "LOOTA_831", "LOOTA_846",
            "LOOTA_BOSS_47p", "LOOTA_BOSS_31p", "LOOTA_BOSS_19p",
        };

        private static void Target(Card card, params PlayReq[] reqs)
        {
            var map = new Dictionary<PlayReq, int> { { PlayReq.REQ_TARGET_TO_PLAY, 0 } };
            foreach (var r in reqs) map[r] = 0;
            card.SetPlayRequirements(map);
        }

        private static Minion[] Alive(Controller c) => c.BoardZone.Where(m => !m.IsDead && !m.ToBeDestroyed).ToArray();

        private static void Hit(Game g, Controller c, IEntity source, ICharacter target, int amount)
        {
            if (target == null || target.IsDead) return;
            new DamageTask(amount, EntityType.TARGET).Process(g, c, source, (IPlayable)target);
        }

        private static CustomTask RandomEnemies(int hits, int amount) => new CustomTask((g, c, s, t, stack) =>
        {
            for (int i = 0; i < hits; i++)
            {
                var pool = new List<ICharacter> { c.Opponent.Hero };
                pool.AddRange(Alive(c.Opponent));
                Hit(g, c, s, pool[g.Random.Next(pool.Count)], amount);
            }
        });

        private static void Buff(Minion m, int attack, int health)
        {
            if (attack != 0) m.AttackDamage = Math.Max(0, m.AttackDamage + attack);
            if (health != 0) m.BaseHealth = m.BaseHealth + health;
        }

        internal static void Apply(Action<int, string, Action<Card>> register)
        {
            void Power(int dbf, string id, ISimpleTask task, Action<Card> more = null) => register(dbf, id, card =>
            {
                card.Power = new Power { PowerTask = task };
                card.Implemented = true;
                card.ExcludeFromLegacyRandomPools = true;
                more?.Invoke(card);
            });
            void Passive(int dbf, string id, Power power) => register(dbf, id, card =>
            {
                card.Power = power;
                card.Implemented = true;
                card.ExcludeFromLegacyRandomPools = true;
            });
            var slot = new Dictionary<PlayReq, int> { { PlayReq.REQ_NUM_MINION_SLOTS, 1 } };

            // --- boss hero powers -----------------------------------------------------------------------------------
            // Rat Race: Summon two 1/1 Rats.
            Power(45894, "LOOTA_BOSS_18p", new SummonTask("LOOTA_BOSS_18t", 2), card => card.SetPlayRequirements(slot));
            // Baby Breath: Deal 2 damage.
            Power(47308, "LOOTA_BOSS_44p", new DamageTask(2, EntityType.TARGET), card => Target(card));
            // Mystic Barrier: Gain 3 Armor.
            Power(47304, "LOOTA_BOSS_42p", new ArmorTask(3));
            // Unstable Explosion: Deal 1 damage to two random enemies.
            Power(46151, "LOOTA_BOSS_05p", RandomEnemies(2, 1));
            // Unstable Demolition: Deal 2 damage to three random enemies.
            Power(46271, "LOOTA_BOSS_28p", RandomEnemies(3, 2));
            // Giant Stomp: Deal 1 damage to all enemy minions.
            Power(47257, "LOOTA_BOSS_39p", new DamageTask(1, EntityType.OP_MINIONS));
            // Fungal Infection: Give your minions +1/+1.
            Power(47319, "LOOTA_BOSS_46p", new CustomTask((g, c, s, t, stack) => { foreach (var m in Alive(c)) Buff(m, 1, 1); }));

            // The Floor is Lava (passive): After a minion is played, deal 2 damage to it.
            Passive(47321, "LOOTA_BOSS_47p", new Power
            {
                Trigger = new Trigger(TriggerType.AFTER_PLAY_MINION)
                {
                    TriggerSource = TriggerSource.ALL, EitherTurn = true,
                    SingleTask = new CustomTask((g, c, s, t, stack) => Hit(g, c, s, t as ICharacter, 2)),
                },
            });
            // Charrrrrge! (passive): After you play a minion, give it Charge.
            Passive(46319, "LOOTA_BOSS_31p", new Power
            {
                Trigger = new Trigger(TriggerType.AFTER_PLAY_MINION)
                {
                    TriggerSource = TriggerSource.FRIENDLY,
                    SingleTask = new CustomTask((g, c, s, t, stack) => { if (t is Minion m && !m.IsDead) { m.HasCharge = true; m.IsExhausted = false; } }),
                },
            });
            // Digest Magic (passive): Whenever your opponent casts a spell, summon a Tunnel Trogg.
            Passive(46205, "LOOTA_BOSS_19p", new Power
            {
                Trigger = new Trigger(TriggerType.CAST_SPELL)
                {
                    TriggerSource = TriggerSource.ENEMY, EitherTurn = true,
                    SingleTask = new SummonTask("LOE_018"),
                },
            });

            // --- hero powers a player can draft (Dalaran Heist) -----------------------------------------------------
            // Lifebloom: Restore a minion to full Health.
            Power(53275, "DALA_Druid_HP1", new HealFullTask(EntityType.TARGET), card => Target(card, PlayReq.REQ_MINION_TARGET));
            // Touch of Bark: Give a minion +1/+1.
            Power(53277, "DALA_Druid_HP2", new CustomTask((g, c, s, t, stack) => { if (t is Minion m) Buff(m, 1, 1); }),
                card => Target(card, PlayReq.REQ_MINION_TARGET));
            // Opportunist: Give a minion +2 Attack this turn.
            Power(53279, "DALA_Hunter_HP1", new AddEnchantmentTask("DALA_Hunter_HP1e", EntityType.TARGET),
                card => Target(card, PlayReq.REQ_MINION_TARGET));
            register(53280, "DALA_Hunter_HP1e", card =>
            {
                card.Power = new Power { Enchant = new Enchant(Effects.Attack_N(2)) { IsOneTurnEffect = true } };
                card.Implemented = true;
            });
            // Burning Wit: Reduce the cost of a random card in your hand by (2).
            Power(53283, "DALA_Mage_HP1", new CustomTask((g, c, s, t, stack) =>
            {
                var hand = c.HandZone.Where(p => p.Cost > 0).ToArray();
                if (hand.Length == 0) return;
                var pick = hand[g.Random.Next(hand.Length)];
                pick.Cost = Math.Max(0, pick.Cost - 2);
            }));
            // Frostburn: Freeze a character. If it's already Frozen, deal 2 damage.
            Power(53285, "DALA_Mage_HP2", new CustomTask((g, c, s, t, stack) =>
            {
                if (!(t is ICharacter ch)) return;
                if (ch.IsFrozen) Hit(g, c, s, ch, 2);
                else ch.IsFrozen = true;
            }), card => Target(card));
            // Backup: Add three 1/1 Silver Hand Recruits to your hand.
            Power(53286, "DALA_Paladin_HP1", new AddCardTo("CS2_101t", EntityType.HAND, 3));
            // Boon of Light: Give a friendly minion Divine Shield.
            Power(53287, "DALA_Paladin_HP2", new CustomTask((g, c, s, t, stack) => { if (t is Minion m) m.HasDivineShield = true; }),
                card => Target(card, PlayReq.REQ_MINION_TARGET, PlayReq.REQ_FRIENDLY_TARGET));
            // Distort: Swap a minion's Attack and Health.
            Power(53288, "DALA_Priest_HP1", new CustomTask((g, c, s, t, stack) =>
            {
                if (!(t is Minion m) || m.IsDead) return;
                int attack = m.AttackDamage, health = m.Health;
                m.AttackDamage = health;
                m.BaseHealth = attack;
                m.Damage = 0;
            }), card => Target(card, PlayReq.REQ_MINION_TARGET));
            // Yoink!: Add a random card to your hand (from another class).
            Power(53291, "DALA_Rogue_HP1", new CustomTask((g, c, s, t, stack) =>
            {
                var pool = Cards.All.Where(k => k.Collectible && k.Implemented && k.Class != c.HeroClass && k.Class != CardClass.NEUTRAL
                    && k.Class != CardClass.INVALID && (k.Type == CardType.MINION || k.Type == CardType.SPELL || k.Type == CardType.WEAPON)).ToArray();
                if (pool.Length == 0 || c.HandZone.IsFull) return;
                new AddCardTo(pool[g.Random.Next(pool.Length)], EntityType.HAND).Process(g, c, s, null);
            }));
            // Cut-less: Deal 2 damage to an undamaged minion.
            Power(53293, "DALA_Rogue_HP2", new DamageTask(2, EntityType.TARGET),
                card => Target(card, PlayReq.REQ_MINION_TARGET, PlayReq.REQ_UNDAMAGED_TARGET));
            // Pain Split: Take 2 damage. Deal 2 damage.
            Power(53296, "DALA_Warlock_HP1", new CustomTask((g, c, s, t, stack) =>
            {
                Hit(g, c, s, c.Hero, 2);
                Hit(g, c, s, t as ICharacter, 2);
            }), card => Target(card));
            // The Pact: Take 2 damage. Summon a 2/2 Imp.
            Power(53297, "DALA_Warlock_HP2", new CustomTask((g, c, s, t, stack) =>
            {
                Hit(g, c, s, c.Hero, 2);
                new SummonTask("DALA_Warlock_HP2t").Process(g, c, s, null);
            }), card => card.SetPlayRequirements(slot));
            // Invigorating Brew: Deal 1 damage to a minion and give it +2 Attack.
            Power(53299, "DALA_Warrior_HP1", new CustomTask((g, c, s, t, stack) =>
            {
                if (!(t is Minion m)) return;
                Hit(g, c, s, m, 1);
                if (!m.IsDead) Buff(m, 2, 0);
            }), card => Target(card, PlayReq.REQ_MINION_TARGET));

            // --- passive treasures (Dungeon Run) --------------------------------------------------------------------
            foreach (var pair in new[] { (46405, "LOOTA_800"), (46407, "LOOTA_801"), (46411, "LOOTA_804"), (46430, "LOOTA_825"), (47300, "LOOTA_846") })
                register(pair.Item1, pair.Item2, card => { card.Implemented = true; card.ExcludeFromLegacyRandomPools = true; });
            // Khadgar's Scrying Orb: Your spells cost (1) less.
            Passive(46428, "LOOTA_824", new Power { Aura = new Aura(AuraType.HAND, Effects.ReduceCost(1)) { Condition = SelfCondition.IsSpell } });
            // Captured Flag: Your minions have +1/+1.
            Passive(46439, "LOOTA_828", new Power { Aura = new Aura(AuraType.BOARD, Effects.AttackHealth_N(1)) });
            // Glyph of Warding: Enemy minions cost (1) more.
            Passive(46925, "LOOTA_831", new Power { Aura = new Aura(AuraType.OP_HAND, Effects.AddCost(1)) { Condition = SelfCondition.IsMinion } });
        }

        /// <summary>
        /// Puts a passive (treasure or passive hero power) into effect for <paramref name="controller"/>. Call it after
        /// <see cref="Game.StartGame"/>, in the same order on every machine that simulates the game.
        /// </summary>
        public static void InstallPassive(Controller controller, string cardId)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (!Passives.Contains(cardId)) throw new ArgumentException("Not a multiplayer passive: " + cardId, nameof(cardId));
            if (Setups.TryGetValue(cardId, out var setup)) setup(controller);
            Card card = Cards.FromId(cardId);
            if (card.Power?.Trigger == null && card.Power?.Aura == null) return;
            // The passive lives on an entity of its own, set aside, so its trigger or aura has an owner.
            IPlayable holder = controller.Hero.HeroPower != null && controller.Hero.HeroPower.Card.Id == cardId
                ? controller.Hero.HeroPower
                : Entity.FromCard(controller, card);
            if (holder.Zone == null) controller.SetasideZone.Add(holder);
            card.Power.Trigger?.Activate(holder);
            card.Power.Aura?.Activate(holder);
            controller.Game.AuraUpdate();
        }

        /// <summary>Replaces the controller's hero power, as a hero card swap would.</summary>
        public static void SetHeroPower(Controller controller, string cardId)
        {
            if (controller == null) throw new ArgumentNullException(nameof(controller));
            if (controller.Hero.HeroPower != null && controller.Hero.HeroPower.Card.Id == cardId) return;
            Card card = Cards.FromId(cardId) ?? throw new ArgumentException("Unknown hero power: " + cardId, nameof(cardId));
            if (card.Type != CardType.HERO_POWER) throw new ArgumentException("Not a hero power: " + cardId, nameof(cardId));
            new ReplaceHeroPower(card).Process(controller.Game, controller, controller.Hero, null);
        }
    }
}
