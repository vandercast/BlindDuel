using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppYgomGame.Card;
using Il2CppYgomGame.Duel;
using Il2CppYgomGame.MDMarkup;
using Il2CppYgomGame.Tutorial;
using Il2CppYgomSystem.UI;
using HarmonyLib;

namespace BlindDuel
{
    [HarmonyPatch(typeof(DuelLP), nameof(DuelLP.SetLP))]
    class PatchSetLP
    {
        [HarmonyPostfix]
        static void Postfix(DuelLP __instance, int lp, bool initialSet)
        {
            try
            {
                if (!initialSet) return;
                string who = __instance.m_IsNear ? "Your" : "Opponent's";
                Speech.SayQueued($"{who} starting life points: {lp}");
            }
            catch (Exception ex) { Log.Write($"[PatchSetLP] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelLP), nameof(DuelLP.ChangeLP), MethodType.Normal)]
    class PatchChangeLP
    {
        [HarmonyPostfix]
        static void Postfix(DuelLP __instance, int afterLP, int damage, Engine.DamageType type)
        {
            try
            {
                string who = __instance.m_IsNear ? "Your" : "Opponent's";

                string reason = type switch
                {
                    Engine.DamageType.ByBattle => "battle damage",
                    Engine.DamageType.ByEffect => "effect damage",
                    Engine.DamageType.ByCost => "cost",
                    Engine.DamageType.ByPay => "payment",
                    Engine.DamageType.ByLost => "lost",
                    Engine.DamageType.Recover => "recovery",
                    _ => ""
                };

                if (type == Engine.DamageType.Recover)
                    Speech.SayImmediate($"{who} life points: {afterLP}, gained {damage} from {reason}");
                else if (damage > 0 && reason.Length > 0)
                    Speech.SayImmediate($"{who} life points: {afterLP}, took {damage} {reason}");
                else
                    Speech.SayImmediate($"{who} life points: {afterLP}");

                if (afterLP < 1)
                {
                    NavigationState.IsInDuel = false;
                    DuelState.Clear();
                }
            }
            catch (Exception ex) { Log.Write($"[PatchChangeLP] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelClient), nameof(DuelClient.Awake))]
    class PatchDuelClientAwake
    {
        [HarmonyPostfix]
        static void Postfix(DuelClient __instance)
        {
            NavigationState.CurrentMenu = Menu.Duel;
            NavigationState.IsInDuel = true;
            DuelLogReader.Reset();
            DuelFieldNav.Reset();

            // Log player identity for debugging online duel perspective
            try
            {
                var init = __instance.engineInitializer;
                if (init != null)
                    Log.Write($"[DuelClientAwake] myPlayerNum={init.myPlayerNum}, rivalPlayerNum={init.rivalPlayerNum}");
            }
            catch { }

            // Subscribe to the game's native field focus event.
            // This fires when the duel cursor moves to any card/zone.
            try
            {
                FieldFocusHandler.Subscribe(__instance);
            }
            catch (Exception ex) { Log.Write($"[DuelClientAwake] Focus subscribe failed: {ex.Message}"); }

            try
            {
                AttackTargetHandler.Subscribe(__instance);
            }
            catch (Exception ex) { Log.Write($"[DuelClientAwake] Attack subscribe failed: {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(CardRoot), nameof(CardRoot.Initialize), MethodType.Normal)]
    class PatchCardRootInit
    {
        [HarmonyPostfix]
        static void Postfix(CardRoot __instance)
        {
            DuelState.Cards.Add(__instance);
        }
    }

    /// <summary>
    /// Handles field/hand/zone card reading via the game's native focus system.
    /// Subscribes to DuelClient.onFocusFieldHandler (delegate subscription, not Harmony patch,
    /// because InvokeFocusField is called from native code and bypasses managed wrappers).
    /// Fires when the duel cursor moves to any position (monsters, spells, hand,
    /// graveyard, extra deck, banished, etc.). Replaces SetDescriptionArea for
    /// all duel field card reading — no more spam from animations or summons.
    /// </summary>
    static class FieldFocusHandler
    {
        private static int _lastUniqueId;

        // Track last focus so dialogs can re-queue the interrupted item
        private static int _lastPlayer, _lastPosition, _lastViewIndex;
        private static bool _hasLastFocus;

        // Hold a reference to prevent GC of the managed delegate
        private static DuelClient.onFocusFieldDelegate _handler;

        public static void Subscribe(DuelClient client)
        {
            _handler = (Action<int, int, int>)OnFieldFocused;
            client.add_onFocusFieldHandler(_handler);
            Log.Write("[FieldFocus] Subscribed to onFocusFieldHandler");
        }

        /// <summary>
        /// Programmatically move focus to a field position and announce it.
        /// Uses DuelFieldBase.SelectItem to move the cursor through the game's
        /// SelectionButton system, then announces via our focus handler.
        /// </summary>
        public static void FocusPosition(int player, int position, int viewIndex = 0)
        {
            // Find the zone's SelectionButton by GameObject name and call Select(false, true)
            try
            {
                string anchorName = GetAnchorName(player, position, viewIndex);
                if (anchorName == null) { goto fallback; }

                var go = UnityEngine.GameObject.Find(anchorName);
                if (go == null) { Log.Write($"[FieldNav] GO not found: {anchorName}"); goto fallback; }

                var btn = go.GetComponent<Il2CppYgomSystem.UI.SelectionButton>();
                if (btn == null) { Log.Write($"[FieldNav] No SelectionButton on {anchorName}"); goto fallback; }

                // Deselect the currently focused item first
                try
                {
                    var allButtons = UnityEngine.Object.FindObjectsOfType<Il2CppYgomSystem.UI.SelectionButton>();
                    foreach (var b in allButtons)
                    {
                        try { if (b.isSelected) b.OnDeselected(); } catch { }
                    }
                }
                catch { }

                // Select the target (CallerCount 267)
                var item = btn.TryCast<Il2CppYgomSystem.UI.SelectionItem>();
                bool ok = item.Select(false, true);
                Log.Write($"[FieldNav] {anchorName} Select={ok}");
                if (ok) return; // game should fire onFocusFieldHandler
            }
            catch (Exception ex) { Log.Write($"[FieldNav] {ex.Message}"); }

            fallback:
            OnFieldFocused(player, position, viewIndex);
        }

        private static string GetAnchorName(int player, int position, int viewIndex)
        {
            string side = DuelState.IsMyPlayer(player) ? "Near" : "Far";

            // Hand cards use different hierarchy
            if (position == Engine.PosHand)
                return $"{side}HandCard/HandCard{viewIndex}/HandCardButton{viewIndex}";

            string zone = null;
            if (position == Engine.PosMonsterLL) zone = "Monster0";
            else if (position == Engine.PosMonsterL) zone = "Monster1";
            else if (position == Engine.PosMonsterC) zone = "Monster2";
            else if (position == Engine.PosMonsterR) zone = "Monster3";
            else if (position == Engine.PosMonsterRR) zone = "Monster4";
            else if (position == Engine.PosMagicLL) zone = "Magic0";
            else if (position == Engine.PosMagicL) zone = "Magic1";
            else if (position == Engine.PosMagicC) zone = "Magic2";
            else if (position == Engine.PosMagicR) zone = "Magic3";
            else if (position == Engine.PosMagicRR) zone = "Magic4";
            else if (position == Engine.PosExLMonster) zone = "ExMonsterL";
            else if (position == Engine.PosExRMonster) zone = "ExMonsterR";
            else if (position == Engine.PosField) zone = "FieldMagic";
            else if (position == Engine.PosGrave) zone = "Grave";
            else if (position == Engine.PosExtra) zone = "Extra";
            else if (position == Engine.PosDeck) zone = "MainDeck";
            else if (position == Engine.PosExclude) zone = "Exclude";

            if (zone == null) return null;
            return $"Anchor_{side}_{zone}";
        }

        static void OnFieldFocused(int player, int position, int viewIndex)
        {
            try
            {
                if (!NavigationState.IsInDuel || !DuelState.HasPhaseStarted) return;
                if (DuelState.IsShowingResult) return;

                // Player left the hand/selection — reset dedup so re-entering reads correctly
                PatchCardInfoSetDescription.ResetHandDedup();
                DuelHandler.ResetSelectionDedup();

                // Selection list pending — defer field focus until title is spoken.
                if (DuelState.HasPendingSelection)
                {
                    DuelState.DeferredFieldFocus = (player, position, viewIndex);
                    return;
                }

                // Suppress while field input is blocked (animations, transitions).
                // fieldInputBlockCounter is the game's native interactivity gate.
                try
                {
                    var client = DuelClient.instance;
                    if (client != null && client.fieldInputBlockCounter > 0) return;
                }
                catch { }

                // After CardCommand closes, suppress this auto-focus silently.
                // The selection prompt message will speak first, then
                // MessageJustAnnounced will queue the next manual navigation.
                if (DuelState.SuppressNextFieldFocus)
                {
                    DuelState.SuppressNextFieldFocus = false;
                    // Store for re-queue, but skip pile zones (navigation artifacts)
                    if (position != Engine.PosExtra && position != Engine.PosDeck)
                    {
                        _lastPlayer = player;
                        _lastPosition = position;
                        _lastViewIndex = viewIndex;
                        _hasLastFocus = true;
                    }
                    return;
                }

                // Consume announcement flags — queue speech after a game event
                // instead of interrupting it. Also clear screen/dialog flags
                // since duel navigation bypasses ButtonPatches where they'd
                // normally be consumed, causing stale flags to persist.
                bool queued = DuelState.MessageJustAnnounced;
                DuelState.MessageJustAnnounced = false;
                NavigationState.ScreenJustAnnounced = false;
                NavigationState.DialogJustAnnounced = false;

                // Field focus means we're not in a selection list
                DuelState.InSelectionList = false;

                // Track for re-queue if a dialog interrupts this focus
                _lastPlayer = player;
                _lastPosition = position;
                _lastViewIndex = viewIndex;
                _hasLastFocus = true;

                string zone = GetZoneName(player, position, viewIndex);

                // Track pile zone focus so DuelHandler can look up card IDs
                // when the player opens the zone's card list (X button).
                if (position == Engine.PosGrave || position == Engine.PosExtra || position == Engine.PosExclude)
                {
                    DuelState.LastBrowsePlayer = player;
                    DuelState.LastBrowsePosition = position;
                    DuelState.BrowseIndex = -1;
                    DuelState.BrowseDirection = 1;
                    DuelState.LastBrowseLogicalIdx = -1;
                }
                else
                {
                    // Left the pile zone — clear browse state so the fallback
                    // doesn't fire on unrelated buttons.
                    DuelState.LastBrowsePlayer = -1;
                    DuelState.LastBrowsePosition = -1;
                    DuelState.BrowseIndex = -1;
                    DuelState.BrowseDirection = 1;
                    DuelState.LastBrowseLogicalIdx = -1;
                }

                // Pile zones (Extra Deck, Deck) — just speak the zone name,
                // don't read individual cards from the pile.
                if (position == Engine.PosExtra || position == Engine.PosDeck)
                {
                    if (!string.IsNullOrEmpty(zone))
                        SpeakField(zone, queued);
                    _lastUniqueId = 0;
                    _hasLastFocus = false; // Don't re-queue pile zones after dialogs
                    return;
                }

                int mrk = 0;
                int uniqueId = 0;
                try
                {
                    mrk = Engine.GetCardID(player, position, viewIndex);
                    uniqueId = Engine.GetCardUniqueID(player, position, viewIndex);
                }
                catch (Exception ex)
                {
                    Log.Write($"[FocusField] Engine query failed: {ex.Message}");
                }

                // Opponent's face-down cards: game hides the card ID (mrk=0) but
                // the card still exists. Use GetCardNum to detect it on field zones.
                if (mrk <= 0 && !DuelState.IsMyPlayer(player) && (IsMonsterZone(position) || IsSpellTrapZone(position) || position == Engine.PosHand))
                {
                    try
                    {
                        int count = Engine.GetCardNum(player, position);
                        if (count > 0)
                        {
                            string msg = !string.IsNullOrEmpty(zone)
                                ? $"Face-down card, {zone}"
                                : "Face-down card";
                            Log.Write($"[FocusField] {msg}");
                            SpeakField(msg, queued);
                            DuelState.CardDetailLines = null;
                            DuelState.CardDetailIndex = 0;
                            _lastUniqueId = 0;
                            return;
                        }
                    }
                    catch (Exception ex) { Log.Write($"[FocusField] Face-down check: {ex.Message}"); }
                }

                if (mrk <= 0)
                {
                    // No card at this position — speak zone name only
                    if (!string.IsNullOrEmpty(zone))
                    {
                        Log.Write($"[FocusField] Empty: {zone}");
                        SpeakField(zone, queued);
                    }
                    DuelState.CardDetailLines = null;
                    DuelState.CardDetailIndex = 0;
                    _lastUniqueId = 0;
                    return;
                }

                // Don't reveal opponent's face-down cards (when mrk is known
                // but card is still physically face-down on the field)
                if (!DuelState.IsMyPlayer(player))
                {
                    try
                    {
                        if (!Engine.GetCardFace(player, position, viewIndex))
                        {
                            string msg = !string.IsNullOrEmpty(zone)
                                ? $"Face-down card, {zone}"
                                : "Face-down card";
                            Log.Write($"[FocusField] {msg}");
                            SpeakField(msg, queued);
                            DuelState.CardDetailLines = null;
                            DuelState.CardDetailIndex = 0;
                            _lastUniqueId = uniqueId;
                            return;
                        }
                    }
                    catch { }
                }

                // Dedup — suppress re-reading the same card instance
                if (uniqueId > 0 && uniqueId == _lastUniqueId) return;
                if (uniqueId > 0) _lastUniqueId = uniqueId;

                // Check if Link monster (no DEF, always Attack Mode)
                bool isLink = false;
                try
                {
                    var content = Content.s_instance;
                    if (content != null)
                        isLink = content.GetFrame(mrk) == Content.Frame.Link;
                }
                catch { }

                // Battle mode and live stats for monster zones
                string battlePos = null;
                int? liveAtk = null, liveDef = null;
                if (IsMonsterZone(position))
                {
                    if (isLink)
                    {
                        battlePos = "Attack Mode";
                    }
                    else
                    {
                        try
                        {
                            bool face = Engine.GetCardFace(player, position, viewIndex);
                            bool turn = Engine.GetCardTurn(player, position, viewIndex);
                            battlePos = !face ? "Set" : turn ? "Defense Mode" : "Attack Mode";
                        }
                        catch (Exception ex) { Log.Write($"[FocusField] Battle mode check: {ex.Message}"); }
                    }
                }

                // Live stats via unique ID (works for all zones including Extra Monster)
                if (uniqueId > 0)
                {
                    try
                    {
                        var bv = Engine.GetBasicValByUniqueId(uniqueId);
                        liveAtk = bv.Atk;
                        if (!isLink) liveDef = bv.Def;
                    }
                    catch (Exception ex) { Log.Write($"[FocusField] BasicVal: {ex.Message}"); }
                }

                // Read card, override live stats, build detail lines for Ctrl+Up/Down
                var card = CardReader.ReadCardFromData(mrk);
                if (liveAtk.HasValue && !string.IsNullOrEmpty(card.Atk))
                    card.Atk = liveAtk.Value >= 0 ? liveAtk.Value.ToString() : "?";
                if (liveDef.HasValue && !string.IsNullOrEmpty(card.Def))
                    card.Def = liveDef.Value >= 0 ? liveDef.Value.ToString() : "?";

                var lines = card.GetDetailLines(out string summary, battlePosition: battlePos, zone: zone);
                DuelState.CardDetailLines = lines;
                DuelState.CardDetailIndex = 0;

                // Speak only the summary (name + position + zone)
                if (!string.IsNullOrEmpty(summary))
                    SpeakField(summary, queued);
            }
            catch (Exception ex) { Log.Write($"[PatchInvokeFocusField] {ex.Message}"); }
        }

        public static void ResetDedup() => _lastUniqueId = 0;

        /// <summary>
        /// Clear last focus tracking without re-queuing speech.
        /// Used when entering a selection list — the field context is replaced.
        /// </summary>
        public static void ClearLastFocus() => _hasLastFocus = false;

        /// <summary>
        /// Speak a deferred field focus queued after a selection title.
        /// Called from HandleTitle after the title is spoken.
        /// </summary>
        public static void SpeakDeferredFocus(int player, int position, int viewIndex)
        {
            // Skip pile zones — they're navigation artifacts, not selection targets
            if (position == Engine.PosExtra || position == Engine.PosDeck) return;

            string zone = GetZoneName(player, position, viewIndex);
            if (string.IsNullOrEmpty(zone)) return;

            int mrk = 0;
            try { mrk = Engine.GetCardID(player, position, viewIndex); }
            catch { }

            // Opponent's face-down cards: game hides card ID (mrk=0) but card exists
            if (mrk <= 0 && !DuelState.IsMyPlayer(player) && (IsMonsterZone(position) || IsSpellTrapZone(position)))
            {
                try
                {
                    int count = Engine.GetCardNum(player, position);
                    if (count > 0)
                    {
                        Speech.SayQueued($"Face-down card, {zone}");
                        return;
                    }
                }
                catch { }
            }

            if (mrk <= 0)
            {
                if (!string.IsNullOrEmpty(zone))
                    Speech.SayQueued(zone);
                return;
            }

            // Don't reveal opponent's face-down cards (mrk known but physically face-down)
            if (!DuelState.IsMyPlayer(player))
            {
                try
                {
                    if (!Engine.GetCardFace(player, position, viewIndex))
                    {
                        Speech.SayQueued($"Face-down card, {zone}");
                        return;
                    }
                }
                catch { }
            }

            CardReader.SpeakCardFromData(mrk, zone, queued: true);
        }

        /// <summary>
        /// Re-queue the last focused field item. Called by dialog handlers
        /// when a dialog interrupts the auto-focused item.
        /// </summary>
        public static void RequeueLastFocus()
        {
            if (!_hasLastFocus) return;
            _hasLastFocus = false;
            SpeakDeferredFocus(_lastPlayer, _lastPosition, _lastViewIndex);
        }

        private static void SpeakField(string text, bool queued)
        {
            if (queued)
                Speech.SayQueued(text);
            else
                Speech.SayItem(text);
        }

        private static bool IsSpellTrapZone(int position)
        {
            return position == Engine.PosMagicLL || position == Engine.PosMagicL ||
                   position == Engine.PosMagicC || position == Engine.PosMagicR ||
                   position == Engine.PosMagicRR;
        }

        private static string GetZoneName(int player, int position, int viewIndex)
        {
            string side = !DuelState.IsMyPlayer(player) ? "Opponent's " : "";

            if (position == Engine.PosMonsterLL) return $"{side}Monster Zone 1";
            if (position == Engine.PosMonsterL) return $"{side}Monster Zone 2";
            if (position == Engine.PosMonsterC) return $"{side}Monster Zone 3";
            if (position == Engine.PosMonsterR) return $"{side}Monster Zone 4";
            if (position == Engine.PosMonsterRR) return $"{side}Monster Zone 5";
            if (position == Engine.PosMagicLL) return $"{side}Spell Trap Zone 1";
            if (position == Engine.PosMagicL) return $"{side}Spell Trap Zone 2";
            if (position == Engine.PosMagicC) return $"{side}Spell Trap Zone 3";
            if (position == Engine.PosMagicR) return $"{side}Spell Trap Zone 4";
            if (position == Engine.PosMagicRR) return $"{side}Spell Trap Zone 5";
            if (position == Engine.PosField) return $"{side}Field Spell Zone";
            if (position == Engine.PosPendulumLeft) return $"{side}Left Pendulum Zone";
            if (position == Engine.PosPendulumRight) return $"{side}Right Pendulum Zone";
            if (position == Engine.PosExLMonster) return $"{side}Extra Monster Zone Left";
            if (position == Engine.PosExRMonster) return $"{side}Extra Monster Zone Right";
            if (position == Engine.PosHand)
            {
                try
                {
                    int count = Engine.GetCardNum(player, Engine.PosHand);
                    if (count > 0 && viewIndex >= 0)
                        return $"{side}Hand, {viewIndex + 1} of {count}";
                }
                catch { }
                return $"{side}Hand";
            }
            if (position == Engine.PosExtra) return $"{side}Extra Deck";
            if (position == Engine.PosDeck) return $"{side}Deck";
            if (position == Engine.PosGrave)
            {
                try
                {
                    int count = Engine.GetCardNum(player, Engine.PosGrave);
                    if (count > 0)
                        return $"{side}Graveyard, {count} cards";
                }
                catch { }
                return $"{side}Graveyard";
            }
            if (position == Engine.PosExclude)
            {
                try
                {
                    int count = Engine.GetCardNum(player, Engine.PosExclude);
                    if (count > 0)
                        return $"{side}Banished, {count} cards";
                }
                catch { }
                return $"{side}Banished";
            }

            Log.Write($"[FocusField] Unknown position: player={player}, pos={position}, mapped to nothing");
            return null;
        }

        private static bool IsMonsterZone(int position)
        {
            return position == Engine.PosMonsterLL || position == Engine.PosMonsterL ||
                   position == Engine.PosMonsterC || position == Engine.PosMonsterR ||
                   position == Engine.PosMonsterRR || position == Engine.PosExLMonster ||
                   position == Engine.PosExRMonster;
        }
    }

    /// <summary>
    /// Announces opponent attack declarations via the game's native delegate.
    /// Fires when any monster declares an attack target.
    /// </summary>
    static class AttackTargetHandler
    {
        private static DuelClient.onDecideAttackTargetDelegate _handler;

        public static void Subscribe(DuelClient client)
        {
            _handler = (Action<int, int, int, int, int, int>)OnAttackDeclared;
            client.add_onDecideAttackTargetHandler(_handler);
            Log.Write("[AttackTarget] Subscribed to onDecideAttackTargetHandler");
        }

        static void OnAttackDeclared(int attackerPlayer, int attackerPosition, int attackerIndex,
            int targetPlayer, int targetPosition, int targetIndex)
        {
            try
            {
                // Only announce opponent's attacks
                if (DuelState.IsMyPlayer(attackerPlayer)) return;

                var content = Content.s_instance;
                if (content == null) return;

                // Get attacker name
                int attackerMrk = 0;
                try { attackerMrk = Engine.GetCardID(attackerPlayer, attackerPosition, attackerIndex); }
                catch { }
                string attackerName = attackerMrk > 0 ? content.GetName(attackerMrk) : "Unknown monster";

                // Get target — direct attack if no card at target position
                int targetMrk = 0;
                try { targetMrk = Engine.GetCardID(targetPlayer, targetPosition, targetIndex); }
                catch { }

                string announcement;
                if (targetMrk > 0)
                {
                    string targetName = content.GetName(targetMrk);
                    announcement = $"Opponent attacks {targetName} with {attackerName}";
                }
                else
                {
                    announcement = $"Opponent attacks directly with {attackerName}";
                }

                Log.Write($"[AttackTarget] {announcement}");
                Speech.SayQueued(announcement);
            }
            catch (Exception ex) { Log.Write($"[AttackTarget] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Card info panel reading — fires when any card gets focused in the duel.
    /// During duels:
    ///   - Hand cards: read directly from Engine database using CardInfoData.cardid
    ///     and speak immediately. No delayed UI read needed.
    ///   - Selection lists: use delayed ReadCardDelayed flow.
    ///   - Field cards: ignored here (handled by onFocusFieldHandler).
    /// Outside duels: fires normally for deck editor, card browser, etc.
    /// </summary>
    [HarmonyPatch(typeof(CardInfo), nameof(CardInfo.SetDescriptionArea))]
    class PatchCardInfoSetDescription
    {
        private const float ReadDelay = 0.15f;
        private static int _lastUniqueId;
        private static int _lastHandUniqueId;
        private static int _pendingMrk;
        private static int _pendingUniqueId;

        [HarmonyPostfix]
        static void Postfix(CardInfo __instance)
        {
            try
            {
                if (!__instance.gameObject.activeInHierarchy) return;

                if (NavigationState.IsInDuel)
                {
                    if (!DuelState.HasPhaseStarted) return;
                    if (DuelState.IsShowingResult) return;

                    try
                    {
                        var data = __instance.m_CardInfoData;

                        // Hand cards: read from Engine and speak immediately.
                        // Uses CardInfoData (position, cardid, index) as the trigger —
                        // the actual card data comes from the Engine database.
                        if (data.position == Engine.PosHand && DuelState.IsMyPlayer(data.player))
                        {
                            int mrk = data.cardid;
                            if (mrk <= 0) return;

                            int uid = data.uniqueid;
                            if (uid > 0 && uid == _lastHandUniqueId) return;
                            if (uid > 0) _lastHandUniqueId = uid;

                            int handCount = Engine.GetCardNum(data.player, Engine.PosHand);
                            int idx = data.index;
                            string zone = handCount > 0
                                ? $"Hand, {idx + 1} of {handCount}"
                                : "Hand";

                            Log.Write($"[HandCard] mrk={mrk}, uid={uid}, {zone}");
                            // Queue after game event messages (summon/activation)
                            // instead of interrupting them
                            bool queued = DuelState.MessageJustAnnounced;
                            DuelState.MessageJustAnnounced = false;
                            // Clear screen/dialog flags — hand navigation confirms
                            // the screen has been acknowledged (same as field focus)
                            NavigationState.ScreenJustAnnounced = false;
                            NavigationState.DialogJustAnnounced = false;

                            // Read card, build detail lines for Ctrl+Up/Down
                            BlindDuelCore.Preview.Clear();
                            var card = CardReader.ReadCardFromData(mrk);
                            var lines = card.GetDetailLines(out string summary, zone: zone);
                            DuelState.CardDetailLines = lines;
                            DuelState.CardDetailIndex = 0;

                            if (!string.IsNullOrEmpty(summary))
                            {
                                if (queued) Speech.SayQueued(summary);
                                else Speech.SayItem(summary);
                            }
                            return;
                        }

                        // Selection list cards are handled by DuelHandler.OnButtonFocused
                        // which reads ListCard.m_CardData directly. Nothing to do here.
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"[SetDescription] CardInfoData read failed: {ex.Message}");
                        return;
                    }
                }

                // Outside duels: read from UI panels (deck editor, card browser, etc.)
                try
                {
                    var data = __instance.m_CardInfoData;
                    _pendingMrk = data.cardid;
                    _pendingUniqueId = data.uniqueid;
                }
                catch (Exception ex)
                {
                    Log.Write($"[SetDescription] CardInfoData read failed: {ex.Message}");
                    _pendingMrk = 0;
                    _pendingUniqueId = 0;
                }

                BlindDuelCore.Instance.CancelInvoke(nameof(BlindDuelCore.ReadCardDelayed));
                BlindDuelCore.Instance.Invoke(nameof(BlindDuelCore.ReadCardDelayed), ReadDelay);
            }
            catch (Exception ex)
            {
                Log.Write($"[SetDescription] {ex.Message}");
            }
        }

        public static int PendingMrk => _pendingMrk;
        public static int PendingUniqueId => _pendingUniqueId;

        public static bool CheckAndUpdateDedup(int uniqueId)
        {
            if (uniqueId <= 0) return false;
            if (uniqueId == _lastUniqueId) return true;
            _lastUniqueId = uniqueId;
            return false;
        }

        public static void ResetDedup() => _lastUniqueId = 0;

        public static void ResetHandDedup() => _lastHandUniqueId = 0;
    }

    // --- Tutorial & Instant Message patches ---

    [HarmonyPatch(typeof(TutorialNavigator), nameof(TutorialNavigator.PlayCenterMsg),
        new Type[] { typeof(Il2CppSystem.Collections.Generic.IList<string>), typeof(UnityEngine.Events.UnityAction), typeof(float) })]
    class PatchTutorialCenterMsg
    {
        private static string _lastMessage = "";

        [HarmonyPostfix]
        static void Postfix(Il2CppSystem.Collections.Generic.IList<string> messages)
        {
            try
            {
                if (messages == null) return;

                var parts = new System.Collections.Generic.List<string>();
                // Il2Cpp IList doesn't expose Count directly — iterate by index with bounds check
                for (int i = 0; ; i++)
                {
                    string msg;
                    try { msg = messages[i]; }
                    catch { break; }
                    if (!string.IsNullOrWhiteSpace(msg))
                        parts.Add(TextUtil.StripTags(msg));
                }

                string combined = string.Join(". ", parts);
                if (string.IsNullOrWhiteSpace(combined) || combined == _lastMessage) return;

                _lastMessage = combined;
                Log.Write($"[TutorialCenter] {combined}");
                if (NavigationState.IsInDuel)
                {
                    DuelState.MessageJustAnnounced = true;
                    Speech.SayImmediate(combined);
                }
                else
                {
                    Speech.SayQueued(combined);
                }
            }
            catch (Exception ex) { Log.Write($"[PatchTutorialCenterMsg] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(TutorialNavigator), nameof(TutorialNavigator.PlayTopMsg),
        new Type[] { typeof(string), typeof(float) })]
    class PatchTutorialTopMsg
    {
        private static string _lastMessage = "";

        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned == _lastMessage) return;

                _lastMessage = cleaned;
                Log.Write($"[TutorialTop] {cleaned}");
                if (NavigationState.IsInDuel)
                {
                    DuelState.MessageJustAnnounced = true;
                    Speech.SayImmediate(cleaned);
                }
                else
                {
                    Speech.SayQueued(cleaned);
                }
            }
            catch (Exception ex) { Log.Write($"[PatchTutorialTopMsg] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(InstantMessage), nameof(InstantMessage.Open))]
    class PatchInstantMessageOpen
    {
        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;
                if (cleaned == InstantMessageDedup.LastMessage) return;
                InstantMessageDedup.LastMessage = cleaned;

                Log.Write($"[InstantMessage] {cleaned}");
                if (NavigationState.IsInDuel)
                {
                    // If a summon/activation just announced, queue after it
                    // instead of interrupting (effect text arrives ~8ms later)
                    if (DuelState.MessageJustAnnounced)
                        Speech.SayQueued(cleaned);
                    else
                    {
                        DuelState.MessageJustAnnounced = true;
                        Speech.SayImmediate(cleaned);
                    }
                }
                else
                {
                    Speech.SayQueued(cleaned);
                }
            }
            catch (Exception ex) { Log.Write($"[PatchInstantMessageOpen] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(InstantMessage), nameof(InstantMessage.ReqOpen))]
    class PatchInstantMessageReqOpen
    {
        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;

                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;
                if (cleaned == InstantMessageDedup.LastMessage) return;
                InstantMessageDedup.LastMessage = cleaned;

                Log.Write($"[InstantMessageReq] {cleaned}");
                if (NavigationState.IsInDuel)
                {
                    if (DuelState.MessageJustAnnounced)
                        Speech.SayQueued(cleaned);
                    else
                    {
                        DuelState.MessageJustAnnounced = true;
                        Speech.SayImmediate(cleaned);
                    }
                }
                else
                {
                    Speech.SayQueued(cleaned);
                }
            }
            catch (Exception ex) { Log.Write($"[PatchInstantMessageReqOpen] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Shared dedup between InstantMessage.Open and InstantMessage.ReqOpen
    /// to prevent the same message speaking twice (ReqOpen queues, Open displays).
    /// </summary>
    static class InstantMessageDedup
    {
        public static string LastMessage = "";
    }

    /// <summary>
    /// Shared helper for duel log patches. Reads ShowCardNameData entries added by
    /// AddRun*Log methods to announce opponent summons and card activations.
    /// </summary>
    static class DuelLogHelper
    {
        /// <summary>
        /// Captured in each summon/activation prefix so the postfix can find
        /// the newly added action (not the last action overall, which may be stale).
        /// </summary>
        private static int _prevActionCount;

        public static void CapturePrevActionCount(DuelLogController instance)
        {
            try { _prevActionCount = instance.m_DataList_ShowAction?.Count ?? 0; }
            catch { _prevActionCount = -1; }
        }

        public static void AnnounceSummon(DuelLogController instance, int prevCardNameCount, string defaultLabel)
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                if (prevCardNameCount < 0) return;

                // Find the newly added action (not the last one overall —
                // the list accumulates across the entire duel).
                string summonName = defaultLabel;
                int? actionOwner = null;
                try
                {
                    var actionList = instance.m_DataList_ShowAction;
                    if (actionList != null && _prevActionCount >= 0
                        && actionList.Count > _prevActionCount)
                    {
                        var newAction = actionList[_prevActionCount];
                        var datac = newAction.datac;
                        var actType = datac.acttype;
                        string specific = GetSummonTypeName(actType);
                        if (specific != null)
                            summonName = specific;
                        // Use datal.owner (actual player number) instead of datac.team
                        // (which reflects turn player, not card owner)
                        try { actionOwner = newAction.datal.owner; }
                        catch { }
                    }
                }
                catch { } // Property call on struct may fail — keep defaultLabel

                // Find new ShowCardNameData entries added by this method
                var cardList = instance.m_DataList_ShowCardName;
                if (cardList == null || cardList.Count <= prevCardNameCount) return;

                for (int i = prevCardNameCount; i < cardList.Count; i++)
                {
                    var card = cardList[i];
                    if (card.isCost) continue;

                    int cardId = card.cardid;
                    // Use datal.owner (player number) for accurate ownership
                    bool isOpponent = actionOwner.HasValue
                        ? !DuelState.IsMyPlayer(actionOwner.Value)
                        : DuelState.IsOpponentTeam(card.team);
                    string cardName = ResolveCardName(cardId);

                    Log.Write($"[DuelLog] {summonName}: {cardName} (owner={actionOwner}, cardTeam={card.team}, opponent={isOpponent}, cardid={cardId})");

                    DuelState.MessageJustAnnounced = true;
                    string prefix = isOpponent ? "Opponent " : "";
                    Speech.SayImmediate($"{prefix}{summonName}: {cardName}");
                    return; // Only announce the first non-cost card
                }
            }
            catch (Exception ex) { Log.Write($"[DuelLogHelper] {ex.Message}"); }
        }

        public static void AnnounceActivation(DuelLogController instance, int prevCardNameCount)
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                if (prevCardNameCount < 0) return;

                // Get action owner from the newly added action
                int? actionOwner = null;
                try
                {
                    var actionList = instance.m_DataList_ShowAction;
                    if (actionList != null && _prevActionCount >= 0
                        && actionList.Count > _prevActionCount)
                    {
                        try { actionOwner = actionList[_prevActionCount].datal.owner; }
                        catch { }
                    }
                }
                catch { }

                var cardList = instance.m_DataList_ShowCardName;
                if (cardList == null || cardList.Count <= prevCardNameCount) return;

                for (int i = prevCardNameCount; i < cardList.Count; i++)
                {
                    var card = cardList[i];
                    if (card.isCost) continue;

                    int cardId = card.cardid;
                    bool isOpponent = actionOwner.HasValue
                        ? !DuelState.IsMyPlayer(actionOwner.Value)
                        : DuelState.IsOpponentTeam(card.team);
                    string cardName = ResolveCardName(cardId);

                    Log.Write($"[DuelLog] Activates: {cardName} (owner={actionOwner}, cardTeam={card.team}, opponent={isOpponent}, cardid={cardId})");

                    DuelState.MessageJustAnnounced = true;
                    string prefix = isOpponent ? "Opponent activates" : "Activate";
                    Speech.SayImmediate($"{prefix}: {cardName}");
                    return;
                }
            }
            catch (Exception ex) { Log.Write($"[DuelLogHelper] Activation: {ex.Message}"); }
        }

        public static string ResolveCardName(int cardId)
        {
            try
            {
                var content = Content.s_instance;
                if (content != null && cardId > 0)
                    return content.GetName(cardId);
            }
            catch { }
            return "unknown";
        }

        /// <summary>
        /// Returns a specific summon type name, or null if the type is unrecognized
        /// (so the caller keeps its default label).
        /// </summary>
        static string GetSummonTypeName(LOGACTIONTYPE type) => type switch
        {
            LOGACTIONTYPE.ACTION_SUMMON => "Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON => "Special Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_RITUAL => "Ritual Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_FUSION => "Fusion Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_SYNC => "Synchro Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_XYZ => "Xyz Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_PENDULUM => "Pendulum Summon",
            LOGACTIONTYPE.ACTION_SPSUMMON_LINK => "Link Summon",
            LOGACTIONTYPE.ACTION_REVERSESUMMON => "Flip Summon",
            _ => null
        };
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.AddRunSummonLog))]
    class PatchAddRunSummonLog
    {
        [HarmonyPrefix]
        static void Prefix(DuelLogController __instance, ref int __state)
        {
            try { __state = __instance.m_DataList_ShowCardName?.Count ?? 0; }
            catch { __state = -1; }
            DuelLogHelper.CapturePrevActionCount(__instance);
        }

        [HarmonyPostfix]
        static void Postfix(DuelLogController __instance, int __state)
        {
            DuelLogHelper.AnnounceSummon(__instance, __state, "Summon");
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.AddRunSpSummonLog))]
    class PatchAddRunSpSummonLog
    {
        [HarmonyPrefix]
        static void Prefix(DuelLogController __instance, ref int __state)
        {
            try { __state = __instance.m_DataList_ShowCardName?.Count ?? 0; }
            catch { __state = -1; }
            DuelLogHelper.CapturePrevActionCount(__instance);
        }

        [HarmonyPostfix]
        static void Postfix(DuelLogController __instance, int __state)
        {
            DuelLogHelper.AnnounceSummon(__instance, __state, "Special Summon");
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.AddRunFusionLog))]
    class PatchAddRunFusionLog
    {
        [HarmonyPrefix]
        static void Prefix(DuelLogController __instance, ref int __state)
        {
            try { __state = __instance.m_DataList_ShowCardName?.Count ?? 0; }
            catch { __state = -1; }
            DuelLogHelper.CapturePrevActionCount(__instance);
        }

        [HarmonyPostfix]
        static void Postfix(DuelLogController __instance, int __state)
        {
            DuelLogHelper.AnnounceSummon(__instance, __state, "Fusion Summon");
        }
    }

    /// <summary>
    /// Announces opponent card activations (effect activation, hand traps, etc.).
    /// When the opponent activates a card, the player hears the card name
    /// before/alongside the effect text from InstantMessage.
    /// </summary>
    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.AddCardHappenLog))]
    class PatchAddCardHappenLog
    {
        [HarmonyPrefix]
        static void Prefix(DuelLogController __instance, ref int __state)
        {
            try { __state = __instance.m_DataList_ShowCardName?.Count ?? 0; }
            catch { __state = -1; }
            DuelLogHelper.CapturePrevActionCount(__instance);
        }

        [HarmonyPostfix]
        static void Postfix(DuelLogController __instance, int __state)
        {
            DuelLogHelper.AnnounceActivation(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(DuelInfoDialogBase), nameof(DuelInfoDialogBase.Open))]
    class PatchDuelInfoDialogOpen
    {
        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Log.Write($"[DuelInfoDialog] {cleaned}");
                if (NavigationState.IsInDuel)
                {
                    DuelState.MessageJustAnnounced = true;
                    Speech.SayImmediate(cleaned);

                    // Re-queue the field item that was just interrupted.
                    // The field focus fires ~4ms before this dialog in the same frame,
                    // so the item spoke first and got cut off by SayImmediate above.
                    FieldFocusHandler.RequeueLastFocus();
                }
                else
                {
                    Speech.SayQueued(cleaned);
                }
            }
            catch (Exception ex) { Log.Write($"[PatchDuelInfoDialog] {ex.Message}"); }
        }
    }

    [HarmonyPatch]
    class PatchDuelConfirmDialogOpen
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = new List<MethodBase>();
            foreach (var m in typeof(DuelConfirmDialog).GetMethods())
            {
                if (m.Name == nameof(DuelConfirmDialog.Open))
                    methods.Add(m);
            }
            return methods;
        }

        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(message)) return;
                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Log.Write($"[DuelConfirmDialog] {cleaned}");
                Speech.SayImmediate(cleaned);
            }
            catch (Exception ex) { Log.Write($"[PatchDuelConfirmDialogOpen] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelSelectDialog), nameof(DuelSelectDialog.Open))]
    class PatchDuelSelectDialogOpen
    {
        [HarmonyPostfix]
        static void Postfix(string message)
        {
            try
            {
                PatchDuelSelectDialogUpdateMessage.ResetDedup();

                if (string.IsNullOrWhiteSpace(message)) return;
                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Log.Write($"[DuelSelectDialog] {cleaned}");
                Speech.SayImmediate(cleaned);
            }
            catch (Exception ex) { Log.Write($"[PatchDuelSelectDialogOpen] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Announces the card/effect when navigating between tabs in a DuelSelectDialog
    /// (e.g., the "Activate a card or effect?" popup). The card tabs are SelectionItems,
    /// not SelectionButtons, so our OnSelected patch doesn't fire for them.
    /// UpdateMessage fires when the selected tab changes (both focus and confirm).
    /// </summary>
    [HarmonyPatch(typeof(DuelSelectDialog), nameof(DuelSelectDialog.UpdateMessage))]
    class PatchDuelSelectDialogUpdateMessage
    {
        private static int _lastIndex = -1;

        [HarmonyPostfix]
        static void Postfix(DuelSelectDialog __instance, int effectIndex)
        {
            try
            {
                if (effectIndex == _lastIndex) return;
                _lastIndex = effectIndex;

                var infoList = __instance.infoList;
                if (infoList == null || effectIndex < 0 || effectIndex >= infoList.Count) return;

                string message = infoList[effectIndex].message;
                if (string.IsNullOrWhiteSpace(message)) return;

                string cleaned = TextUtil.StripTags(message);
                if (string.IsNullOrWhiteSpace(cleaned)) return;

                Log.Write($"[DuelSelectTab] Tab {effectIndex}: {cleaned}");
                Speech.SayItem(cleaned);
            }
            catch (Exception ex) { Log.Write($"[PatchDuelSelectDialogUpdateMessage] {ex.Message}"); }
        }

        public static void ResetDedup() => _lastIndex = -1;
    }

    /// <summary>
    /// Speaks the selection prompt for card selection list mode.
    /// SetTitle is called internally during SetListImpl for list-based selections.
    /// Shared dedup/speech logic lives in HandleTitle().
    /// </summary>
    [HarmonyPatch(typeof(CardSelectionList), nameof(CardSelectionList.SetTitle))]
    class PatchCardSelectionListSetTitle
    {
        private static bool _nextReadQueued;
        private static string _lastTitle = "";

        /// <summary>
        /// Shared handler for selection prompt titles from both SetTitle and SetList patches.
        /// Deduplicates and speaks the title.
        /// </summary>
        public static void HandleTitle(string cleaned)
        {
            if (cleaned == _lastTitle) return;
            _lastTitle = cleaned;

            Log.Write($"[CardSelectionList] {cleaned}");
            DuelState.MessageJustAnnounced = true;
            Speech.SayImmediate(cleaned);
            _nextReadQueued = true;

            // Queue the deferred or interrupted item after the title.
            // This mirrors QueueFocusedItem for normal menus.
            DuelState.HasPendingSelection = false;
            QueueDeferredItem();

            // Clear last field focus — the selection list replaces the field
            // context, so re-queuing the last card would sound like a menu item.
            FieldFocusHandler.ClearLastFocus();

            // Re-queue button text that was queued but then interrupted by SayImmediate
            if (!string.IsNullOrEmpty(DuelState.LastQueuedButtonText))
            {
                Speech.SayQueued(DuelState.LastQueuedButtonText);
                DuelState.LastQueuedButtonText = null;
            }
        }

        /// <summary>
        /// Speak the item that was deferred during selection setup, queued after the title.
        /// </summary>
        private static void QueueDeferredItem()
        {
            // Deferred button (card list selection)
            var btn = DuelState.DeferredSelectionButton;
            if (btn != null)
            {
                DuelState.DeferredSelectionButton = null;
                try
                {
                    var handler = HandlerRegistry.Current;
                    if (handler != null)
                    {
                        string text = handler.OnButtonFocused(btn);
                        // Handler spoke the card directly (returned "")
                        // or returned null — try default text
                        if (text == null)
                        {
                            text = TextExtractor.ExtractFirst(btn.gameObject);
                        }
                        if (!string.IsNullOrWhiteSpace(text) && text != "")
                            Speech.SayQueued(text);
                    }
                }
                catch (Exception ex) { Log.Write($"[QueueDeferred] Button: {ex.Message}"); }
                return;
            }

            // Deferred field focus (field zone selection)
            var focus = DuelState.DeferredFieldFocus;
            if (focus.HasValue)
            {
                DuelState.DeferredFieldFocus = null;
                try
                {
                    var (player, position, viewIndex) = focus.Value;
                    FieldFocusHandler.SpeakDeferredFocus(player, position, viewIndex);
                }
                catch (Exception ex) { Log.Write($"[QueueDeferred] Field: {ex.Message}"); }
            }
        }

        [HarmonyPostfix]
        static void Postfix(string title)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title)) return;
                string cleaned = TextUtil.StripTags(title);
                if (string.IsNullOrWhiteSpace(cleaned)) return;
                HandleTitle(cleaned);
            }
            catch (Exception ex) { Log.Write($"[PatchCardSelectionListSetTitle] {ex.Message}"); }
        }

        public static bool ConsumeQueuedFlag()
        {
            bool val = _nextReadQueued;
            _nextReadQueued = false;
            return val;
        }

        public static void ResetDedup() => _lastTitle = "";
    }

    /// <summary>
    /// Catches selection prompts that bypass SetTitle — e.g. field selection mode
    /// ("Select the card to send to the Graveyard"). SetList receives the title
    /// as a parameter and may not call SetTitle for all selection modes.
    /// </summary>
    [HarmonyPatch(typeof(CardSelectionList), nameof(CardSelectionList.SetList))]
    class PatchCardSelectionListSetList
    {
        /// <summary>
        /// PREFIX: Set pending flag BEFORE the game creates items and auto-focuses.
        /// Mirrors HasPendingScreen for normal menus.
        /// </summary>
        [HarmonyPrefix]
        static void Prefix()
        {
            if (NavigationState.IsInDuel)
            {
                DuelState.HasPendingSelection = true;
                DuelState.DeferredSelectionButton = null;
                DuelState.DeferredFieldFocus = null;
                DuelHandler.ResetSelectionDedup();
                PatchCardSelectionListSetTitle.ResetDedup();
            }
        }

        [HarmonyPostfix]
        static void Postfix(string title)
        {
            try
            {
                // Always clear pending flag — even if title is empty.
                // Otherwise HasPendingSelection stays true and all buttons are deferred forever.
                if (NavigationState.IsInDuel)
                    DuelState.HasPendingSelection = false;

                if (string.IsNullOrWhiteSpace(title)) return;
                string cleaned = TextUtil.StripTags(title);
                if (string.IsNullOrWhiteSpace(cleaned)) return;
                PatchCardSelectionListSetTitle.HandleTitle(cleaned);
            }
            catch (Exception ex) { Log.Write($"[PatchCardSelectionListSetList] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Catches field selection prompts that go through EffectTaskRunDialog
    /// (e.g. "Select the card to send to the Graveyard"). RunDialog sets up
    /// the selection UI and populates the text. Reading __instance.text here
    /// catches prompts that bypass CardSelectionList.SetTitle/SetList.
    /// </summary>
    [HarmonyPatch(typeof(EffectTaskRunDialog), nameof(EffectTaskRunDialog.RunDialog))]
    class PatchEffectTaskRunDialog
    {
        [HarmonyPostfix]
        static void Postfix(EffectTaskRunDialog __instance)
        {
            try
            {
                if (!NavigationState.IsInDuel) return;

                string text = __instance.text;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    string cleaned = TextUtil.StripTags(text);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        Log.Write($"[EffectTaskRunDialog] {cleaned}");
                        PatchCardSelectionListSetTitle.HandleTitle(cleaned);
                        return;
                    }
                }

                // Fallback: read activateCardSelectionText static field
                string actText = EffectTaskRunDialog.activateCardSelectionText;
                if (!string.IsNullOrWhiteSpace(actText))
                {
                    string cleaned = TextUtil.StripTags(actText);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        Log.Write($"[EffectTaskRunDialog.activate] {cleaned}");
                        PatchCardSelectionListSetTitle.HandleTitle(cleaned);
                    }
                }
            }
            catch (Exception ex) { Log.Write($"[PatchEffectTaskRunDialog] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelEndOperation), nameof(DuelEndOperation.Setup))]
    class PatchDuelEndOperationSetup
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                // Fires early when duel end sequence begins (before animation).
                // Suppress button speech until DuelEndMessage.Setup populates the result text.
                DuelState.IsShowingResult = true;
                Log.Write("[DuelEndOp] Duel end sequence started, suppressing buttons");
            }
            catch (Exception ex) { Log.Write($"[PatchDuelEndOperationSetup] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelEndMessage), nameof(DuelEndMessage.Setup))]
    class PatchDuelEndMessageSetup
    {
        [HarmonyPostfix]
        static void Postfix(string message, bool winMyself, bool winRival)
        {
            try
            {
                string result = winMyself ? "Victory" : winRival ? "Defeat" : "Draw";

                string cleaned = !string.IsNullOrWhiteSpace(message)
                    ? TextUtil.StripTags(message)
                    : "";

                string announcement = !string.IsNullOrEmpty(cleaned)
                    ? $"{result}. {cleaned}"
                    : result;

                Log.Write($"[DuelEnd] {announcement}");
                Speech.SayImmediate(announcement);

                // Result has been spoken — allow buttons and mark duel as over
                NavigationState.IsInDuel = false;
                DuelState.Clear();
            }
            catch (Exception ex) { Log.Write($"[PatchDuelEndMessageSetup] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Read match tips pages (MDMarkup content shown at the start of solo practice duels).
    /// Only speaks during duels to avoid interfering with menu MDMarkup reading.
    /// </summary>
    [HarmonyPatch(typeof(MDMarkupPageWidgetBase), nameof(MDMarkupPageWidgetBase.BindContentData))]
    class PatchMDMarkupBindContent
    {
        [HarmonyPostfix]
        static void Postfix(MDMarkupPageWidgetBase __instance)
        {
            try
            {
                if (!NavigationState.IsInDuel) return;

                string title = __instance.m_CaptionText?.text;
                string body = __instance.m_Text?.text;

                title = TextUtil.StripTags(title);
                body = TextUtil.StripTags(body);

                if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) return;

                string announcement = !string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(body)
                    ? $"{title}. {body}"
                    : title ?? body;

                Log.Write($"[MatchTips] {announcement}");
                Speech.SayQueued(announcement);
            }
            catch (Exception ex) { Log.Write($"[PatchMDMarkupBindContent] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(CardCommand), nameof(CardCommand.Open), new Type[0])]
    class PatchCardCommandOpen
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                Log.Write("[CardCommand] Action menu opened — silencing card speech");
                Speech.Silence();
            }
            catch (Exception ex) { Log.Write($"[PatchCardCommandOpen] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(CardCommand), nameof(CardCommand.Close))]
    class PatchCardCommandClose
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                // Suppress the next field focus — a selection prompt message
                // will follow shortly and should speak first.
                DuelState.SuppressNextFieldFocus = true;
            }
            catch (Exception ex) { Log.Write($"[PatchCardCommandClose] {ex.Message}"); }
        }
    }

    /// <summary>
    /// When battle position selection opens (Attack/Defense choice during summon),
    /// queue the first auto-focused button so it doesn't interrupt any preceding speech.
    /// SetDefaultPosition is called by the game when initializing position buttons.
    /// </summary>
    [HarmonyPatch(typeof(CardCommandEx), nameof(CardCommandEx.SetDefaultPosition))]
    class PatchPositionSelectOpen
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                Log.Write("[PositionSelect] Battle position selection opened");
                NavigationState.DialogJustAnnounced = true;
                DuelState.HasPendingSelection = true;
                DuelState.DeferredSelectionButton = null;
                DuelState.DeferredFieldFocus = null;
            }
            catch (Exception ex) { Log.Write($"[PatchPositionSelectOpen] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(EffectTaskPhaseChange), nameof(EffectTaskPhaseChange.PlayPhaseChangeEffect))]
    class PatchPhaseChange
    {
        private static Engine.Phase _lastPhase = Engine.Phase.Null;

        [HarmonyPostfix]
        static void Postfix(EffectTaskPhaseChange __instance)
        {
            try
            {
                DuelState.HasPhaseStarted = true;

                var phase = __instance.phase;
                if (phase == _lastPhase) return;
                _lastPhase = phase;

                string phaseName = phase switch
                {
                    Engine.Phase.Draw => "Draw Phase",
                    Engine.Phase.Standby => "Standby Phase",
                    Engine.Phase.Main1 => "Main Phase 1",
                    Engine.Phase.Battle => "Battle Phase",
                    Engine.Phase.Main2 => "Main Phase 2",
                    Engine.Phase.End => "End Phase",
                    _ => ""
                };

                if (string.IsNullOrEmpty(phaseName)) return;

                Log.Write($"[PhaseChange] {phaseName}");
                Speech.SayQueued(phaseName);
            }
            catch (Exception ex) { Log.Write($"[PatchPhaseChange] {ex.Message}"); }
        }
    }

    // ── Duel Log Viewer ──────────────────────────────────────────────

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.Open))]
    class PatchDuelLogOpen
    {
        [HarmonyPostfix]
        static void Postfix(DuelLogController __instance)
        {
            try
            {
                if (!NavigationState.IsInDuel) return;
                DuelState.IsDuelLogOpen = true;

                // Capture controller + scroll view for index polling and team lookup
                DuelState.LogController = __instance;
                var sv = __instance.m_ScrollView;
                DuelState.LogScrollView = sv;

                Log.Write("[DuelLog] Opened");
                Speech.SayImmediate("Duel Log");
                DuelLogReader.InitScroll(sv);
            }
            catch (Exception ex) { Log.Write($"[PatchDuelLogOpen] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.Close))]
    class PatchDuelLogClose
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                if (!DuelState.IsDuelLogOpen) return;
                DuelState.IsDuelLogOpen = false;
                Log.Write("[DuelLog] Closed");
            }
            catch (Exception ex) { Log.Write($"[PatchDuelLogClose] {ex.Message}"); }
        }
    }

    [HarmonyPatch(typeof(CardInfo), nameof(CardInfo.SetCardByCardId))]
    class PatchDuelLogSetCard
    {
        [HarmonyPostfix]
        static void Postfix(int cardid)
        {
            try
            {
                if (!DuelState.IsDuelLogOpen) return;
                if (DuelState.InSelectionList) return;
                if (cardid <= 0) return;

                // Look up team from the log controller's card name data
                bool isOpponent = false;
                try
                {
                    var controller = DuelState.LogController;
                    if (controller != null)
                    {
                        var cardList = controller.m_DataList_ShowCardName;
                        if (cardList != null)
                        {
                            for (int i = cardList.Count - 1; i >= 0; i--)
                            {
                                if (cardList[i].cardid == cardid)
                                {
                                    isOpponent = DuelState.IsOpponentTeam(cardList[i].team);
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }

                // Speak card name only (card info panel shows full details)
                string cardName = DuelLogHelper.ResolveCardName(cardid);
                string speech = isOpponent ? $"Opponent: {cardName}" : cardName;
                Log.Write($"[DuelLog-Card] {speech}");
                Speech.SayItem(speech);
            }
            catch (Exception ex) { Log.Write($"[PatchDuelLogSetCard] {ex.Message}"); }
        }
    }

    /// <summary>
    /// Patches OnUpdateShow* methods on DuelLogController.
    /// These fire when the game renders/updates items in the log scroll view —
    /// including during user scrolling. Each receives the EOM GameObject
    /// containing the rendered text for that entry.
    /// </summary>
    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowAction))]
    class PatchDuelLogShowAction
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "Action", dataindex);
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowCardName))]
    class PatchDuelLogShowCardName
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "CardName", dataindex);
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowPhase))]
    class PatchDuelLogShowPhase
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "Phase", dataindex);
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowTurn))]
    class PatchDuelLogShowTurn
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "Turn", dataindex);
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowText))]
    class PatchDuelLogShowText
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "Text", dataindex);
        }
    }

