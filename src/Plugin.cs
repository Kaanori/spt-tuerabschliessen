// ---------------------------------------------------------------
//  Tuer abschliessen
//
//  Haengt den Eintrag "Abschliessen" ins Tuermenue. Damit laesst
//  sich eine Tuer, die man mit dem Schluessel aufgesperrt und
//  danach wieder zugemacht hat, erneut verriegeln.
//
//  WARUM DAS UEBERHAUPT GEHT: Das Spiel kann es bereits. In
//  EFT.Interactive.WorldInteractiveObject steht eine fertige,
//  oeffentliche Methode Lock():
//
//      _interaction.ResultState = EDoorState.Locked;
//      DoorState = EDoorState.Locked;
//      CurrentAngle = GetAngle(DoorState);
//
//  Auch EInteractionType.Lock gibt es. BSG hat den Menueeintrag
//  nur nie verdrahtet: EFT.InteractionContextHelper
//  .GetAvailableActions(GamePlayerOwner, Door) kennt genau
//  OpenDoor, CloseDoor, Breach und die drei grauen Taktik-
//  Eintraege. Hier wird nichts nachgebaut - wir haengen uns mit
//  einem Postfix hinter diese Methode und legen einen Eintrag
//  obendrauf.
//
//  NICHT UEBER Door.Interact(..., EInteractionType.Lock) GEHEN.
//  Die Basisklasse verarbeitet Lock, aber Door ueberschreibt
//  Interact und ihr Schalter kennt nur Open, Close, Unlock und
//  Breach - der Aufruf liefe ins Leere. Darum direkt door.Lock().
//  Kostet die Griff-Animation, wirkt aber zuverlaessig.
//
//  BOTS BEKOMMEN ES MIT: Lock() schreibt ueber den DoorState-
//  Setter, und der feuert OnDoorStateChanged sowie
//  DoorStateChanged(). Daran haengt die KI-Navigation.
//
//  KARTENTUEREN (KeycardDoor) sind absichtlich aussen vor - die
//  haben ihre eigene Menuemethode und ein anderes Schloss.
//
//  Einstellbar unter F12 -> Tuer abschliessen.
// ---------------------------------------------------------------
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using EFT.Interactive;
using EFT.UI;
using HarmonyLib;

namespace TuerAbschliessen
{
	[BepInPlugin("com.kaan.tuerabschliessen", "Tuer abschliessen", "1.0.0")]
	public class Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;

		internal static ConfigEntry<bool> An;

		internal static ConfigEntry<bool> SchluesselNoetig;

		internal static ConfigEntry<string> Beschriftung;

		internal static ConfigEntry<bool> Protokoll;

		internal static ConfigEntry<bool> JedeTuerEintretbar;

		internal static ConfigEntry<bool> EintretenGelingtImmer;

		internal static ConfigEntry<bool> BotsTretenEin;

