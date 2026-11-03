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
//  SEIT 1.1.0 - TUEREN OHNE SCHLOSS (Einstellung, Standard aus):
//  Auch Tueren ohne KeyId lassen sich abschliessen. Welche genau,
//  entscheidet Abgeschlossen.OhneSchlossAbschliessbar. Das Spiel
//  selbst bietet an so einer Tuer danach nur noch "Eintreten" an:
//  InteractionContextHelper.TryAddUnlock bricht bei leerer KeyId
//  sofort ab, ein "Unlock" erscheint nie. Darum haengen wir an
//  Tueren, die WIR verriegelt haben, "Aufschliessen" an - das ruft
//  die Unlock()-Methode des Spiels (Zustand zurueck auf Shut,
//  ebenfalls ueber den DoorState-Setter).
//
//  SEIT 1.1.0 - BOTS: Jede Tuer, die du abgeschlossen hast, merkt
//  sich das Plugin bis Raidende; kein Bot bekommt sie auf (siehe
//  Abgeschlossen.cs).
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
	[BepInPlugin("com.kaan.tuerabschliessen", "Tuer abschliessen", "1.1.0")]
	// ORBIT nur, damit es vor uns geladen ist - Pflicht ist es nicht.
	[BepInDependency(OrbitSperre.OrbitGuid, BepInDependency.DependencyFlags.SoftDependency)]
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

		internal static ConfigEntry<bool> AuchOhneSchloss;

		internal static ConfigEntry<bool> BotsAussperren;

		internal static ConfigEntry<string> BeschriftungAuf;

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

			AuchOhneSchloss = base.Config.Bind("1. Allgemein", "Auch Tueren ohne Schloss abschliessen", defaultValue: false,
				"An: auch gewoehnliche Tueren ohne Schluesselloch lassen sich abschliessen.\n"
				+ "Ausgenommen bleiben Kartentueren, Strom-/Schalter-Tueren, Quest- und\n"
				+ "Trigger-Tueren und Tueren, die das Spiel selbst verriegelt hat.\n"
				+ "Wieder auf geht so eine Tuer ueber \"Aufschliessen\" oder Eintreten.");

			BotsAussperren = base.Config.Bind("1. Allgemein", "Bots respektieren abgeschlossene Tueren", defaultValue: true,
				"An: eine Tuer, die DU abgeschlossen hast, bekommt kein Bot auf - weder\n"
				+ "aufschliessen noch oeffnen noch eintreten, auch nicht ueber ORBIT oder SAIN.\n"
				+ "Gilt bis Raidende oder bis du sie selbst wieder aufmachst.");

			Beschriftung = base.Config.Bind("2. Anzeige", "Beschriftung", "Abschliessen",
				"Text des Menueeintrags. Das Spiel schickt Menuetexte durch seine " +
				"Uebersetzungstabelle; steht der Text dort nicht drin, wird er " +
				"unveraendert angezeigt - genau das ist hier gewollt.");

			BeschriftungAuf = base.Config.Bind("2. Anzeige", "Beschriftung Aufschliessen", "Aufschliessen",
				"Text des Menueeintrags an einer Tuer ohne Schloss, die du abgeschlossen hast.");

			Protokoll = base.Config.Bind("3. Diagnose", "Protokoll", defaultValue: false,
				"Schreibt jede Abschliess-Aktion in die BepInEx-Konsole - und jeden\n"
				+ "abgewiesenen Versuch eines Bots samt Aufrufer.");

			try
			{
				Harmony harmony = new Harmony("com.kaan.tuerabschliessen");
				harmony.PatchAll();
				if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(OrbitSperre.OrbitGuid))
				{
					OrbitSperre.Anwenden(harmony);
				}
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

				bool hatSchloss = !string.IsNullOrEmpty(door.KeyId);

				// Nur eine zugefallene Tuer laesst sich abschliessen. Offen
				// heisst: erst zumachen.
				if (door.DoorState == EDoorState.Shut)
				{
					if (hatSchloss)
					{
						// Steckt der passende Schluessel in der Tasche? GetKey
						// sitzt in PlayerOwner und macht genau die Pruefung, die
						// das Spiel auch fuers Aufsperren benutzt.
						if (Plugin.SchluesselNoetig.Value && owner.GetKey(door) == null)
						{
							return;
						}
					}
					else
					{
						// Ohne KeyId hat die Tuer gar kein Schloss - nur mit
						// der Zusatz-Einstellung, und nur geeignete Tueren.
						if (!Plugin.AuchOhneSchloss.Value)
						{
							return;
						}
						if (!Abgeschlossen.OhneSchlossAbschliessbar(door, out string grund))
						{
							if (Plugin.Protokoll.Value && door.Id != _letzteAbgelehnte)
							{
								_letzteAbgelehnte = door.Id;
								Plugin.Log.LogInfo("Ohne Schloss nicht abschliessbar: " + door.Id + " (" + grund + ")");
							}
							return;
						}
					}

					Door tuer = door;
					GamePlayerOwner besitzer = owner;
					EintragAnhaengen(__result, Plugin.Beschriftung.Value, !door.Operatable, delegate
					{
						Abschliessen(besitzer, tuer);
					});
					return;
				}

				// Verriegelt - und zwar von UNS? Dann muss es auch wieder
				// aufgehen. Das Spiel bietet "Unlock" nur mit passendem
				// Schluessel an (TryAddUnlock); ohne KeyId nie.
				if (door.DoorState == EDoorState.Locked && Abgeschlossen.IstUnsere(door))
				{
					if (hatSchloss)
					{
						// Mit Schloss gilt wie bisher der Schluessel. Nur wer
						// "Schluessel noetig" aus hat, konnte ohne Schluessel
						// abschliessen - dem fehlt sonst der Weg zurueck.
						if (Plugin.SchluesselNoetig.Value || HatEintrag(__result, "Unlock"))
						{
							return;
						}
					}

					Door tuer = door;
					GamePlayerOwner besitzer = owner;
					EintragAnhaengen(__result, Plugin.BeschriftungAuf.Value, !door.Operatable, delegate
					{
						Aufschliessen(besitzer, tuer);
					});
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

				// Merken, damit nur wir sie wieder aufbekommen.
				if (door.DoorState == EDoorState.Locked)
				{
					Abgeschlossen.Merken(door);
				}

				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo("Abgeschlossen: " + door.Id + " ("
						+ (string.IsNullOrEmpty(door.KeyId) ? "ohne Schloss" : "Schluessel " + door.KeyId) + ")");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Abschliessen - " + ex);
			}
		}

		private static void Aufschliessen(GamePlayerOwner owner, Door door)
		{
			try
			{
				owner.Player.MovementContext.ResetCanUsePropState();

				// Die eigene Sperre fuer diesen einen Aufruf oeffnen.
				Abgeschlossen.Freigeben(door);

				// Wie im Spiel: der Bediener wird vorher eingetragen. Die
				// UnlockCoroutine liest ihn beim Ausloesen der Trigger.
				door.SetUser(owner.Player);

				// Die Unlock-Logik des Spiels: Zustand Shut ueber den
				// DoorState-Setter (KI-Navigation bekommt es mit), Winkel
				// zurueck, Trigger fuer "Shut" ausloesen.
				door.Unlock();

				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo("Aufgeschlossen: " + door.Id + " (jetzt " + door.DoorState + ")");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Aufschliessen - " + ex);
			}
		}

		private static string _letzteAbgelehnte;

		private static bool HatEintrag(AvailableInteractionState zustand, string name)
		{
			// Bewusst mit Index statt foreach - die Liste wird waehrend des
			// Menueaufbaus an anderer Stelle noch angefasst.
			for (int i = 0; i < zustand.Actions.Count; i++)
			{
				InteractionAction eintrag = zustand.Actions[i];
				if (eintrag != null && eintrag.Name == name)
				{
					return true;
				}
			}
			return false;
		}

		private static void EintragAnhaengen(AvailableInteractionState zustand, string text, bool grau, Action aktion)
		{
			// Kein zweiter Eintrag, falls die Methode mehrfach durchlaeuft.
			if (HatEintrag(zustand, text))
			{
				return;
			}
			zustand.Actions.Add(new InteractionAction
			{
				Name = text,
				Disabled = grau,
				Action = aktion
			});
			// Das Menue faerbt sich grau, solange ein Fehlertext gesetzt
			// ist. Wir haben gerade einen brauchbaren Eintrag angehaengt.
			zustand.Error = null;
		}
	}
}
