// ---------------------------------------------------------------
//  SchlossBedienung - Abschliessen und Aufschliessen ueber den
//  Bedienweg des Spiels (seit 1.1.1)
//
//  WIE DAS SPIEL AUFSCHLIESST (InteractionContextHelper.TryAddUnlock):
//      Player.StartInteraction(tuer, UnlockResult, ...)
//        -> MovementContext.StartInteraction: InteractionParameters
//           holen (Door.GetInteractionParameters: an einer
//           verriegelten Tuer AnimationId = DoorKeyOpenInteraction,
//           Griff fuer "Locked"), LockForInteraction
//        -> DoorInteractState: Hand zum Schloss (LayHand), nach 1 s
//           Schluessel in die Hand (BaseUpdate -> SpawnInHands, dabei
//           klimpert der Schluessel: GUISounds-Klang "pickup" des
//           Schluessels), dann ExecuteInteraction -> Door.Interact
//        -> Door.Interact(Unlock) -> Unlock() -> UnlockCoroutine
//           -> LockHandle.OpenCoroutine(): DER Schloss-Klang. Der
//              Schliesszylinder (DoorHandle "LockHandle" der Tuer)
//              dreht sich und spielt DownSound (sofort) und UpSound
//              (nach halber OpenAnimation), beide ueber BetterAudio,
//              Rolloff 14, Lautstaerke 0.8, am Zylinder.
//      Der Klang haengt also an der TUER (ihrem LockHandle), nicht am
//      Schluessel und nicht am UnlockResult. Vom Schluessel kommt nur
//      das Klimpern beim In-die-Hand-Nehmen.
//
//  WAS WIR DARAUS MACHEN: Abschliessen und Aufschliessen laufen
//  genauso ueber Player.StartInteraction - mit eigenen Ergebnis-
//  Typen, damit wir sie unterwegs wiedererkennen:
//   - AbschliessErgebnis (EInteractionType.Lock). Door.Interact
//     kennt Lock nicht (der Schalter dort hat nur Open, Close,
//     Unlock, Breach - die Basisklasse haette Lock). Wir ergaenzen
//     genau diesen Fall: Zylinder drehen wie beim Aufschliessen
//     (LockHandle.OpenCoroutine, gleicher Klang), dann Lock().
//   - AufschliessErgebnis (ein UnlockResult ohne Schluessel, wie es
//     das Spiel selbst in WorldInteractiveObject.Interact(Unlock)
//     baut). Door.Interact(Unlock) laeuft unveraendert -> Unlock().
//  Dazu dieselbe Hand-Animation: beim Start einer Abschliess-
//  Bedienung liefert Door.GetInteractionParameters die Werte, die
//  das Spiel an derselben Tuer im Zustand Locked liefern wuerde
//  (Schluessel-Animation, Schloss-Griff). Und mit Schluessel kommt
//  er nach 1 s in die Hand wie beim Aufschliessen.
//
//  TUEREN OHNE ZYLINDER (LockHandle fehlt oder ist stumm - so gut
//  wie alle Tueren ohne Schloss): da klingt auch das Spiel beim
//  Aufschliessen nach nichts. Damit es trotzdem wie Aufschliessen
//  klingt, spielen wir die Klaenge eines echten Zylinders aus
//  derselben Karte ab (der haeufigste), mit denselben Werten wie
//  DoorHandle.OpenCoroutine und am Tuergriff.
// ---------------------------------------------------------------
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.Interactive;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace TuerAbschliessen
{
	// Abschliessen ueber den Bedienweg. Key darf null sein (Tuer ohne
	// Schloss, oder "Schluessel noetig" aus) - dann bleibt die Hand leer.
	internal sealed class AbschliessErgebnis : InteractionResult
	{
		public readonly KeyComponent Key;

		public AbschliessErgebnis(KeyComponent key)
			: base(EInteractionType.Lock)
		{
			Key = key;
		}
	}

	// Aufschliessen ohne Schluessel. Muss ein UnlockResult sein: Door.Interact
	// castet bei Unlock darauf (Succeed).
	internal sealed class AufschliessErgebnis : UnlockResult
	{
		public AufschliessErgebnis()
			: base(null, null, succeed: true)
		{
		}
	}

	internal static class SchlossBedienung
	{
		// Die Tuer, fuer die gerade eine Abschliess-Bedienung startet.
		// Nur waehrend des Aufrufs von Player.StartInteraction gesetzt.
		internal static Door Startet;

		internal static void Abschliessen(Player spieler, Door tuer, KeyComponent schluessel)
		{
			Startet = tuer;
			try
			{
				spieler.StartInteraction(tuer, new AbschliessErgebnis(schluessel), null);
			}
			finally
			{
				Startet = null;
			}
		}

		internal static void Aufschliessen(Player spieler, Door tuer)
		{
			// Die Freigabe fuer unsere Sperre gibt der Prefix auf
			// MovementContext.StartInteraction (nur IsYourPlayer).
			spieler.StartInteraction(tuer, new AufschliessErgebnis(), null);
		}

		// Wird von Door.Interact aus gestartet, sobald die Hand am Schloss
		// ist (gleicher Zeitpunkt wie Unlock()).
		internal static IEnumerator Abschliessvorgang(Door tuer)
		{
			if (HatSchlossKlang(tuer))
			{
				// Genau das, was UnlockCoroutine vor dem Zustandswechsel tut.
				yield return tuer.LockHandle.OpenCoroutine();
			}
			else
			{
				Ersatzklang(tuer);
			}

			// In der Zwischenzeit von jemand anderem geoeffnet? Dann nicht
			// mehr verriegeln. Interacting = noch unsere Bedienung.
			EDoorState zustand = tuer.DoorState;
			if (zustand != EDoorState.Interacting && zustand != EDoorState.Shut)
			{
				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo("Abschliessen abgebrochen: " + tuer.Id + " ist inzwischen " + zustand);
				}
				yield break;
			}

			tuer.Lock();

			if (tuer.DoorState == EDoorState.Locked)
			{
				Abgeschlossen.Merken(tuer);
			}

			if (Plugin.Protokoll.Value)
			{
				Plugin.Log.LogInfo("Abgeschlossen: " + tuer.Id + " ("
					+ (string.IsNullOrEmpty(tuer.KeyId) ? "ohne Schloss" : "Schluessel " + tuer.KeyId)
					+ ", Klang " + (HatSchlossKlang(tuer) ? "Zylinder" : "Ersatz") + ")");
			}
		}

		// Hat die Tuer einen Zylinder, der beim Aufschliessen klingt?
		internal static bool HatSchlossKlang(WorldInteractiveObject tuer)
		{
			DoorHandle zylinder = tuer.LockHandle;
			return zylinder != null
				&& ((zylinder.DownSound != null && zylinder.DownSound.Length != 0)
					|| (zylinder.UpSound != null && zylinder.UpSound.Length != 0));
		}

		// -----------------------------------------------------------
		//  Ersatzklang fuer Tueren ohne (klingenden) Zylinder
		// -----------------------------------------------------------

		private static object _welt;

		private static DoorHandle _vorlage;

		private static bool _vorlageGesucht;

		internal static void Ersatzklang(Door tuer)
		{
			DoorHandle vorlage = Vorlage();
			if (vorlage == null || !MonoBehaviourSingleton<BetterAudio>.Instantiated)
			{
				return;
			}
			// Der Zylinder saesse am Griff; ohne Griff an der Klangstelle
			// der Tuer (dort spielt sie auch Oeffnen/Schliessen).
			Vector3 ort = tuer._handle != null ? tuer._handle.transform.position : tuer.GetSoundPos();
			tuer.StartCoroutine(ErsatzklangSpielen(tuer, vorlage, ort));
		}

		// Dieselben Klaenge, Gruppen, Rolloffs, Lautstaerken und derselbe
		// Zeitpunkt wie DoorHandle.OpenCoroutine - nur ohne Drehung.
		private static IEnumerator ErsatzklangSpielen(Door tuer, DoorHandle vorlage, Vector3 ort)
		{
			Spielen(tuer, vorlage.DownSound, ort, BetterAudio.AudioSourceGroupType.Environment);
			AnimationCurve kurve = vorlage.OpenAnimation;
			if (vorlage.UpSound == null || vorlage.UpSound.Length == 0 || kurve == null || kurve.length == 0)
			{
				yield break;
			}
			yield return new WaitForSeconds(kurve[kurve.length - 1].time / 2f);
			if (tuer != null)
			{
				Spielen(tuer, vorlage.UpSound, ort, BetterAudio.AudioSourceGroupType.InteractiveObjects);
			}
		}

		private static void Spielen(Door tuer, AudioClip[] klaenge, Vector3 ort, BetterAudio.AudioSourceGroupType gruppe)
		{
			if (klaenge == null || klaenge.Length == 0)
			{
				return;
			}
			AudioClip klang = klaenge[UnityEngine.Random.Range(0, klaenge.Length)];
			if (klang == null)
			{
				return;
			}
			BetterSource quelle = MonoBehaviourSingleton<BetterAudio>.Instance.PlayAtPoint(ort, klang,
				CameraManager.Instance.Distance(ort), gruppe, 14, 0.8f);
			if (quelle != null)
			{
				// Das haengt das Spiel bei echten Zylindern ueber
				// OnPlaySound an (WorldInteractiveObject.OnAwake).
				tuer.ProcessOcclusion(quelle, ort);
			}
		}

		// Der haeufigste klingende Zylinder der Karte - einmal je Raid.
		private static DoorHandle Vorlage()
		{
			object welt = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
			if (!ReferenceEquals(welt, _welt))
			{
				_welt = welt;
				_vorlage = null;
				_vorlageGesucht = false;
			}
			if (_vorlageGesucht)
			{
				return _vorlage;
			}
			_vorlageGesucht = true;
			try
			{
				Dictionary<AudioClip, int> zaehler = new Dictionary<AudioClip, int>();
				Dictionary<AudioClip, DoorHandle> beispiel = new Dictionary<AudioClip, DoorHandle>();
				foreach (Door tuer in UnityEngine.Object.FindObjectsOfType<Door>(true))
				{
					if (tuer is KeycardDoor || !HatSchlossKlang(tuer))
					{
						continue;
					}
					DoorHandle zylinder = tuer.LockHandle;
					AudioClip[] erste = zylinder.DownSound != null && zylinder.DownSound.Length != 0 ? zylinder.DownSound : zylinder.UpSound;
					AudioClip schluessel = erste[0];
					if (schluessel == null)
					{
						continue;
					}
					zaehler.TryGetValue(schluessel, out int n);
					zaehler[schluessel] = n + 1;
					if (!beispiel.ContainsKey(schluessel))
					{
						beispiel[schluessel] = zylinder;
					}
				}
				int bester = 0;
				foreach (KeyValuePair<AudioClip, int> paar in zaehler)
				{
					if (paar.Value > bester)
					{
						bester = paar.Value;
						_vorlage = beispiel[paar.Key];
					}
				}
				if (Plugin.Protokoll.Value)
				{
					Plugin.Log.LogInfo(_vorlage == null
						? "Ersatzklang: kein klingender Schliesszylinder auf dieser Karte."
						: "Ersatzklang: Zylinder von " + (_vorlage.transform.parent != null ? _vorlage.transform.parent.name : _vorlage.name)
							+ " (" + bester + " Tueren klingen so).");
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Ersatzklang suchen - " + ex);
			}
			return _vorlage;
		}
	}

	// ---------------------------------------------------------------
	//  Die Patches dazu
	// ---------------------------------------------------------------

	// 1) Door.Interact: Lock ergaenzen (fehlt in Door, die Basisklasse
	//    haette es), und beim Aufschliessen ohne Zylinder den Ersatzklang.
	[HarmonyPatch(typeof(Door), nameof(Door.Interact), new Type[] { typeof(InteractionResult) })]
	internal static class BedienungAusfuehren
	{
		[HarmonyPrefix]
		internal static bool Vorher(Door __instance, InteractionResult interactionResult)
		{
			try
			{
				if (interactionResult is AbschliessErgebnis)
				{
					__instance.StartCoroutine(SchlossBedienung.Abschliessvorgang(__instance));
					return false;
				}
				if (interactionResult is AufschliessErgebnis && !SchlossBedienung.HatSchlossKlang(__instance))
				{
					SchlossBedienung.Ersatzklang(__instance);
				}
			}
			catch (Exception ex)
			{
				Plugin.Log.LogError("Tuer abschliessen: Door.Interact - " + ex);
			}
			return true;
		}
	}

	// 2) Hand-Animation: beim Start einer Abschliess-Bedienung die Werte,
	//    die Door.GetInteractionParameters an derselben Tuer im Zustand
	//    Locked liefert (Schluessel-Animation, Schloss-Griff, Snap).
	//    Position und Blickziel sind fuer Shut und Locked ohnehin gleich.
	[HarmonyPatch(typeof(Door), nameof(Door.GetInteractionParameters))]
	internal static class SchlossAnimation
	{
		[HarmonyPostfix]
		internal static void Nachher(Door __instance, Vector3 yourPosition, ref WorldInteractiveObject.InteractionParameters __result)
		{
			if (!ReferenceEquals(__instance, SchlossBedienung.Startet))
			{
				return;
			}
			__result.AnimationId = (int)__instance.DoorKeyOpenInteraction;
			__result.Grip = GriffFuerSchloss(__instance, yourPosition);
			__result.Snap = (__instance.Snap & EDoorState.Locked) != 0;
		}

		// WorldInteractiveObject.GetClosestGrip, nur mit Zustand Locked
		// statt des aktuellen.
		private static GripPose GriffFuerSchloss(Door tuer, Vector3 position)
		{
			if (tuer.LockHandle != null)
			{
				tuer.LockHandle.DefPos();
			}
			GripPose[] griffe = tuer.Grips;
			if (griffe == null)
			{
				return null;
			}
			float naechster = float.MaxValue;
			GripPose ergebnis = null;
			for (int i = 0; i < griffe.Length; i++)
			{
				if (griffe[i] == null || (griffe[i].DoorState & EDoorState.Locked) == EDoorState.None)
				{
					continue;
				}
				float abstand = (griffe[i].transform.position - position).sqrMagnitude;
				if (!(abstand > naechster))
				{
					naechster = abstand;
					ergebnis = griffe[i];
				}
			}
			return ergebnis;
		}
	}

	// 3) Schluessel in der Hand. Das Spiel nimmt ihn in DoorInteractState
	//    .BaseUpdate nach Key_Spawn_Timing (1 s) in die Hand - aber nur bei
	//    InteractionType Unlock, und dann ungeprueft ueber
	//    ((UnlockResult)Result).Key.Item.
	//    - Abschliessen mit Schluessel: denselben Schritt fuer uns.
	//    - Aufschliessen ohne Schluessel: Key ist null, der Spielcode liefe
	//      jeden Frame in eine NullReferenceException. _spawned = true
	//      ueberspringt den Schritt (ClearHands am Ende prueft auf null).
	[HarmonyPatch(typeof(DoorInteractState), nameof(DoorInteractState.BaseUpdate))]
	internal static class SchluesselInDerHand
	{
		[HarmonyPrefix]
		internal static void Vorher(DoorInteractState __instance)
		{
			if (__instance._spawned)
			{
				return;
			}
			InteractionResult ergebnis = __instance.MovementContext.InteractionInfo.Result;
			if (ergebnis is AufschliessErgebnis)
			{
				__instance._spawned = true;
				return;
			}
			if (ergebnis is AbschliessErgebnis abschliessen && abschliessen.Key != null
				&& Time.realtimeSinceStartup - __instance._enterTime > DoorInteractState.Key_Spawn_Timing)
			{
				__instance.MovementContext.SpawnKeyInHands(abschliessen.Key.Item, "Base HumanLDigit21");
				__instance._spawned = true;
			}
		}
	}
}
