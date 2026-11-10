// MementoAmp.cs - host-side helper for the CubeCoders AMP "Ultima Memento" template.
//
// AMP writes this file to <instance>/ultima-memento/amp/ and compiles it with csc (Windows)
// or mcs (Linux / Mono) during the update and pre-start stages. Keep it C# 5 compatible.
//
// Commands:
//   prepare   --staging <dir>                                  wipe the download staging folder
//   deploy    --staging <dir> --world <dir> --mode <m> --info <keep|overwrite>
//   prestart  --world <dir> --exe <name> --values <kvp> --defaults <kvp> --bridge <cs> --manage <true|false>
//   overrides --settings <Settings.cs> --values <kvp> [--defaults <kvp>] --out <file>
//
// IMPORTANT: this file is embedded in an AMP CreateFile stage. AMP replaces double curly
// brace placeholders in that text, so this source must never contain two opening or two
// closing curly braces next to each other. tools/build.py checks this.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// Opt in to the .NET 4.6.2+ path handling so that long (\\?\ prefixed) paths work on Windows.
[assembly: System.Runtime.Versioning.TargetFramework( ".NETFramework,Version=v4.6.2" )]

namespace MementoAmp
{
	// Windows' classic 260 character path limit is easy to hit with the deep script folders
	// inside a deep AMP data folder, so every file system call goes through this prefix.
	internal static class LongPath
	{
		private static readonly bool Windows = Path.DirectorySeparatorChar == '\\';

		public static void Enable()
		{
			try
			{
				AppContext.SetSwitch( "Switch.System.IO.UseLegacyPathHandling", false );
				AppContext.SetSwitch( "Switch.System.IO.BlockLongPaths", false );
			}
			catch
			{
			}
		}

