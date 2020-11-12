using System.Collections.Generic;
using Bolt.Matchmaking;
using Photon.Realtime;
using Photon.Voice.Unity;
using UdpKit;
using UnityEngine;

namespace Bolt.Samples.Voice
{
	/// <summary>
	/// Class responsible for managing all the Connection with the Voice Rooms and also the speakers for each player
	///
	/// It will create a new custom speaker when a new player enters the game, making it available for later use.
	/// </summary>
	[RequireComponent(typeof(VoiceConnection), typeof(Recorder))]
	public class BoltVoiceBridge : Bolt.GlobalEventListener, IConnectionCallbacks, IMatchmakingCallbacks
	{
		// ------------ UNITY MEMBERS ---------------------------------------------------------------------------------------

		/// <summary>
		/// Custom Speaker for Bolt
		/// </summary>
		[SerializeField] private GameObject BoltSpeakerPrefab;

		// ------------ PUBLIC MEMBERS --------------------------------------------------------------------------------------

		/// <summary>
		/// Singleton Instance
		/// </summary>
		public static BoltVoiceBridge Instance;

		// ------------ PRIVATE MEMBERS -------------------------------------------------------------------------------------

		/// <summary>
		/// Represents the connection with the voice room
		/// </summary>
		private VoiceConnection voiceConnection;

		/// <summary>
		/// Voice Recorder reference
		/// <see cref="https://doc.photonengine.com/en-us/voice/v2/getting-started/recorder"/>
		/// </summary>
		private Recorder voiceRecorder;

		/// <summary>
		/// Cached Interest Group for the Voice Client
		///
		/// <see cref="https://doc.photonengine.com/en-us/realtime/current/gameplay/interestgroups"/>
		/// </summary>
		private byte voiceRecorderDefaultGroup;

		/// <summary>
		/// Mapping for Spekers and Players
		///
		/// This is used to retrieve the custom Speaker Game Object and parent it to the player
		/// </summary>
		private Dictionary<int, BoltVoiceSpeakerController> speakerRegistry;

		/// <summary>
		/// Builds the Room Name where the Voice Client must enter.
		/// This name is based on the Bolt game session name that the player is already in.
		/// </summary>
		public string RoomName
		{
			get
			{
				var session = BoltMatchmaking.CurrentSession;
				var sessionName = session.HostName;
				return string.Format("{0}_voice", sessionName);
			}
		}

		/// <summary>
		/// Retrieve the current region the Player is already connected with Bolt
		public string Region
		{
			get
			{
				return BoltMatchmaking.CurrentMetadata["Region"] as string;
			}
		}

		/// <summary>
		/// Get the Local Player ID, that is given by the Photon Cloud when it enters the Voice Room
		/// </summary>
		public int LocalPlayerID
		{
			get
			{
				if (IsConnected)
				{
					return this.voiceConnection.Client.LocalPlayer.ActorNumber;
				}

				return -1;
			}
		}

		/// <summary>
		/// Custom Room params used to create or join the Voice Session
		/// </summary>
		private EnterRoomParams EnterRoomParams
		{
			get
			{
				return new EnterRoomParams
				{
					RoomName = RoomName,
					RoomOptions = new RoomOptions()
					{
						IsVisible = false,
						PublishUserId = false,
					}
				};
			}
		}

		/// <summary>
		/// Check if the Voice Client is connected
		/// </summary>
		public bool IsConnected { get { return voiceConnection.Client.IsConnected; } }

		private void Awake()
		{
			// Simple Singleton
			if (Instance == null)
			{
				Instance = this;
			}
			else
			{
				Destroy(this.gameObject);
			}

			// Cache reference
			voiceConnection = GetComponent<VoiceConnection>();
			// Change the default Speaker Factory method for our custom one
			voiceConnection.SpeakerFactory = this.CustomBoltSpeakerFactory;

			// Cache reference
			voiceRecorder = GetComponent<Recorder>();
			// Cache the default Interest Group
			voiceRecorderDefaultGroup = voiceRecorder.InterestGroup;

			// Init the Speaker Registry
			speakerRegistry = new Dictionary<int, BoltVoiceSpeakerController>();
		}

		#region Voice Events

		/// <summary>
		/// This custom implementation of the Speaker Factory is very similar to the orignal,
		/// but besides the Speaker instance creation, also stores the reference on the Speaker Registry
		/// for a later use by the players
		///
		/// <seealso cref="Photon.Voice.Unity.VoiceConnection.SimpleSpeakerFactory"/>
		/// </summary>
		/// <param name="playerId">LoadBalancingClient Player ID</param>
		/// <param name="voiceId">Voice ID</param>
		/// <param name="userData">Custom User Data</param>
		/// <returns>New Speaker instance</returns>
		protected Speaker CustomBoltSpeakerFactory(int playerId, byte voiceId, object userData)
		{
			BoltLog.Info("[BoltVoiceBridge] SpeakerFactory for Player {0}", playerId);

			// Create Instance based on the Custom Speker from Bolt
			var speakerInstance = Instantiate(BoltSpeakerPrefab);

			// Get Speaker referecence
			var speaker = speakerInstance.GetComponent<Speaker>();

			// Store the player ID on our custom BoltVoiceSpeakerController
			var speakerController = speakerInstance.GetComponent<BoltVoiceSpeakerController>();
			speakerController.PlayerID = playerId;

			// Store on the Registry based on the Player ID
			speakerRegistry.Add(playerId, speakerController);

			// Return the Speaker reference
			return speaker;
		}

		#endregion

		#region Bolt Events

