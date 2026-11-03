// ---------------------------------------------------------------
//  Abgeschlossen - was wir abgeschlossen haben, bleibt zu
//
//  Merkt sich je Raid jede Tuer, die DU ueber "Abschliessen"
//  verriegelt hast (mit oder ohne Schloss), und laesst sie nur von
//  dir selbst wieder aufgehen: per Schluessel, "Aufschliessen"
//  oder Eintreten. Bots kommen nicht durch.
//
//  WARUM ES NOETIG IST: Vanilla-Bots fassen verschlossene Tueren
//  nicht an (BotDoorOpener.InteractionWithDoor kennt nur Shut und
//  Open, WantPassTheDoor ebenso), SAIN auch nicht
//  (DoorOpener.InteractWithDoor: nur Shut/Open). ORBIT dagegen
//  bricht verschlossene Tueren fuer PMCs auf, und zwar an den
//  Spielregeln vorbei:
//    - WorldInteractiveObject.Unlock() direkt (GhostUnlockDoor,
//      Ankunft am Ziel, RushPlan)
//    - UnlockCoroutine() direkt (MULTISTEP-Kartenleser)
//    - DoorState = Open plus CurrentAngle direkt (SnapDoorOpen,
//      DoorWatch, Stuck-Behandlung "closed previously-opened door")
//    - LockForInteraction + Interact(Open) (OperationPlan)
//  Alle diese Wege laufen am Ende durch vier Stellen der Tuer:
//  den DoorState-Setter, SetDoorState, Unlock und KickOpen - und
//  den CurrentAngle-Setter, der das Tuerblatt dreht. Genau dort
//  sitzt die Sperre. Zusaetzlich sagen wir ORBIT ueber dessen
//  eigene Abfrage MultiStepAccess.CanForceUnlock "nein" - so
//  plant ORBIT gar nicht erst einen Weg durch unsere Tuer.
//
//  WER DARF: Nur der eigene Spieler. Startet er an einer unserer
//  Tueren eine Bedienung (MovementContext.StartInteraction fuer
//  Aufsperren mit Schluessel, StartDoorBreak fuers Eintreten),
//  bekommt die Tuer fuer ein paar Sekunden eine Freigabe. Bots
//  laufen ueber dieselben Methoden, haben aber IsYourPlayer=false.
//
//  KI-NAVIGATION: Lock() schreibt ueber den DoorState-Setter, der
//  OnDoorStateChanged feuert. Daran haengt NavMeshDoorLink, und
//  DrakiaXYZ-Waypoints schaltet dort fuer verschlossene Tueren den
//  Carver_Closed ein (DoorLinkStateChangePatch) - das NavMesh ist
//  an der Tuer dann zu, Bots planen aussen herum.
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Text;
using Comfort.Common;
using Cutscene;
using EFT;
using EFT.GameTriggers;
using EFT.GlobalEvents;
using EFT.Interactive;
using HarmonyLib;
using UnityEngine;

namespace TuerAbschliessen
{
	internal static class Abgeschlossen
	{
		// Wie lange eine Freigabe haelt. Eintreten und Aufsperren mit
		// Schluessel brauchen keine drei Sekunden bis zum Endzustand.
		private const float FreigabeSekunden = 8f;

		private static readonly HashSet<WorldInteractiveObject> _unsere = new HashSet<WorldInteractiveObject>();

		private static readonly Dictionary<WorldInteractiveObject, float> _freigabeBis = new Dictionary<WorldInteractiveObject, float>();

		private static readonly Dictionary<WorldInteractiveObject, float> _letzteMeldung = new Dictionary<WorldInteractiveObject, float>();

		private static object _welt;

		private static HashSet<WorldInteractiveObject> _skriptTueren;

		private static HashSet<string> _skriptTuerIds;

		// Neuer Raid, neue Tueren: alles vergessen.
		private static void WeltPruefen()
		{
			object welt = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
			if (ReferenceEquals(welt, _welt))
			{
				return;
			}
			_welt = welt;
			_unsere.Clear();
			_freigabeBis.Clear();
			_letzteMeldung.Clear();
			_skriptTueren = null;
			_skriptTuerIds = null;
		}