    [HarmonyPatch(typeof(DuelLogController), nameof(DuelLogController.OnUpdateShowChain))]
    class PatchDuelLogShowChain
    {
        [HarmonyPostfix]
        static void Postfix(UnityEngine.GameObject eom, int dataindex)
        {
            DuelLogReader.CaptureItem(eom, "Chain", dataindex);
        }
    }

    /// <summary>
    /// Captures and reads duel log entries. OnUpdateShow* callbacks store
    /// rendered text as entries are created during gameplay. When the log
    /// opens, reads the stored entries. Selection polling reads the
    /// currently selected entry as the user D-pads through the log.
    /// </summary>
    static class DuelLogReader
    {
        private static readonly List<string> _entries = new();
        private static string _lastSpoken = "";
        private static Il2CppYgomSystem.UI.SelectionItem _lastSelectedItem;

        /// <summary>
        /// Capture text from an OnUpdateShow* callback.
        /// Called during gameplay as log entries are created.
        /// Stores the rendered text for later reading.
        /// </summary>
        public static void CaptureItem(UnityEngine.GameObject eom, string type, int dataindex)
        {
            try
            {
                if (eom == null) return;
                var results = TextExtractor.ExtractAll(eom);
                if (results.Count == 0) return;
                string text = string.Join(", ", results.ConvertAll(r => r.Text));
                if (string.IsNullOrWhiteSpace(text)) return;

                _entries.Add(text);
                Log.Write($"[DuelLog-Item] [{type}:{dataindex}] {text}");
            }
            catch { }
        }