		public override void SceneLoadLocalDone(string scene, IProtocolToken token)
		{
			if (BoltNetwork.IsServer || BoltNetwork.IsClient)
			{
				// Registers this class to receive Photon callbacks
				voiceConnection.Client.AddCallbackTarget(this);

				// Auto automatically
				this.ConnectNow();
			}
		}

		public override void BoltShutdownBegin(AddCallback registerDoneCallback, UdpConnectionDisconnectReason disconnectReason)
		{
			if (BoltNetwork.IsServer || BoltNetwork.IsClient)
			{
				// Remove this class from the listeners list
				voiceConnection.Client.RemoveCallbackTarget(this);

				// Disconnect the Voice client when Bolt start to shutdown
				voiceConnection.Client.Disconnect();
			}
		}

		#endregion

		#region Utils

		/// <summary>
		/// Change the Interest Group in which the player will send Voice data and hear from.
		/// This is used to segreate the players based on different "buckets".
		/// </summary>
		/// <param name="groupID">Group ID to enter</param>
		public void ChangeVoiceGround(int groupID)
		{
			if (voiceRecorder != null)
			{
				voiceConnection.Client.OpChangeGroups(
					new byte[0],
					new byte[] { (byte) groupID }
				);

				voiceRecorder.InterestGroup = (byte)groupID;

				BoltLog.Info("[BoltVoiceBridge] InterestGroup changed to {0}", voiceRecorder.InterestGroup);
			}
		}

		/// <summary>
		/// Reset the Interest Group.
		///
		/// <see cref="ChangeVoiceGround(int)"/>
		/// </summary>
		public void ResetVoiceGroup()
		{
			if (voiceRecorder != null)
			{
				voiceConnection.Client.OpChangeGroups(
					new byte[0],
					new byte[] { voiceRecorderDefaultGroup }
				);

				voiceRecorder.InterestGroup = voiceRecorderDefaultGroup;

				BoltLog.Info("[BoltVoiceBridge] InterestGroup changed to {0}", voiceRecorder.InterestGroup);
			}
		}

		/// <summary>
		/// Retrive the Speaker reference based on the Player ID.
		/// </summary>
		/// <param name="playerID">Player ID used to get the Speaker Game Object</param>
		/// <param name="speaker">Speaker Game Object, if found</param>
		/// <returns>True, if there is a Speaker for the specified Player ID, false otherwise</returns>
		public bool GetSpeaker(int playerID, out GameObject speaker)
		{
			BoltVoiceSpeakerController controller;
			var found = speakerRegistry.TryGetValue(playerID, out controller);

			if (found)
			{
				speaker = controller.gameObject;
				return true;
			}

			speaker = null;
			return false;
		}

		/// <summary>
		/// Connect the local peer into the custom Voice Room on the same Region as Bolt is already connected
		/// </summary>
		public void ConnectNow()
		{
			BoltLog.Info("[BoltVoiceBridge] Starting Voice connection...");

			var customSettings = new AppSettings();
			voiceConnection.Settings.CopyTo(customSettings);

			// Connect to the same Region as Bolt is connected
			customSettings.FixedRegion = Region;

			if (voiceConnection.ConnectUsingSettings(customSettings))
			{
				BoltLog.Info("[BoltVoiceBridge] Connecting to Region {0}", customSettings.FixedRegion);
			}
			else
			{
				BoltLog.Error("[BoltVoiceBridge] Not able to connect");
			}
		}

		#endregion

		#region MatchmakingCallbacks

		public void OnCreatedRoom()
		{
			BoltLog.Info("[BoltVoiceBridge] Room Created {0}", voiceConnection.Client.CurrentRoom.Name);
		}

		public void OnCreateRoomFailed(short returnCode, string message)
		{
			BoltLog.Error("[BoltVoiceBridge] OnCreateRoomFailed errorCode={0} errorMessage={1}", returnCode, message);
		}

		public void OnFriendListUpdate(List<FriendInfo> friendList) { }

		public void OnJoinedRoom()
		{
			BoltLog.Info("[BoltVoiceBridge] Joined Room as Player {0}", LocalPlayerID);
			ResetVoiceGroup();
		}

		public void OnJoinRandomFailed(short returnCode, string message)
		{
			BoltLog.Error("[BoltVoiceBridge] OnJoinRandomFailed errorCode={0} errorMessage={1}", returnCode, message);
		}

		public void OnJoinRoomFailed(short returnCode, string message)
		{
			BoltLog.Error("[BoltVoiceBridge] OnJoinRoomFailed roomName={0} errorCode={1} errorMessage={2}", this.RoomName, returnCode, message);
		}

		public void OnLeftRoom()
		{
			BoltLog.Info("[BoltVoiceBridge] Left Room");
		}

		#endregion

		#region ConnectionCallbacks

		public void OnConnected()
		{
			BoltLog.Info("[BoltVoiceBridge] Connected");
		}

		public void OnConnectedToMaster()
		{
			this.voiceConnection.Client.OpJoinOrCreateRoom(this.EnterRoomParams);
		}

		public void OnDisconnected(DisconnectCause cause)
		{
			if (cause == DisconnectCause.None || cause == DisconnectCause.DisconnectByClientLogic)
			{
				return;
			}
			BoltLog.Error("[BoltVoiceBridge] OnDisconnected cause={0}", cause);
		}

		public void OnRegionListReceived(RegionHandler regionHandler) { }

		public void OnCustomAuthenticationResponse(Dictionary<string, object> data) { }

		public void OnCustomAuthenticationFailed(string debugMessage) { }

		#endregion

	}
}
