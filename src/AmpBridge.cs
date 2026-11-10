// AmpBridge.cs - glue between Ultima Memento and the CubeCoders AMP panel.
//
// Installed to World/Info/Scripts/AMP/ by the AMP template every time the server starts
// (only rewritten when it changes). Do not edit it there; your changes would be replaced.
//
// What it does:
//  * Reads commands that AMP sends to the console (save, shutdown, broadcast, online, help)
//    and runs them on the game thread. AMP stops the server with "shutdown", which saves
//    the world first. Not used when S_EnableConsole is on: the built-in console then
//    handles the same "save" and "shutdown" commands.
//  * Prints "[AMP] ..." lines that AMP uses to detect when the server is ready and when
//    players join or leave.
//  * Sets the owner account password chosen in AMP (if any).
//  * After a crash, exits instead of starting a second copy of the server that AMP cannot
//    see or stop. CrashGuard still writes its crash report and backup; AMP restarts the
//    server if its "restart on crash" option is on.
//  * Replaces [Restart with a message: a restarted process would run outside AMP.
//
// IMPORTANT: this file is embedded in an AMP CreateFile stage. AMP replaces double curly
// brace placeholders in that text, so it must never contain two opening or two closing
// curly braces next to each other. tools/build.py checks this. Keep it C# 5 compatible.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Server;
using Server.Accounting;
using Server.Commands;
using Server.Network;

namespace Server.Misc
{
	public static class AmpBridgeAccounts
	{
		// Runs before the other Initialize methods (accounts are loaded by then), so the
		// owner account exists before anything checks for it.
		[CallPriority( -1000000 )]
		public static void Initialize()
		{
			try
			{
				AmpBridge.ApplyOwnerAccount();
			}
			catch ( Exception ex )
			{
				Console.WriteLine( "[AMP] Could not apply the owner account settings: {0}", ex.Message );
			}
		}
	}

	public static class AmpBridge
	{
		private static bool m_ShuttingDown;

		// Runs after every other Initialize method so the [Restart registration wins.
		[CallPriority( 1000000 )]
		public static void Initialize()
		{
			EventSink.ServerStarted += new ServerStartedEventHandler( OnServerStarted );
			EventSink.Login += new LoginEventHandler( OnLogin );
			EventSink.Logout += new LogoutEventHandler( OnLogout );
			EventSink.Crashed += new CrashedEventHandler( OnCrashed );

			DisableDetachedCrashRestart();

			CommandSystem.Register( "Restart", AccessLevel.Administrator, new CommandEventHandler( Restart_OnCommand ) );

			if ( !MySettings.S_EnableConsole )
			{
				Thread reader = new Thread( new ThreadStart( ReadConsole ) );
				reader.IsBackground = true;
				reader.Name = "AMP console reader";
				reader.Start();
			}
		}

		// ---------------------------------------------------------------- owner account

		public static void ApplyOwnerAccount()
		{
			string path = Path.Combine( Path.Combine( Path.Combine( Core.BaseDirectory, ".." ), "amp" ), "runtime.kvp" );

			if ( !File.Exists( path ) )
				return;

			string user = "Admin";
			string pass = "";

			foreach ( string raw in File.ReadAllLines( path ) )
			{
				string line = raw.TrimEnd( '\r' );
				int eq = line.IndexOf( '=' );

				if ( eq <= 0 )
					continue;

				string key = line.Substring( 0, eq ).Trim();
				string value = line.Substring( eq + 1 );

				if ( key == "OwnerUsername" && value.Trim().Length > 0 )
					user = value.Trim();
				else if ( key == "OwnerPassword" )
					pass = value;
			}

			try
			{
				File.Delete( path ); // do not leave the password lying around on disk
			}
			catch
			{
			}

			if ( pass.Length == 0 || pass.StartsWith( "{" + "{" ) )
			{
				Account existing = Accounts.GetAccount( user ) as Account;

				if ( existing != null && existing.CheckPassword( "password" ) )
				{
					Console.WriteLine( "[AMP] WARNING: account '{0}' still has the default password 'password'.", user );
					Console.WriteLine( "[AMP] WARNING: set 'Owner account password' in AMP before other people can connect." );
				}

				return;
			}

			Account acct = Accounts.GetAccount( user ) as Account;

			if ( acct == null )
			{
				acct = new Account( user, pass );
				acct.AccessLevel = AccessLevel.Owner;
				Console.WriteLine( "[AMP] Created owner account '{0}'.", user );
				return;
			}

			if ( !acct.CheckPassword( pass ) )
			{
				acct.SetPassword( pass );
				Console.WriteLine( "[AMP] Updated the password of account '{0}'.", user );
			}

			if ( acct.AccessLevel < AccessLevel.Owner )
			{
				acct.AccessLevel = AccessLevel.Owner;
				Console.WriteLine( "[AMP] Account '{0}' now has Owner access.", user );
			}
		}

		// ---------------------------------------------------------------- status lines for AMP