        /// <summary>
        /// Read recent log entries when the log opens.
        /// Speaks the last several captured entries.
        /// </summary>
        public static void ReadRecentEntries()
        {
            try
            {
                if (_entries.Count == 0)
                {
                    Speech.SayQueued("No entries");
                    Log.Write("[DuelLog-Read] No entries");
                    return;
                }

                // Read the last few entries (most recent actions)
                int start = Math.Max(0, _entries.Count - 8);
                var recent = new List<string>();
                for (int i = start; i < _entries.Count; i++)
                    recent.Add(_entries[i]);

                string combined = string.Join(". ", recent);
                Log.Write($"[DuelLog-Read] {combined}");
                Speech.SayQueued(combined);
            }
            catch (Exception ex) { Log.Write($"[DuelLog-Read] {ex.Message}"); }
        }

        /// <summary>
        /// Initialize selection tracking when the log opens.
        /// </summary>
        public static void InitScroll(DuelLogScrollView sv)
        {
            _lastSelectedItem = null;
            _lastSpoken = "";
            Log.Write("[DuelLog] Selection tracking initialized");
        }

        /// <summary>
        /// Poll the selector's selected item each frame while the log is open.
        /// When D-pad changes the selection, reads the newly selected entry.
        /// The SelectionItem itself has no text — the visual content is in a
        /// sibling or parent element, so we search up the hierarchy.
        /// Called from BlindDuelCore.Update().
        /// </summary>
        public static void PollSelection()
        {
            try
            {
                var sv = DuelState.LogScrollView;
                if (sv == null) return;

                var selector = sv.selector;
                if (selector == null) return;

                var selected = selector.GetSelectedItem();
                if (selected == null) return;

                // Same item — no change
                if (selected == _lastSelectedItem) return;
                _lastSelectedItem = selected;

                // Clear dedup on every selection change so returning to a
                // previously-spoken entry still reads it
                _lastSpoken = "";

                var go = selected.gameObject;
                if (go == null) return;

                string text = null;
                var itemName = go.name;

                // LP face icons: read individual player LP from Engine
                if (itemName == "FaceIconL" || itemName == "FaceIconR")
                {
                    int player = itemName == "FaceIconL" ? 0 : 1;
                    int lp = DuelClient.GetLP(player);
                    string label = DuelState.IsMyPlayer(player) ? "Your" : "Opponent";
                    text = $"{label} LP: {lp}";
                }

                // Card entries (Card0/Card1/...) are handled by PatchDuelLogSetCard
                // via CardInfo.SetCardByCardId — skip here
                if (text == null && itemName.StartsWith("Card") && go.transform.parent?.name == "CardRoot")
                    return;

                // Try the item's text, then parent, then grandparent
                if (text == null)
                {
                    var transform = go.transform;
                    var results = TextExtractor.ExtractAll(go);
                    if (results.Count > 0)
                        text = string.Join(", ", results.ConvertAll(r => r.Text));

                    if (string.IsNullOrWhiteSpace(text) && transform.parent != null)
                    {
                        results = TextExtractor.ExtractAll(transform.parent.gameObject);
                        if (results.Count > 0)
                            text = string.Join(", ", results.ConvertAll(r => r.Text));
                    }

                    if (string.IsNullOrWhiteSpace(text) && transform.parent?.parent != null)
                    {
                        results = TextExtractor.ExtractAll(transform.parent.parent.gameObject);
                        if (results.Count > 0)
                            text = string.Join(", ", results.ConvertAll(r => r.Text));
                    }
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    Log.Write($"[DuelLog-Poll] No text: {itemName}");
                    return;
                }

                _lastSpoken = text;
                Log.Write($"[DuelLog-Nav] {text}");
                Speech.SayItem(text);
            }
            catch (Exception ex) { Log.Write($"[DuelLog-Poll] {ex.Message}"); }
        }

        public static void Reset()
        {
            _entries.Clear();
            _lastSpoken = "";
            _lastSelectedItem = null;
        }
    }
}