		public static string P( string path )
		{
			if ( !Windows || path.StartsWith( @"\\?\" ) )
				return path;

			if ( path.StartsWith( @"\\" ) )
				return @"\\?\UNC\" + path.Substring( 2 );

			return @"\\?\" + path;
		}

		public static string Show( string path )
		{
			if ( path.StartsWith( @"\\?\UNC\" ) )
				return @"\\" + path.Substring( 8 );

			if ( path.StartsWith( @"\\?\" ) )
				return path.Substring( 4 );

			return path;
		}
	}

	internal sealed class FatalException : Exception
	{
		public FatalException( string message ) : base( message )
		{
		}
	}

	internal static class Log
	{
		public static void Info( string message )
		{
			Console.WriteLine( "[MementoAmp] " + message );
		}

		public static void Warn( string message )
		{
			Console.WriteLine( "[MementoAmp] WARNING: " + message );
		}

		public static void Error( string message )
		{
			Console.WriteLine( "[MementoAmp] ERROR: " + message );
			Console.Error.WriteLine( "[MementoAmp] ERROR: " + message );
		}
	}

	internal sealed class Options
	{
		private readonly Dictionary<string, string> m_Values = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

		public static Options Parse( string[] args, int start )
		{
			Options o = new Options();

			for ( int i = start; i < args.Length; i++ )
			{
				string a = args[i];

				if ( !a.StartsWith( "--" ) )
					throw new FatalException( "Unexpected argument '" + a + "'." );

				string key = a.Substring( 2 );
				string value = "true";

				if ( i + 1 < args.Length && !args[i + 1].StartsWith( "--" ) )
				{
					value = args[i + 1];
					i++;
				}

				o.m_Values[key] = value;
			}

			return o;
		}

		public string Get( string key, string fallback )
		{
			string v;

			if ( m_Values.TryGetValue( key, out v ) )
				return v;

			return fallback;
		}

		public string Required( string key )
		{
			string v = Get( key, null );

			if ( string.IsNullOrEmpty( v ) )
				throw new FatalException( "Missing required option --" + key + "." );

			return v;
		}

		public string RequiredPath( string key )
		{
			return LongPath.P( Path.GetFullPath( Required( key ).Trim().TrimEnd( '/', '\\' ) ) );
		}

		public bool Flag( string key, bool fallback )
		{
			string v = Get( key, null );

			if ( v == null )
				return fallback;

			bool b;

			if ( Values.TryParseBool( v, out b ) )
				return b;

			return fallback;
		}
	}

	internal static class Program
	{
		private static int Main( string[] args )
		{
			LongPath.Enable();

			if ( args.Length == 0 )
			{
				PrintUsage();
				return 2;
			}

			try
			{
				Options o = Options.Parse( args, 1 );

				switch ( args[0].ToLowerInvariant() )
				{
					case "prepare":
						Deploy.Prepare( o );
						return 0;
					case "deploy":
						Deploy.Run( o );
						return 0;
					case "prestart":
						PreStart.Run( o );
						return 0;
					case "overrides":
						Overrides.Generate( o.RequiredPath( "settings" ), o.RequiredPath( "values" ), o.Get( "defaults", null ), o.RequiredPath( "out" ) );
						return 0;
					default:
						PrintUsage();
						return 2;
				}
			}
			catch ( FatalException ex )
			{
				Log.Error( ex.Message );
				return 1;
			}
			catch ( Exception ex )
			{
				Log.Error( ex.ToString() );
				return 1;
			}
		}

		private static void PrintUsage()
		{
			Console.WriteLine( "MementoAmp - helper for the AMP Ultima Memento template" );
			Console.WriteLine( "  prepare   --staging <dir>" );
			Console.WriteLine( "  deploy    --staging <dir> --world <dir> --mode <latest|tag|git> --info <keep|overwrite>" );
			Console.WriteLine( "  prestart  --world <dir> --exe <World.exe|WorldLinux.exe> --values <kvp> --defaults <kvp> --bridge <cs> --manage <true|false>" );
			Console.WriteLine( "  overrides --settings <Settings.cs> --values <kvp> [--defaults <kvp>] --out <file>" );
		}
	}

	// ------------------------------------------------------------------------------------------
	// Install / update
	// ------------------------------------------------------------------------------------------

	internal static class Deploy
	{
		public static void Prepare( Options o )
		{
			string staging = o.RequiredPath( "staging" );
			EnsureStagingPath( staging );

			if ( Directory.Exists( staging ) )
				DeleteDirectory( staging );

			Directory.CreateDirectory( Path.Combine( staging, "release" ) );
			Directory.CreateDirectory( Path.Combine( staging, "source" ) );
			Log.Info( "Staging folder ready: " + LongPath.Show( staging ) );
		}

		public static void Run( Options o )
		{
			string staging = o.RequiredPath( "staging" );
			string world = o.RequiredPath( "world" );
			string mode = o.Get( "mode", "latest" ).Trim().ToLowerInvariant();
			string info = o.Get( "info", "keep" ).Trim().ToLowerInvariant();

			EnsureStagingPath( staging );

			if ( info != "keep" && info != "overwrite" )
				throw new FatalException( "--info must be 'keep' or 'overwrite' (got '" + info + "')." );

			string releaseWorld = FindWorld( Path.Combine( staging, "release" ) );

			if ( releaseWorld == null )
				throw new FatalException( "Could not find a 'World' folder with Source/System/Main.cs in the downloaded release. Check the GitHub repository, release tag and release asset name settings." );

			string sourceWorld = null;

			if ( mode == "git" )
			{
				sourceWorld = FindWorld( Path.Combine( staging, "source" ) );

				if ( sourceWorld == null )
					throw new FatalException( "Could not find a 'World' folder with Source/System/Main.cs in the downloaded source archive. Check the branch / tag / commit setting." );
			}

			string codeWorld = sourceWorld != null ? sourceWorld : releaseWorld;
			bool freshInstall = !Directory.Exists( Path.Combine( world, "Saves" ) );

			Log.Info( "Release files: " + LongPath.Show( releaseWorld ) );

			if ( sourceWorld != null )
				Log.Info( "Source code:   " + LongPath.Show( sourceWorld ) );

			Log.Info( "Server folder: " + LongPath.Show( world ) );
			Log.Info( freshInstall ? "No Saves folder yet: this is a fresh install, the starting world will be copied." : "Existing Saves folder found: your world, accounts and backups are kept." );

			Directory.CreateDirectory( world );

			Stats stats = new Stats();

			// 1) Release: data files (maps etc.), starting world, documentation, prebuilt World.exe.
			CopyWorld( releaseWorld, world, freshInstall, info, stats );

			// 2) Optional newer source code on top of it (git mode). Never seeds Saves.
			if ( sourceWorld != null )
				CopyWorld( sourceWorld, world, false, info, stats );

			// 3) Source code is mirrored so that files deleted upstream do not linger and break the script compile.
			MirrorSource( codeWorld, world, stats );

			// 4) The compiled script cache depends on the core and on the scripts; force a rebuild.
			DeleteFileIfExists( Path.Combine( world, Path.Combine( "Data", "Data.bin" ) ) );
			DeleteFileIfExists( Path.Combine( world, Path.Combine( "Data", "Data.hash" ) ) );

			Log.Info( string.Format( "Copied {0} file(s), kept {1} existing customised file(s).", stats.Copied, stats.Kept ) );

			if ( !File.Exists( Path.Combine( world, Path.Combine( "Data", Path.Combine( "Files", "map0.mul" ) ) ) ) )
				Log.Warn( "Data/Files/map0.mul is missing. The server needs the map files that ship in the release zip." );

			WriteMarker( world, mode, releaseWorld, sourceWorld );

			DeleteDirectory( staging );

			if ( freshInstall )
			{
				Log.Info( "Fresh install complete. After the first start: log in as the Admin account and run the [BuildWorld command once." );
				Log.Info( "Set 'Owner account password' in AMP before letting other people connect; the default Admin password is public." );
			}
		}

		private sealed class Stats
		{
			public int Copied;
			public int Kept;
		}

		private static void EnsureStagingPath( string staging )
		{
			// Safety net: never wipe anything that is not our own staging folder.
			if ( !string.Equals( Path.GetFileName( staging ), "staging", StringComparison.OrdinalIgnoreCase ) )
				throw new FatalException( "Refusing to use '" + LongPath.Show( staging ) + "' as the staging folder (its name must be 'staging')." );
		}

		private static string FindWorld( string root )
		{
			if ( !Directory.Exists( root ) )
				return null;

			Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
			queue.Enqueue( new KeyValuePair<string, int>( root, 0 ) );

			while ( queue.Count > 0 )
			{
				KeyValuePair<string, int> item = queue.Dequeue();
				string dir = item.Key;

				if ( string.Equals( Path.GetFileName( dir ), "World", StringComparison.OrdinalIgnoreCase )
					&& File.Exists( Path.Combine( dir, Path.Combine( "Source", Path.Combine( "System", "Main.cs" ) ) ) ) )
					return dir;

				if ( item.Value >= 4 )
					continue;

				foreach ( string sub in Directory.GetDirectories( dir ) )
					queue.Enqueue( new KeyValuePair<string, int>( sub, item.Value + 1 ) );
			}

			return null;
		}

		private static void CopyWorld( string from, string to, bool seedSaves, string infoPolicy, Stats stats )
		{
			foreach ( string dir in Directory.GetDirectories( from ) )
			{
				string name = Path.GetFileName( dir );
				string target = Path.Combine( to, name );

				if ( Is( name, "Source" ) || Is( name, "Backups" ) )
					continue; // Source is mirrored separately; Backups belong to the running server.

				if ( Is( name, "Saves" ) )
				{
					if ( seedSaves && !Directory.Exists( target ) )
						CopyDirectory( dir, target, true, stats );

					continue;
				}

				if ( Is( name, "Info" ) )
				{
					CopyInfo( dir, target, infoPolicy, stats );
					continue;
				}

				CopyDirectory( dir, target, true, stats );
			}

			foreach ( string file in Directory.GetFiles( from ) )
				CopyFile( file, Path.Combine( to, Path.GetFileName( file ) ), true, stats );
		}

		private static void CopyInfo( string from, string to, string policy, Stats stats )
		{
			string scripts = Path.Combine( from, "Scripts" );

			foreach ( string file in Directory.GetFiles( from, "*", SearchOption.AllDirectories ) )
			{
				string rel = file.Substring( from.Length ).TrimStart( Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar );
				string target = Path.Combine( to, rel );
				string name = Path.GetFileName( file );
				bool inScripts = string.Equals( Path.GetDirectoryName( file ), scripts, StringComparison.OrdinalIgnoreCase );

				bool overwrite;

				if ( inScripts && Is( name, "Settings.override.cs" ) )
					overwrite = false; // generated by AMP or edited by you; never replaced by an update
				else if ( inScripts && ( Is( name, "Settings.cs" ) || name.EndsWith( ".csproj", StringComparison.OrdinalIgnoreCase ) ) )
					overwrite = true;  // must always match the server source code
				else
					overwrite = policy == "overwrite";

				CopyFile( file, target, overwrite, stats );
			}
		}

		private static void MirrorSource( string codeWorld, string world, Stats stats )
		{
			string from = Path.Combine( codeWorld, "Source" );
			string to = Path.Combine( world, "Source" );

			if ( !File.Exists( Path.Combine( from, Path.Combine( "System", "Main.cs" ) ) ) )
				throw new FatalException( "The downloaded files have no Source/System/Main.cs; refusing to replace the server source code." );

			if ( Directory.Exists( to ) )
				DeleteDirectory( to );

			CopyDirectory( from, to, true, stats );
		}

		private static void CopyDirectory( string from, string to, bool overwrite, Stats stats )
		{
			Directory.CreateDirectory( to );

			foreach ( string file in Directory.GetFiles( from ) )
				CopyFile( file, Path.Combine( to, Path.GetFileName( file ) ), overwrite, stats );

			foreach ( string dir in Directory.GetDirectories( from ) )
				CopyDirectory( dir, Path.Combine( to, Path.GetFileName( dir ) ), overwrite, stats );
		}

		private static void CopyFile( string from, string to, bool overwrite, Stats stats )
		{
			if ( File.Exists( to ) && !overwrite )
			{
				stats.Kept++;
				return;
			}

			string dir = Path.GetDirectoryName( to );

			if ( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );

			if ( File.Exists( to ) )
				File.SetAttributes( to, FileAttributes.Normal );

			File.Copy( from, to, true );
			stats.Copied++;
		}

		private static void WriteMarker( string world, string mode, string releaseWorld, string sourceWorld )
		{
			try
			{
				string ampDir = Path.Combine( Path.GetDirectoryName( world ), "amp" );
				Directory.CreateDirectory( ampDir );

				StringBuilder sb = new StringBuilder();
				sb.AppendLine( "Installed (UTC): " + DateTime.UtcNow.ToString( "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture ) );
				sb.AppendLine( "Install source:  " + mode );
				sb.AppendLine( "Release folder:  " + Path.GetFileName( Path.GetDirectoryName( releaseWorld ) ) );

				if ( sourceWorld != null )
					sb.AppendLine( "Source folder:   " + Path.GetFileName( Path.GetDirectoryName( sourceWorld ) ) );

				File.WriteAllText( Path.Combine( ampDir, "installed.txt" ), sb.ToString() );
			}
			catch ( Exception ex )
			{
				Log.Warn( "Could not write installed.txt: " + ex.Message );
			}
		}

		private static bool Is( string a, string b )
		{
			return string.Equals( a, b, StringComparison.OrdinalIgnoreCase );
		}

		private static void DeleteFileIfExists( string path )
		{
			if ( File.Exists( path ) )
			{
				File.SetAttributes( path, FileAttributes.Normal );
				File.Delete( path );
			}
		}

		public static void DeleteDirectory( string path )
		{
			if ( !Directory.Exists( path ) )
				return;

			foreach ( string file in Directory.GetFiles( path, "*", SearchOption.AllDirectories ) )
				File.SetAttributes( file, FileAttributes.Normal );

			Directory.Delete( path, true );
		}
	}

	// ------------------------------------------------------------------------------------------
	// Pre-start: verify install, install the in-game AMP bridge, write Settings.override.cs
	// ------------------------------------------------------------------------------------------

	internal static class PreStart
	{
		public static void Run( Options o )
		{
			string world = o.RequiredPath( "world" );
			string exe = o.Required( "exe" );

			if ( !File.Exists( Path.Combine( world, exe ) ) )
				throw new FatalException( "The server is not installed yet (" + LongPath.Show( Path.Combine( world, exe ) ) + " is missing). Click 'Update' in AMP first." );

			string settingsCs = Path.Combine( world, Path.Combine( "Info", Path.Combine( "Scripts", "Settings.cs" ) ) );

			if ( !File.Exists( settingsCs ) )
				throw new FatalException( "Missing " + LongPath.Show( settingsCs ) + ". Click 'Update' in AMP to repair the install." );

			string bridge = o.Get( "bridge", null );

			if ( !string.IsNullOrEmpty( bridge ) )
				InstallBridge( LongPath.P( Path.GetFullPath( bridge ) ), world );

			string target = Path.Combine( world, Path.Combine( "Info", Path.Combine( "Scripts", "Settings.override.cs" ) ) );

			if ( !o.Flag( "manage", true ) )
			{
				Log.Info( "'Manage Settings.override.cs' is off in AMP: leaving " + LongPath.Show( target ) + " untouched." );
				return;
			}

			Overrides.Generate( settingsCs, o.RequiredPath( "values" ), o.Get( "defaults", null ), target );
		}

		private static void InstallBridge( string source, string world )
		{
			if ( !File.Exists( source ) )
			{
				Log.Warn( "AMP bridge source not found at " + LongPath.Show( source ) + "; skipping." );
				return;
			}

			string target = Path.Combine( world, Path.Combine( "Info", Path.Combine( "Scripts", Path.Combine( "AMP", "AmpBridge.cs" ) ) ) );

			if ( TextFile.WriteIfChanged( target, File.ReadAllText( source ) ) )
				Log.Info( "Installed the AMP bridge script: " + LongPath.Show( target ) );
		}
	}

	internal static class TextFile
	{
		private static readonly Encoding Utf8Bom = new UTF8Encoding( true );

		// Rewriting an unchanged script would change its timestamp and force the server to
		// recompile every script on the next start, so only write when the content differs.
		public static bool WriteIfChanged( string path, string content )
		{
			content = content.Replace( "\r\n", "\n" ).Replace( "\n", Environment.NewLine );

			if ( File.Exists( path ) && File.ReadAllText( path ) == content )
				return false;

			string dir = Path.GetDirectoryName( path );

			if ( !string.IsNullOrEmpty( dir ) )
				Directory.CreateDirectory( dir );

			if ( File.Exists( path ) )
				File.SetAttributes( path, FileAttributes.Normal );

			File.WriteAllText( path, content, Utf8Bom );
			return true;
		}
	}

	// ------------------------------------------------------------------------------------------
	// Settings.override.cs generation
	// ------------------------------------------------------------------------------------------

	internal enum FieldType
	{
		Bool,
		Int,
		Double,
		String,
		IntArray
	}

	internal sealed class FieldDef
	{
		public string Name;
		public FieldType Type;
		public string DefaultText;  // raw C# initializer
		public object Default;      // parsed default, or null if it could not be parsed
		public bool DefaultParsed;
	}

	internal static class Overrides
	{
		public static void Generate( string settingsCs, string valuesPath, string defaultsPath, string outPath )
		{
			Dictionary<string, FieldDef> fields = SettingsParser.Parse( File.ReadAllText( settingsCs ) );

			if ( fields.Count == 0 )
				throw new FatalException( "No settings were found in " + LongPath.Show( settingsCs ) + "." );

			if ( !File.Exists( valuesPath ) )
				throw new FatalException( "AMP settings file not found: " + LongPath.Show( valuesPath ) );

			List<KeyValuePair<string, string>> values = Kvp.Read( valuesPath );
			Dictionary<string, string> templateDefaults = new Dictionary<string, string>( StringComparer.Ordinal );

			if ( !string.IsNullOrEmpty( defaultsPath ) && File.Exists( defaultsPath ) )
			{
				foreach ( KeyValuePair<string, string> kv in Kvp.Read( defaultsPath ) )
					templateDefaults[kv.Key] = kv.Value;
			}

			List<string> lines = new List<string>();
			List<string> names = new List<string>();
			List<string> errors = new List<string>();
			string placeholder = "{" + "{";

			foreach ( KeyValuePair<string, string> kv in values )
			{
				FieldDef f;

				if ( !fields.TryGetValue( kv.Key, out f ) )
				{
					Log.Warn( "AMP setting '" + kv.Key + "' does not exist in this version's Settings.cs; ignored." );
					continue;
				}

				if ( kv.Value.Contains( placeholder ) )
				{
					Log.Warn( "AMP did not fill in a value for '" + kv.Key + "'; using the Settings.cs default." );
					continue;
				}

				object value;
				string error;

				if ( !Values.TryParse( f, kv.Value, out value, out error ) )
				{
					errors.Add( kv.Key + ": " + error );
					continue;
				}

				if ( f.DefaultParsed && Values.AreEqual( f.Type, value, f.Default ) )
					continue; // same as the game's default: nothing to override

				string templateText;

				if ( templateDefaults.TryGetValue( kv.Key, out templateText ) )
				{
					object templateValue;
					string ignored;

					if ( Values.TryParse( f, templateText, out templateValue, out ignored ) && Values.AreEqual( f.Type, value, templateValue ) )
					{
						if ( f.DefaultParsed )
							Log.Info( "Upstream changed the default of " + kv.Key + " (AMP template: " + templateText + ", Settings.cs: " + f.DefaultText + "). You never changed it in AMP, so the new default is used." );

						continue; // still the template's default: follow Settings.cs
					}
				}

				lines.Add( "\t\t\tMySettings." + f.Name + " = " + Values.ToLiteral( f.Type, value ) + ";" );
				names.Add( f.Name + " = " + Values.ToLiteral( f.Type, value ) );
			}

			if ( errors.Count > 0 )
				throw new FatalException( "Invalid value(s) in the AMP settings, fix them on the Configuration page:" + Environment.NewLine + "  " + string.Join( Environment.NewLine + "  ", errors.ToArray() ) );

			string content = Render( lines );
			bool changed = TextFile.WriteIfChanged( outPath, content );

			if ( names.Count == 0 )
				Log.Info( "Settings.override.cs: every setting matches Settings.cs, no overrides" + ( changed ? " (file updated)." : " (unchanged)." ) );
			else
				Log.Info( "Settings.override.cs: " + names.Count + " override(s)" + ( changed ? " written" : " unchanged" ) + ": " + string.Join( ", ", names.ToArray() ) );
		}

		private static string Render( List<string> lines )
		{
			StringBuilder sb = new StringBuilder();

			sb.AppendLine( "// ============================================================================" );
			sb.AppendLine( "// GENERATED BY THE CUBECODERS AMP \"ULTIMA MEMENTO\" TEMPLATE - DO NOT EDIT" );
			sb.AppendLine( "//" );
			sb.AppendLine( "// This file is rewritten every time the server is started from AMP. It holds" );
			sb.AppendLine( "// only the settings whose AMP value differs from the default in Settings.cs." );
			sb.AppendLine( "// Change settings on the AMP Configuration page instead of editing this file," );
			sb.AppendLine( "// or turn off 'Manage Settings.override.cs' in AMP to maintain it by hand." );
			sb.AppendLine( "// ============================================================================" );
			sb.AppendLine( "namespace Server" );
			sb.AppendLine( "{" );
			sb.AppendLine( "\tpublic static class SettingOverrides" );
			sb.AppendLine( "\t{" );
			sb.AppendLine( "\t\t// Runs first in the Configure phase, before any script's Initialize, so that" );
			sb.AppendLine( "\t\t// values read early at startup (S_Port, S_Address, ...) also take effect." );
			sb.AppendLine( "\t\t[CallPriority( -1000000 )]" );
			sb.AppendLine( "\t\tpublic static void Configure()" );
			sb.AppendLine( "\t\t{" );

			if ( lines.Count == 0 )
				sb.AppendLine( "\t\t\t// Every setting currently matches its Settings.cs default." );

			foreach ( string line in lines )
				sb.AppendLine( line );

			sb.AppendLine( "\t\t}" );
			sb.AppendLine( "\t}" );
			sb.AppendLine( "}" );

			return sb.ToString();
		}
	}

	internal static class Kvp
	{
		public static List<KeyValuePair<string, string>> Read( string path )
		{
			List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();

			foreach ( string raw in File.ReadAllLines( path ) )
			{
				string line = raw.TrimEnd( '\r' );

				if ( line.Trim().Length == 0 || line.TrimStart().StartsWith( "#" ) )
					continue;

				int eq = line.IndexOf( '=' );

				if ( eq <= 0 )
					continue;

				list.Add( new KeyValuePair<string, string>( line.Substring( 0, eq ).Trim(), line.Substring( eq + 1 ) ) );
			}

			return list;
		}
	}

	internal static class SettingsParser
	{
		private static readonly Regex Declaration = new Regex(
			@"public\s+static\s+(?<type>bool|int|double|string|int\s*\[\s*\])\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>(?:@""(?:[^""]|"""")*""|""(?:[^""\\]|\\.)*""|[^;""])*);",
			RegexOptions.Singleline | RegexOptions.CultureInvariant );

		public static Dictionary<string, FieldDef> Parse( string source )
		{
			string code = StripComments( source );
			Dictionary<string, FieldDef> fields = new Dictionary<string, FieldDef>( StringComparer.Ordinal );

			foreach ( Match m in Declaration.Matches( code ) )
			{
				FieldDef f = new FieldDef();
				f.Name = m.Groups["name"].Value;
				f.Type = ToFieldType( m.Groups["type"].Value );
				f.DefaultText = Regex.Replace( m.Groups["value"].Value.Trim(), @"\s+", " " );

				object parsed;
				f.DefaultParsed = Values.TryParseInitializer( f.Type, f.DefaultText, out parsed );
				f.Default = parsed;

				if ( !f.DefaultParsed )
					Log.Warn( "Could not read the default of " + f.Name + " (" + f.DefaultText + "); it is compared with the AMP template default instead." );

				fields[f.Name] = f;
			}

			return fields;
		}

		private static FieldType ToFieldType( string t )
		{
			t = Regex.Replace( t, @"\s+", "" );

			switch ( t )
			{
				case "bool": return FieldType.Bool;
				case "int": return FieldType.Int;
				case "double": return FieldType.Double;
				case "string": return FieldType.String;
				default: return FieldType.IntArray;
			}
		}

		// Removes // and /* */ comments while leaving string and char literals intact, so that
		// commented-out examples such as "// public static string S_Address = ..." are ignored.
		public static string StripComments( string s )
		{
			StringBuilder sb = new StringBuilder( s.Length );
			int i = 0;
			int n = s.Length;

			while ( i < n )
			{
				char c = s[i];

				if ( c == '/' && i + 1 < n && s[i + 1] == '/' )
				{
					while ( i < n && s[i] != '\n' )
						i++;

					continue;
				}

				if ( c == '/' && i + 1 < n && s[i + 1] == '*' )
				{
					i += 2;

					while ( i + 1 < n && !( s[i] == '*' && s[i + 1] == '/' ) )
						i++;

					i += 2;
					sb.Append( ' ' );
					continue;
				}

				if ( c == '@' && i + 1 < n && s[i + 1] == '"' )
				{
					int start = i;
					i += 2;

					while ( i < n )
					{
						if ( s[i] == '"' )
						{
							if ( i + 1 < n && s[i + 1] == '"' )
							{
								i += 2;
								continue;
							}

							i++;
							break;
						}

						i++;
					}

					sb.Append( s, start, Math.Min( i, n ) - start );
					continue;
				}

				if ( c == '"' || c == '\'' )
				{
					int start = i;
					char quote = c;
					i++;

					while ( i < n && s[i] != quote && s[i] != '\n' )
					{
						if ( s[i] == '\\' )
							i++;

						i++;
					}

					i++;
					sb.Append( s, start, Math.Min( i, n ) - start );
					continue;
				}

				sb.Append( c );
				i++;
			}

			return sb.ToString();
		}
	}

	internal static class Values
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		public static bool TryParseBool( string text, out bool value )
		{
			switch ( text.Trim().ToLowerInvariant() )
			{
				case "true":
				case "1":
				case "yes":
				case "on":
					value = true;
					return true;
				case "false":
				case "0":
				case "no":
				case "off":
					value = false;
					return true;
				default:
					value = false;
					return false;
			}
		}

		// Parses a C# initializer from Settings.cs.
		public static bool TryParseInitializer( FieldType type, string text, out object value )
		{
			value = null;
			string t = text.Trim();

			switch ( type )
			{
				case FieldType.Bool:
					if ( t == "true" || t == "false" )
					{
						value = t == "true";
						return true;
					}

					return false;

				case FieldType.Int:
				{
					int i;

					if ( int.TryParse( t, NumberStyles.AllowLeadingSign, Inv, out i ) )
					{
						value = i;
						return true;
					}

					return false;
				}

				case FieldType.Double:
				{
					string d = t.TrimEnd( 'd', 'D', 'f', 'F', 'm', 'M' );
					double v;

					if ( double.TryParse( d, NumberStyles.Float, Inv, out v ) )
					{
						value = v;
						return true;
					}

					return false;
				}

				case FieldType.String:
					if ( t == "null" )
						return true; // value stays null

					if ( t.StartsWith( "@\"" ) && t.EndsWith( "\"" ) && t.Length >= 3 )
					{
						value = t.Substring( 2, t.Length - 3 ).Replace( "\"\"", "\"" );
						return true;
					}

					if ( t.StartsWith( "\"" ) && t.EndsWith( "\"" ) && t.Length >= 2 )
					{
						string s;

						if ( TryUnescape( t.Substring( 1, t.Length - 2 ), out s ) )
						{
							value = s;
							return true;
						}
					}

					return false;

				case FieldType.IntArray:
				{
					Match empty = Regex.Match( t, @"^new\s+int\s*\[\s*0\s*\]$" );

					if ( empty.Success )
					{
						value = new int[0];
						return true;
					}

					Match m = Regex.Match( t, @"^new\s*(?:int\s*)?\[\s*\]\s*\{(?<items>[^\}]*)\}$" );

					if ( !m.Success )
						return false;

					int[] items;

					if ( TryParseIntList( m.Groups["items"].Value, out items ) )
					{
						value = items;
						return true;
					}

					return false;
				}
			}

			return false;
		}

		// Parses a value typed in the AMP web panel.
		public static bool TryParse( FieldDef f, string raw, out object value, out string error )
		{
			value = null;
			error = null;
			string t = raw.Trim();

			switch ( f.Type )
			{
				case FieldType.Bool:
				{
					bool b;

					if ( TryParseBool( t, out b ) )
					{
						value = b;
						return true;
					}

					error = "'" + raw + "' is not true or false.";
					return false;
				}

				case FieldType.Int:
				{
					double d;

					if ( TryParseNumber( t, out d ) && d == Math.Floor( d ) && d >= int.MinValue && d <= int.MaxValue )
					{
						value = (int)d;
						return true;
					}

					error = "'" + raw + "' is not a whole number.";
					return false;
				}

				case FieldType.Double:
				{
					double d;

					if ( TryParseNumber( t, out d ) )
					{
						value = d;
						return true;
					}

					error = "'" + raw + "' is not a number.";
					return false;
				}

				case FieldType.String:
				{
					// An empty box means "no value" when the game's default is null (for example S_Address).
					if ( raw.Length == 0 && f.DefaultParsed && f.Default == null )
						return true;

					value = raw;
					return true;
				}

				case FieldType.IntArray:
				{
					int[] items;

					if ( TryParseIntList( t, out items ) )
					{
						value = items;
						return true;
					}

					error = "'" + raw + "' is not a comma separated list of whole numbers.";
					return false;
				}
			}

			error = "unsupported setting type.";
			return false;
		}

		private static bool TryParseNumber( string t, out double d )
		{
			if ( t.IndexOf( ',' ) >= 0 && t.IndexOf( '.' ) < 0 )
				t = t.Replace( ',', '.' ); // tolerate a comma decimal separator

			if ( double.TryParse( t, NumberStyles.Float, Inv, out d ) && !double.IsNaN( d ) && !double.IsInfinity( d ) )
				return true;

			return false;
		}

		private static bool TryParseIntList( string text, out int[] items )
		{
			List<int> list = new List<int>();

			foreach ( string part in text.Split( new char[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries ) )
			{
				int i;

				if ( !int.TryParse( part.Trim(), NumberStyles.AllowLeadingSign, Inv, out i ) )
				{
					items = null;
					return false;
				}

				list.Add( i );
			}

			items = list.ToArray();
			return true;
		}

		public static bool AreEqual( FieldType type, object a, object b )
		{
			if ( a == null || b == null )
				return a == null && b == null;

			if ( type == FieldType.IntArray )
			{
				int[] x = (int[])a;
				int[] y = (int[])b;

				if ( x.Length != y.Length )
					return false;

				for ( int i = 0; i < x.Length; i++ )
				{
					if ( x[i] != y[i] )
						return false;
				}

				return true;
			}

			return a.Equals( b );
		}

		public static string ToLiteral( FieldType type, object value )
		{
			switch ( type )
			{
				case FieldType.Bool:
					return (bool)value ? "true" : "false";

				case FieldType.Int:
					return ( (int)value ).ToString( Inv );

				case FieldType.Double:
				{
					string s = ( (double)value ).ToString( "R", Inv );

					if ( s.IndexOf( '.' ) < 0 && s.IndexOf( 'E' ) < 0 && s.IndexOf( 'e' ) < 0 )
						s += ".0";

					return s;
				}

				case FieldType.String:
					return value == null ? "null" : Quote( (string)value );

				case FieldType.IntArray:
				{
					int[] items = (int[])value;

					if ( items.Length == 0 )
						return "new int[0]";

					string[] parts = new string[items.Length];

					for ( int i = 0; i < items.Length; i++ )
						parts[i] = items[i].ToString( Inv );

					return "new int[] { " + string.Join( ", ", parts ) + " }";
				}
			}

			throw new FatalException( "unsupported setting type." );
		}

		// Plain ASCII C# string literal; everything else is \u escaped so the file encoding never matters.
		private static string Quote( string s )
		{
			StringBuilder sb = new StringBuilder( s.Length + 2 );
			sb.Append( '"' );

			foreach ( char c in s )
			{
				switch ( c )
				{
					case '\\': sb.Append( "\\\\" ); break;
					case '"': sb.Append( "\\\"" ); break;
					case '\n': sb.Append( "\\n" ); break;
					case '\r': sb.Append( "\\r" ); break;
					case '\t': sb.Append( "\\t" ); break;
					default:
						if ( c < 32 || c > 126 )
							sb.Append( "\\u" ).Append( ( (int)c ).ToString( "x4", Inv ) );
						else
							sb.Append( c );
						break;
				}
			}

			sb.Append( '"' );
			return sb.ToString();
		}

		private static bool TryUnescape( string s, out string result )
		{
			StringBuilder sb = new StringBuilder( s.Length );

			for ( int i = 0; i < s.Length; i++ )
			{
				char c = s[i];

				if ( c != '\\' )
				{
					sb.Append( c );
					continue;
				}

				if ( ++i >= s.Length )
				{
					result = null;
					return false;
				}

				switch ( s[i] )
				{
					case '\\': sb.Append( '\\' ); break;
					case '"': sb.Append( '"' ); break;
					case '\'': sb.Append( '\'' ); break;
					case 'n': sb.Append( '\n' ); break;
					case 'r': sb.Append( '\r' ); break;
					case 't': sb.Append( '\t' ); break;
					case '0': sb.Append( '\0' ); break;
					case 'u':
						if ( i + 4 < s.Length )
						{
							sb.Append( (char)int.Parse( s.Substring( i + 1, 4 ), NumberStyles.HexNumber, Inv ) );
							i += 4;
							break;
						}

						result = null;
						return false;
					default:
						result = null;
						return false;
				}
			}

			result = sb.ToString();
			return true;
		}
	}
}
