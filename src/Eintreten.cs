// ---------------------------------------------------------------
//  Jede Tuer eintretbar
//
//  Das Spiel kann Tueren eintreten, laesst es aber nur selten zu.
//  Vier Huerden stehen im Weg, und jede einzelne genuegt, damit
//  der Menueeintrag grau bleibt oder der Tritt ins Leere geht:
//
//   1. Zwei Schalter je Tuer, von BSG im Editor gesetzt:
//      CanBeBreached und CanInteractWithBreach. Bei den meisten
//      Tueren steht mindestens einer auf false.
//   2. Door.IsBreachAngle - man muss halbwegs frontal stehen.
//   3. Door.BreachSuccessRoll - die Tuer muss sich VON EINEM WEG
//      oeffnen; von der falschen Seite misslingt es immer.
//   4. Im Liegen bleibt der Eintrag grau.
//
//  WO DER HEBEL SITZT: Der Menueeintrag "Breach" wird IMMER
//  angehaengt, sobald die Tuer zu oder abgeschlossen ist - nur
//  sein Disabled-Kennzeichen wechselt. Es genuegt also, dieses
//  Kennzeichen zu loeschen, statt die Pruefungen einzeln
//  auszuhebeln.
//
//  Ob der Tritt gelingt, entscheidet spaeter ein einziges Feld:
//  MovementContext.NextBreachResult. In BreachDoorState steht
//      if (MovementContext.NextBreachResult) -> Tuer fliegt auf
//      else                                  -> FailBreach
//  Dieses Feld setzen wir auf true.
//
//  ABSICHTLICH NICHT Door.BreachSuccessRoll GEPATCHT: dieselbe
//  Methode fragt auch BotDoorOpener ab. Dort einzugreifen wuerde
//  jedem Scav jede Tuer oeffnen. MovementContext haben aber auch
//  Bots - darum wirkt der Patch nur bei IsYourPlayer.
//
//  BOTS: Ob ein Bot eintritt, wuerfelt BotDoorOpener.TryPassCurrentDoor
//  einmal je Tuer (BREACH_CHANCE_100) in _doBreach. Ist das false,
//  oeffnet er die Tuer normal. Genau diese Entscheidung setzen wir.
// ---------------------------------------------------------------
using System;
using System.Reflection;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;

namespace TuerAbschliessen
{
	// 1) Den Menueeintrag freischalten.
	[HarmonyPatch]
	internal static class EintretenFreischalten
	{
		[HarmonyTargetMethod]
		internal static MethodBase Ziel()
		{
			Func<GamePlayerOwner, Door, AvailableInteractionState> zeiger =
				InteractionContextHelper.GetAvailableActions;
			return zeiger.Method;
		}

		[HarmonyPostfix]
		internal static void Nachher(GamePlayerOwner owner, Door door, AvailableInteractionState __result)
		{
			try
			{
				if (!Plugin.An.Value || !Plugin.JedeTuerEintretbar.Value)
				{
					return;
				}
				if (__result == null || __result.Actions == null || (object)door == null)
				{
					return;
				}

				// Die beiden Schalter gleich mitziehen: andere Stellen im
				// Spiel fragen sie ebenfalls ab.
				door.CanBeBreached = true;
				door.CanInteractWithBreach = true;

				for (int i = 0; i < __result.Actions.Count; i++)
				{
					InteractionAction eintrag = __result.Actions[i];
					if (eintrag == null || eintrag.Name != "Breach")
					{
						continue;
					}
					// Eine Tuer, die das Spiel gar nicht bedienen laesst,
					// bleibt gesperrt - da wuerde ein Tritt nur haengen.
					eintrag.Disabled = !door.Operatable;
					if (Plugin.Protokoll.Value)
					{
						Plugin.Log.LogInfo("Eintreten freigeschaltet an " + door.Id
							+ " (Zustand " + door.DoorState + ")");
					}
					break;
				}

				if (__result.Actions.Count > 0)
				{
					__result.Error = null;
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Eintreten-Menue - " + ex);
			}
		}
	}

	// 2) Den Tritt gelingen lassen.
	[HarmonyPatch]
	internal static class EintretenGelingt
	{
		[HarmonyTargetMethod]
		internal static MethodBase Ziel()
		{
			return AccessTools.Method(
				typeof(MovementContext),
				nameof(MovementContext.StartDoorBreak),
				new Type[] { typeof(WorldInteractiveObject), typeof(InteractionResult), typeof(Action) });
		}

		[HarmonyPostfix]
		internal static void Nachher(MovementContext __instance)
		{
			try
			{
				if (!Plugin.An.Value || !Plugin.EintretenGelingtImmer.Value || __instance == null)
				{
					return;
				}
				// Nur die eigene Figur - Bots laufen ueber denselben Weg.
				if (__instance._player == null || !__instance._player.IsYourPlayer)
				{
					return;
				}

				// Erst das Ergebnis selbst - daran haengt in BreachDoorState
				// die Entscheidung zwischen "Tuer fliegt auf" und "Fehltritt".
				__instance.NextBreachResult = true;
				// Dann die Animation: das Original hat sie mit dem
				// gewuerfelten Wert schon gesetzt, sonst tritt die Figur
				// sichtbar daneben, waehrend die Tuer aufgeht.
				__instance.PlayerAnimatorSetKickSucceed(true);
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Eintreten-Ergebnis - " + ex);
			}
		}
	}

	// 3) Bots treten nicht ein: ihre Wuerfel-Entscheidung vorwegnehmen.
	[HarmonyPatch(typeof(BotDoorOpener), nameof(BotDoorOpener.TryPassCurrentDoor))]
	internal static class BotsTretenNichtEin
	{
		[HarmonyPrefix]
		internal static void Vorher(BotDoorOpener __instance)
		{
			if (Plugin.BotsTretenEin.Value || __instance._breachRolledForCurrentDoor)
			{
				return;
			}
			__instance._doBreach = false;
			__instance._breachRolledForCurrentDoor = true;
		}
	}
}
