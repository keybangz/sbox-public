using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Sandbox.Twitch
{
	internal class TwitchClient
	{
		internal const string EndpointURL = "wss://irc-ws.chat.twitch.tv:443";

		private Engine.WebSocket _webSocket;
		internal string Username;
		internal string UserId;
		private bool _reconnect;
		private List<string> _channels = new();
		private bool fullyConnected;

		public async Task<bool> Connect()
		{
			if ( _webSocket != null )
				return false;

			Username = Engine.Streamer.Username;
			UserId = Engine.Streamer.UserId;

			_webSocket = new();
			_webSocket.OnDisconnected += WebSocket_OnDisconnected;
			_webSocket.OnMessageReceived += WebSocket_OnMessageReceived;

			try
			{
				// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
				await _webSocket.Connect( EndpointURL ).ConfigureAwait( false );

				await _webSocket.Send( "CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership" ).ConfigureAwait( false );
				await _webSocket.Send( $"PASS oauth:{Engine.Streamer.Token}" ).ConfigureAwait( false );
				await _webSocket.Send( $"NICK {Username}" ).ConfigureAwait( false );

				while ( !fullyConnected )
				{
					// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
					await Task.Delay( 100 ).ConfigureAwait( false );

					if ( _webSocket == null )
						return false;
				}

				return true;
			}
			catch ( Exception e )
			{
				_webSocket = null;

				Log.Warning( e, $"Failed to connect to {EndpointURL}, trying again in 1 second" );

				// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
				await Task.Delay( 1000 ).ConfigureAwait( false );
				return await Connect().ConfigureAwait( false );
			}
		}

		public void Disconnect()
		{
			Disconnect( false );
		}

		internal void Disconnect( bool reconnect )
		{
			if ( _webSocket == null )
				return;

			_reconnect = reconnect;
			Username = null;

			_webSocket.Dispose();
			_webSocket = null;

			_channels.Clear();
		}

		public async void SendMessage( string message )
		{
			if ( _webSocket == null )
				return;

			await _webSocket.Send( $"PRIVMSG #{Username} :{message}" );
		}

		public void ClearChat()
		{
			SendCommand( "clear" );
		}

		public void BanUser( string username, string reason )
		{
			SendCommand( $"ban {username} {reason}" );
		}

		public void UnbanUser( string username )
		{
			SendCommand( $"unban {username}" );
		}

		public void TimeoutUser( string username, int duration, string reason )
		{
			SendCommand( $"timeout {username} {duration} {reason}" );
		}

		private async void SendCommand( string command )
		{
			if ( _webSocket == null )
				return;

			await _webSocket.Send( $"PRIVMSG #{Username} :/{command}" );
		}

		private void IRC_OnConnected()
		{
			_reconnect = true;
			fullyConnected = true;
			JoinChannel( Username );
		}

		public async void JoinChannel( string channel )
		{
			if ( _webSocket == null )
				return;

			if ( string.IsNullOrEmpty( channel ) )
				return;

			await _webSocket.Send( $"JOIN #{channel.ToLower()}" );
		}

		public async void LeaveChannel( string channel )
		{
			if ( _webSocket == null )
				return;

			if ( string.IsNullOrEmpty( channel ) )
				return;

			await _webSocket.Send( $"PART #{channel.ToLower()}" );
		}

		static StreamChatMessage ParseChatMessage( IRCMessage ircMessage )
		{
			var message = new StreamChatMessage
			{
				Channel = ircMessage.Channel,
				Message = ircMessage.Message,
				Username = ircMessage.User,
				DisplayName = null,
				Color = null,
				Badges = null
			};

			foreach ( var tag in ircMessage.Tags )
			{
				switch ( tag.Key )
				{
					case "display-name":
						message.DisplayName = tag.Value;
						break;
					case "color":
						message.Color = tag.Value;
						break;
					case "badges":
						message.Badges = string.IsNullOrEmpty( tag.Value ) ? null : tag.Value.Split( ',' );
						break;
				}
			}

			if ( string.IsNullOrEmpty( message.DisplayName ) )
				message.DisplayName = message.Username;

			return message;
		}


		private void IRC_OnMessage( StreamChatMessage message )
		{
			if ( string.IsNullOrWhiteSpace( message.Message ) )
				return;

			Engine.Streamer.RunEvent( "stream.message", message );
		}

		private async void IRC_OnPing()
		{
			if ( _webSocket == null )
				return;

			await _webSocket.Send( "PONG" );
		}

		private void IRC_OnJoin( IRCMessage message )
		{
			if ( message.User == Username )
			{
				_channels.Add( message.Channel );
			}

			Engine.Streamer.RunEvent( "stream.join", message.User );
		}

		private void IRC_OnPart( IRCMessage message )
		{
			if ( message.User == Username )
			{
				_channels.Remove( message.Channel );
			}

			Engine.Streamer.RunEvent( "stream.leave", message.User );
		}

		private void HandleIRCMessage( IRCMessage message )
		{
			if ( message.Message.Contains( "Login authentication failed" ) )
			{
				Disconnect( false );
				return;
			}

			switch ( message.Command )
			{
				case IRCCommand.PrivMsg:
					IRC_OnMessage( ParseChatMessage( message ) );
					break;
				case IRCCommand.Ping:
					IRC_OnPing();
					break;
				case IRCCommand.Join:
					IRC_OnJoin( message );
					break;
				case IRCCommand.Part:
					IRC_OnPart( message );
					break;
				case IRCCommand.RPL_004:
					IRC_OnConnected();
					break;
				case IRCCommand.Unknown:
				default:
					break;
			}
		}

		private void WebSocket_OnMessageReceived( string message )
		{
			var stringSeparators = new[] { "\r\n" };
			var lines = message.Split( stringSeparators, StringSplitOptions.None );

			foreach ( var line in lines )
			{
				if ( line.Length <= 1 )
					continue;

				HandleIRCMessage( IRCParser.Parse( line ) );
			}
		}

		private void WebSocket_OnDisconnected( int status, string reason )
		{
			if ( _webSocket == null )
				return;

			_webSocket = null;
			_channels.Clear();

			if ( reason == "Disposing" )
			{
				_reconnect = false;
			}

			if ( _reconnect )
			{
				_ = Connect();
			}
		}

	}
}