		internal static void Merken(WorldInteractiveObject tuer)
		{
			WeltPruefen();
			_unsere.Add(tuer);
			_freigabeBis.Remove(tuer);
		}

		internal static bool IstUnsere(WorldInteractiveObject tuer)
		{
			return _unsere.Count != 0 && (object)tuer != null && _unsere.Contains(tuer);
		}

		internal static void Freigeben(WorldInteractiveObject tuer)
		{
			if (IstUnsere(tuer))
			{
				_freigabeBis[tuer] = Time.time + FreigabeSekunden;
			}
		}

		internal static void FreigabeBeenden(WorldInteractiveObject tuer)
		{
			if (_freigabeBis.Count != 0 && (object)tuer != null)
			{
				_freigabeBis.Remove(tuer);
			}
		}

		// Zu heisst: verriegelt - oder mitten in einer Bedienung, die
		// von "verriegelt" aus gestartet wurde.
		private static bool IstZu(WorldInteractiveObject tuer)
		{
			EDoorState zustand = tuer.DoorState;
			return zustand == EDoorState.Locked
				|| (zustand == EDoorState.Interacting && tuer.FallbackState == EDoorState.Locked);
		}

		// true = diesen Zugriff abweisen.
		internal static bool Abweisen(WorldInteractiveObject tuer, string was)
		{
			if (!IstUnsere(tuer) || !Plugin.BotsAussperren.Value || !IstZu(tuer))
			{
				return false;
			}
			if (_freigabeBis.TryGetValue(tuer, out float bis) && Time.time <= bis)
			{
				return false;
			}
			Melden(tuer, was);
			return true;
		}

		// Nach jedem Zustandswechsel: ist die Tuer wieder offen oder
		// nur zu, gehoert sie nicht mehr uns.
		internal static void NachZustand(WorldInteractiveObject tuer)
		{
			if (!IstUnsere(tuer))
			{
				return;
			}
			EDoorState zustand = tuer.DoorState;
			if (zustand == EDoorState.Open || zustand == EDoorState.Shut)
			{
				_unsere.Remove(tuer);
				_freigabeBis.Remove(tuer);
				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo("Tuer " + tuer.Id + " ist wieder " + zustand + " - nicht mehr gesperrt.");
				}
			}
		}

		private static void Melden(WorldInteractiveObject tuer, string was)
		{
			if (!Plugin.Protokoll.Value)
			{
				return;
			}
			// Hoechstens alle zwei Sekunden je Tuer - CurrentAngle wird
			// sonst jeden Frame gemeldet.
			if (_letzteMeldung.TryGetValue(tuer, out float zuletzt) && Time.time - zuletzt < 2f)
			{
				return;
			}
			_letzteMeldung[tuer] = Time.time;
			Plugin.Log.LogInfo("Abgewiesen an " + tuer.Id + ": " + was + " - Aufrufer: " + Aufrufer());
		}

		// Die ersten fremden Aufrufer - zeigt beim Test, WER es versucht
		// hat (ORBIT, SAIN, Spiel).
		private static string Aufrufer()
		{
			try
			{
				System.Diagnostics.StackFrame[] rahmen = new System.Diagnostics.StackTrace(3, false).GetFrames();
				if (rahmen == null)
				{
					return "?";
				}
				StringBuilder sb = new StringBuilder();
				int n = 0;
				for (int i = 0; i < rahmen.Length && n < 4; i++)
				{
					System.Reflection.MethodBase m = rahmen[i].GetMethod();
					if (m == null || m.DeclaringType == null || m.DeclaringType.Namespace == "TuerAbschliessen")
					{
						continue;
					}
					if (n > 0)
					{
						sb.Append(" <- ");
					}
					sb.Append(m.DeclaringType.FullName).Append('.').Append(m.Name);
					n++;
				}
				return sb.ToString();
			}
			catch
			{
				return "?";
			}
		}