		private static void OnServerStarted()
		{
			Console.WriteLine( "[AMP] Server started. Listening on port {0}.", MySettings.S_Port );
		}

		private static void OnLogin( LoginEventArgs e )
		{
			Mobile m = e.Mobile;

			if ( m == null || m.Player == false )
				return;

			Console.WriteLine( "[AMP] Player joined: {0} ({1})", m.Name, DescribeAccount( m ) );
		}

		private static void OnLogout( LogoutEventArgs e )
		{
			Mobile m = e.Mobile;

			if ( m == null || m.Player == false )
				return;

			Console.WriteLine( "[AMP] Player left: {0} ({1})", m.Name, DescribeAccount( m ) );
		}

		private static string DescribeAccount( Mobile m )
		{
			string account = m.Account == null ? "?" : m.Account.Username;
			return "account " + account + ", serial 0x" + m.Serial.Value.ToString( "X" );
		}

		// ---------------------------------------------------------------- crashes and restarts

		private static void DisableDetachedCrashRestart()
		{
			try
			{
				Type crashGuard = Type.GetType( "Server.Misc.CrashGuard" );

				if ( crashGuard == null )
					return;

				FieldInfo restart = crashGuard.GetField( "RestartServer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public );

				if ( restart != null && restart.FieldType == typeof( bool ) && !restart.IsLiteral && !restart.IsInitOnly )
					restart.SetValue( null, false );
			}
			catch
			{
			}
		}

		private static void OnCrashed( CrashedEventArgs e )
		{
			e.Close = true; // exit instead of waiting for a key press
			Console.WriteLine( "[AMP] The server crashed and will now exit. The report is in World/Crash <date>.log and a copy of the saves is in World/Backups/Crashed." );
		}

		private static void Restart_OnCommand( CommandEventArgs e )
		{
			e.Mobile.SendMessage( "This server is managed by AMP. Use the Restart button in the AMP panel instead." );
		}

		// ---------------------------------------------------------------- console commands

		private static void ReadConsole()
		{
			while ( true )
			{
				string line;

				try
				{
					line = Console.ReadLine();
				}
				catch
				{
					return;
				}

				if ( line == null )
					return; // stdin closed

				line = line.Trim();

				if ( line.Length > 0 )
					Timer.DelayCall( TimeSpan.Zero, new TimerStateCallback( Execute ), line );
			}
		}

		private static void Execute( object state )
		{
			string line = (string)state;
			string command = line;
			string rest = "";
			int space = line.IndexOf( ' ' );

			if ( space > 0 )
			{
				command = line.Substring( 0, space );
				rest = line.Substring( space + 1 ).Trim();
			}

			try
			{
				switch ( command.ToLowerInvariant() )
				{
					case "save":
						Console.WriteLine( "[AMP] Saving the world..." );
						AutoSave.Save();
						break;

					case "shutdown":
					case "stop":
						Shutdown( rest.ToLowerInvariant() != "nosave" );
						break;

					case "broadcast":
					case "bc":
						if ( rest.Length == 0 )
						{
							Console.WriteLine( "[AMP] Usage: broadcast <message>" );
							break;
						}

						World.Broadcast( 0x35, true, rest );
						Console.WriteLine( "[AMP] Broadcast sent." );
						break;

					case "online":
					case "players":
						ListOnline();
						break;

					case "help":
					case "?":
						Console.WriteLine( "[AMP] Console commands:" );
						Console.WriteLine( "[AMP]   save                save the world now" );
						Console.WriteLine( "[AMP]   shutdown            save the world, then stop the server" );
						Console.WriteLine( "[AMP]   shutdown nosave     stop the server without saving" );
						Console.WriteLine( "[AMP]   broadcast <text>    message every online player" );
						Console.WriteLine( "[AMP]   online              list online players" );
						break;

					default:
						Console.WriteLine( "[AMP] Unknown command '{0}'. Type 'help' for the list.", command );
						break;
				}
			}
			catch ( Exception ex )
			{
				Console.WriteLine( "[AMP] Command '{0}' failed: {1}", command, ex.Message );
			}
		}

		private static void Shutdown( bool save )
		{
			if ( m_ShuttingDown )
				return;

			m_ShuttingDown = true;

			if ( save )
			{
				Console.WriteLine( "[AMP] Saving the world before shutting down..." );
				World.Broadcast( 0x35, true, "The server is shutting down. The world is being saved." );
				AutoSave.Save();
			}

			Console.WriteLine( "[AMP] Shutting down." );
			Core.Kill( false );
		}

		private static void ListOnline()
		{
			List<NetState> states = NetState.Instances;
			int count = 0;

			for ( int i = 0; i < states.Count; i++ )
			{
				Mobile m = states[i].Mobile;

				if ( m == null )
					continue;

				count++;
				Console.WriteLine( "[AMP]   {0} ({1}) from {2}", m.Name, DescribeAccount( m ), states[i] );
			}

			Console.WriteLine( "[AMP] {0} player(s) online.", count );
		}
	}
}