		private void Awake()
		{
			Log = base.Logger;

			An = base.Config.Bind("1. Allgemein", "Eingeschaltet", defaultValue: true,
				"Zeigt den Eintrag zum Abschliessen im Tuermenue.");

			SchluesselNoetig = base.Config.Bind("1. Allgemein", "Schluessel noetig", defaultValue: true,
				"An: nur mit dem passenden Schluessel in der Tasche. " +
				"Aus: jede Tuer mit Schloss laesst sich abschliessen.");

			JedeTuerEintretbar = base.Config.Bind("1. Allgemein", "Jede Tuer eintretbar", defaultValue: true,
				"An: \"Eintreten\" ist an jeder geschlossenen Tuer waehlbar - auch an\n"
				+ "denen, die das Spiel dafuer nie vorgesehen hat, und egal aus\n"
				+ "welchem Winkel du davorstehst.");

			EintretenGelingtImmer = base.Config.Bind("1. Allgemein", "Eintreten gelingt immer", defaultValue: true,
				"An: kein Fehltritt mehr. Ab Werk misslingt der Tritt immer, wenn\n"
				+ "die Tuer zu dir hin aufgeht - dann trittst du sie ins Schloss\n"
				+ "statt auf.");

			BotsTretenEin = base.Config.Bind("1. Allgemein", "Bots duerfen eintreten", defaultValue: false,
				"Aus: Bots oeffnen Tueren nur noch normal und treten keine mehr ein.\n"
				+ "An: Bots wuerfeln wie ab Werk (BREACH_CHANCE_100 ihrer Schwierigkeit).");

			Beschriftung = base.Config.Bind("2. Anzeige", "Beschriftung", "Abschliessen",
				"Text des Menueeintrags. Das Spiel schickt Menuetexte durch seine " +
				"Uebersetzungstabelle; steht der Text dort nicht drin, wird er " +
				"unveraendert angezeigt - genau das ist hier gewollt.");

			Protokoll = base.Config.Bind("3. Diagnose", "Protokoll", defaultValue: false,
				"Schreibt jede Abschliess-Aktion in die BepInEx-Konsole.");

			try
			{
				new Harmony("com.kaan.tuerabschliessen").PatchAll();
				Log.LogInfo("Tuer abschliessen: bereit.");
			}
			catch (Exception ex)
			{
				Log.LogError("Tuer abschliessen: Patch fehlgeschlagen - " + ex);
			}
		}
	}

	// Haengt sich hinter den Aufbau des Tuermenues.
	[HarmonyPatch]
	internal static class TuermenuePatch
	{
		// GetAvailableActions gibt es mehrfach - fuer Kisten, Kofferraeume,
		// Kartentueren und so weiter. Statt den Namen als Zeichenkette
		// hinzuschreiben und auf die richtige Ueberladung zu hoffen, lassen
		// wir den Compiler sie aussuchen: Wenn das Spiel diese Methode mit
		// genau dieser Signatur nicht mehr hat, scheitert schon der Bau -
		// und nicht erst das Plugin im laufenden Raid.
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
				if (!Plugin.An.Value)
				{
					return;
				}
				if (__result == null || __result.Actions == null)
				{
					return;
				}
				if ((object)door == null || (object)owner == null || (object)owner.Player == null)
				{
					return;
				}

				// Nur eine zugefallene Tuer laesst sich abschliessen. Offen
				// heisst: erst zumachen. Locked heisst: ist schon zu.
				if (door.DoorState != EDoorState.Shut)
				{
					return;
				}

				// Ohne KeyId hat die Tuer gar kein Schloss.
				if (string.IsNullOrEmpty(door.KeyId))
				{
					return;
				}

				// Steckt der passende Schluessel in der Tasche? GetKey sitzt
				// in PlayerOwner und macht genau die Pruefung, die das Spiel
				// auch fuers Aufsperren benutzt.
				if (Plugin.SchluesselNoetig.Value && owner.GetKey(door) == null)
				{
					return;
				}

				string text = Plugin.Beschriftung.Value;

				// Kein zweiter Eintrag, falls die Methode mehrfach durchlaeuft.
				// Bewusst mit Index statt foreach - die Liste wird waehrend des
				// Menueaufbaus an anderer Stelle noch angefasst.
				for (int i = 0; i < __result.Actions.Count; i++)
				{
					InteractionAction interactionAction = __result.Actions[i];
					if (interactionAction != null && interactionAction.Name == text)
					{
						return;
					}
				}

				Door tuer = door;
				GamePlayerOwner besitzer = owner;

				__result.Actions.Add(new InteractionAction
				{
					Name = text,
					Disabled = !door.Operatable,
					Action = delegate
					{
						Abschliessen(besitzer, tuer);
					}
				});

				// Das Menue faerbt sich grau, solange ein Fehlertext gesetzt
				// ist. Wir haben gerade einen brauchbaren Eintrag angehaengt.
				if (__result.Actions.Count > 0)
				{
					__result.Error = null;
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Menueaufbau - " + ex);
			}
		}

		private static void Abschliessen(GamePlayerOwner owner, Door door)
		{
			try
			{
				// Denselben Zustand zuruecksetzen wie Oeffnen und Schliessen,
				// sonst haengt die Spielfigur im Bedien-Zustand fest.
				owner.Player.MovementContext.ResetCanUsePropState();

				door.Lock();

				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo("Abgeschlossen: " + door.Id + " (Schluessel " + door.KeyId + ")");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Abschliessen - " + ex);
			}
		}
	}
}