		// -----------------------------------------------------------
		//  Welche Tueren OHNE Schloss duerfen abgeschlossen werden?
		//
		//  Nur gewoehnliche Tueren (Door, SlidingDoor). Nicht:
		//   - KeycardDoor (eigene Menuemethode, Kartenleser),
		//     DoorSwitch (oeffnen loest einen Schalter aus)
		//   - Tueren, die das Spiel selbst ohne Schluessel verriegelt
		//     hat (InitialDoorState Locked): Strom-, Quest- und
		//     Nur-Eintreten-Tueren
		//   - Tueren mit TriggersMap (Zustand loest Spielereignisse aus)
		//   - Tueren, die ein Skript fernsteuert oder beobachtet:
		//     Switch.Door (Strom/Tore), HandlerInteractable (Trigger,
		//     z. B. Quests), InteractiveProxy (Kartenleser-Stellvertreter),
		//     Zwischensequenzen, Leuchtturm-Waerter, Quest-Ereignisse
		//     (SpecificDoorStateChangeEventGenerator,
		//     DoorInteractingEventFilter)
		//   - Sonderkontexte (specificInteractionContext, z. B. Waerter)
		//   - kaputte, gesperrte oder abgeschaltete Tueren
		//  Ausgang-Tueren (ExfiltrationDoor) und Transits sind gar keine
		//  Door und landen nie in diesem Menue.
		// -----------------------------------------------------------
		internal static bool OhneSchlossAbschliessbar(Door tuer, out string grund)
		{
			Type typ = tuer.GetType();
			if (typ != typeof(Door) && typ != typeof(SlidingDoor))
			{
				grund = "Typ " + typ.Name;
				return false;
			}
			if (!tuer.Operatable || tuer.NoInteractionsAllowed || tuer.IsBroken
				|| !tuer.enabled || !tuer.gameObject.activeInHierarchy)
			{
				grund = "nicht bedienbar";
				return false;
			}
			if (tuer.specificInteractionContext != ESpecificInteractionContext.None)
			{
				grund = "Sonderkontext " + tuer.specificInteractionContext;
				return false;
			}
			if (tuer.InitialDoorState == EDoorState.Locked)
			{
				grund = "vom Spiel ohne Schluessel verriegelt";
				return false;
			}
			if (tuer.TriggersMap != null && tuer.TriggersMap.Length > 0)
			{
				grund = "loest Trigger aus";
				return false;
			}
			SkriptTuerenSammeln();
			if (_skriptTueren.Contains(tuer) || (!string.IsNullOrEmpty(tuer.Id) && _skriptTuerIds.Contains(tuer.Id)))
			{
				grund = "von einem Skript gesteuert";
				return false;
			}
			grund = null;
			return true;
		}

		// Einmal je Raid, erst wenn es gebraucht wird.
		private static void SkriptTuerenSammeln()
		{
			WeltPruefen();
			if (_skriptTueren != null)
			{
				return;
			}
			HashSet<WorldInteractiveObject> tueren = new HashSet<WorldInteractiveObject>();
			HashSet<string> ids = new HashSet<string>();
			try
			{
				foreach (Switch schalter in UnityEngine.Object.FindObjectsOfType<Switch>(true))
				{
					Dazu(tueren, schalter.Door);
				}
				foreach (HandlerInteractable h in UnityEngine.Object.FindObjectsOfType<HandlerInteractable>(true))
				{
					Dazu(tueren, h._interactable);
					if (!string.IsNullOrEmpty(h._interactableId))
					{
						ids.Add(h._interactableId);
					}
				}
				foreach (InteractiveProxy p in UnityEngine.Object.FindObjectsOfType<InteractiveProxy>(true))
				{
					Dazu(tueren, p.Link);
				}
				foreach (InteractiveObjectCutsceneTrigger c in UnityEngine.Object.FindObjectsOfType<InteractiveObjectCutsceneTrigger>(true))
				{
					Dazu(tueren, c.interactiveObject);
				}
				foreach (LighthouseKeeperZone z in UnityEngine.Object.FindObjectsOfType<LighthouseKeeperZone>(true))
				{
					Dazu(tueren, z._keeperEntranceDoor);
				}
				foreach (SpecificDoorStateChangeEventGenerator g in UnityEngine.Object.FindObjectsOfType<SpecificDoorStateChangeEventGenerator>(true))
				{
					Dazu(tueren, g._targetDoor);
				}
				foreach (DoorInteractingEventFilter f in UnityEngine.Object.FindObjectsOfType<DoorInteractingEventFilter>(true))
				{
					Dazu(tueren, f._doorForTracking);
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Skript-Tueren suchen - " + ex);
			}
			_skriptTueren = tueren;
			_skriptTuerIds = ids;
			if (Plugin.Protokoll.Value)
			{
				Plugin.Log.LogInfo("Skript-Tueren in diesem Raid: " + tueren.Count + " (+ " + ids.Count + " Trigger-Ids) - ohne Schloss nicht abschliessbar.");
			}
		}

		private static void Dazu(HashSet<WorldInteractiveObject> tueren, WorldInteractiveObject tuer)
		{
			if (tuer != null)
			{
				tueren.Add(tuer);
			}
		}
	}

	// ---------------------------------------------------------------
	//  Die Sperre selbst
	// ---------------------------------------------------------------

	// Freigabe fuer den eigenen Spieler: Aufsperren mit Schluessel und
	// jede andere normale Bedienung ...
	[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StartInteraction))]
	internal static class FreigabeBedienen
	{
		[HarmonyPrefix]
		internal static void Vorher(MovementContext __instance, WorldInteractiveObject interactive)
		{
			if (__instance._player != null && __instance._player.IsYourPlayer)
			{
				Abgeschlossen.Freigeben(interactive);
			}
		}
	}

	// ... und Eintreten.
	[HarmonyPatch(typeof(MovementContext), nameof(MovementContext.StartDoorBreak))]
	internal static class FreigabeEintreten
	{
		[HarmonyPrefix]
		internal static void Vorher(MovementContext __instance, WorldInteractiveObject interactive)
		{
			if (__instance._player != null && __instance._player.IsYourPlayer)
			{
				Abgeschlossen.Freigeben(interactive);
			}
		}
	}

	// Fehltritt: die Tuer faellt gleich in "verriegelt" zurueck, die
	// Freigabe ist verbraucht.
	[HarmonyPatch(typeof(Door), nameof(Door.FailBreach))]
	internal static class FreigabeNachFehltritt
	{
		[HarmonyPostfix]
		internal static void Nachher(Door __instance)
		{
			Abgeschlossen.FreigabeBeenden(__instance);
		}
	}

	// 1) Jeder Zustandswechsel laeuft durch diesen Setter - auch
	//    LockForInteraction, UnlockCoroutine und ORBITs
	//    "DoorState = Open".
	[HarmonyPatch(typeof(Door), nameof(Door.DoorState), MethodType.Setter)]
	internal static class SperreZustand
	{
		[HarmonyPrefix]
		internal static bool Vorher(Door __instance, EDoorState value)
		{
			if (value == EDoorState.Locked)
			{
				return true;
			}
			return !Abgeschlossen.Abweisen(__instance, "DoorState = " + value);
		}

		[HarmonyPostfix]
		internal static void Nachher(Door __instance)
		{
			Abgeschlossen.NachZustand(__instance);
		}
	}

	// 2) Oeffnen/Schliessen mit Animation: SetDoorState startet die
	//    Drehung des Tuerblatts, bevor der Zustand gesetzt wird.
	[HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.SetDoorState))]
	internal static class SperreSetDoorState
	{
		[HarmonyPrefix]
		internal static bool Vorher(WorldInteractiveObject __instance, EDoorState state)
		{
			if (state == EDoorState.Locked)
			{
				return true;
			}
			return !Abgeschlossen.Abweisen(__instance, "SetDoorState(" + state + ")");
		}
	}

	// 3) Aufsperren.
	[HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.Unlock))]
	internal static class SperreUnlock
	{
		[HarmonyPrefix]
		internal static bool Vorher(WorldInteractiveObject __instance)
		{
			return !Abgeschlossen.Abweisen(__instance, "Unlock()");
		}
	}

	// 4) Eintreten.
	[HarmonyPatch]
	internal static class SperreEintreten
	{
		[HarmonyTargetMethod]
		internal static System.Reflection.MethodBase Ziel()
		{
			// KickOpen(bool) ruft diese Ueberladung - eine Stelle genuegt.
			return AccessTools.Method(typeof(Door), nameof(Door.KickOpen),
				new Type[] { typeof(Vector3), typeof(bool) });
		}

		[HarmonyPrefix]
		internal static bool Vorher(Door __instance)
		{
			return !Abgeschlossen.Abweisen(__instance, "KickOpen");
		}
	}

	// 5) Das Tuerblatt selbst: ORBIT dreht es nach "DoorState = Open"
	//    zusaetzlich direkt auf. Solange die Tuer verriegelt ist, bleibt
	//    es im Rahmen.
	[HarmonyPatch(typeof(WorldInteractiveObject), nameof(WorldInteractiveObject.CurrentAngle), MethodType.Setter)]
	internal static class SperreWinkel
	{
		[HarmonyPrefix]
		internal static void Vorher(WorldInteractiveObject __instance, ref float value)
		{
			WinkelHalten(__instance, ref value);
		}

		internal static void WinkelHalten(WorldInteractiveObject tuer, ref float value)
		{
			if (!Abgeschlossen.IstUnsere(tuer) || tuer.DoorState != EDoorState.Locked)
			{
				return;
			}
			float zu = tuer.GetAngle(EDoorState.Locked);
			if (Mathf.Approximately(value, zu))
			{
				return;
			}
			if (Abgeschlossen.Abweisen(tuer, "CurrentAngle = " + value.ToString("F0")))
			{
				value = zu;
			}
		}
	}

	// SlidingDoor ueberschreibt den Setter, ohne die Basis zu rufen.
	[HarmonyPatch(typeof(SlidingDoor), nameof(SlidingDoor.CurrentAngle), MethodType.Setter)]
	internal static class SperreWinkelSchiebetuer
	{
		[HarmonyPrefix]
		internal static void Vorher(SlidingDoor __instance, ref float value)
		{
			SperreWinkel.WinkelHalten(__instance, ref value);
		}
	}

	// 6) ORBIT gezielt: dessen eigene Frage "darf ein PMC diese Tuer
	//    aufbrechen?" (MultiStepAccess.CanForceUnlock). Mit "nein"
	//    behandelt ORBIT unsere Tuer wie eine stromgesicherte: kein
	//    Weg wird durch sie geplant, kein Ziel dahinter vergeben.
	//    Wird nur gesetzt, wenn ORBIT geladen ist (Plugin.Awake).
	internal static class OrbitSperre
	{
		internal const string OrbitGuid = "com.chazut.orbit";

		internal static void Anwenden(Harmony harmony)
		{
			Type typ = AccessTools.TypeByName("Orbit.Systems.MultiStepAccess");
			System.Reflection.MethodInfo ziel = typ == null ? null
				: AccessTools.Method(typ, "CanForceUnlock", new Type[] { typeof(Door) });
			if (ziel == null || ziel.ReturnType != typeof(bool))
			{
				Plugin.Log.LogWarning("Tuer abschliessen: ORBIT geladen, aber MultiStepAccess.CanForceUnlock(Door) nicht gefunden - "
					+ "die Sperre an der Tuer greift trotzdem, ORBIT plant nur weiter Wege hindurch.");
				return;
			}
			harmony.Patch(ziel, postfix: new HarmonyMethod(typeof(OrbitSperre), nameof(Nachher)));
			Plugin.Log.LogInfo("Tuer abschliessen: ORBIT erkannt - abgeschlossene Tueren sind fuer ORBIT tabu.");
		}

		internal static void Nachher(Door door, ref bool __result)
		{
			if (__result && Plugin.BotsAussperren.Value && Abgeschlossen.IstUnsere(door) && door.DoorState == EDoorState.Locked)
			{
				__result = false;
			}
		}
	}
}
