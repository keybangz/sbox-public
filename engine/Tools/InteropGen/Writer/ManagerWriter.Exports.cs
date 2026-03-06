using System.Collections.Generic;
using System.Linq;

namespace Facepunch.InteropGen;

internal partial class ManagerWriter
{
	// Flag to use delegate-based exports instead of UnmanagedCallersOnly
	// This is needed for Linux compatibility
	private bool UseLinuxCompatibleExports => true;

	private void Exports()
	{
		StartBlock( $"internal static unsafe class Exports" );
		{
			if ( UseLinuxCompatibleExports )
			{
				// Store delegates to prevent GC collection
				WriteLine( "// Delegate storage to prevent garbage collection" );
				WriteLine( "private static readonly List<Delegate> _delegateStore = new List<Delegate>();" );
				WriteLine( "private static readonly Dictionary<string, IntPtr> _functionPointers = new Dictionary<string, IntPtr>();" );
				WriteLine();

				// Helper method to get or create function pointer
				StartBlock( "internal static IntPtr GetFunctionPointer<T>( string name, T del ) where T : Delegate" );
				{
					WriteLine( "if ( _functionPointers.TryGetValue( name, out var ptr ) ) return ptr;" );
					WriteLine( "_delegateStore.Add( del );" );
					WriteLine( "ptr = Marshal.GetFunctionPointerForDelegate( del );" );
					WriteLine( "_functionPointers[name] = ptr;" );
					WriteLine( "return ptr;" );
				}
				EndBlock();
				WriteLine();
			}

			foreach ( Class c in definitions.Classes.Where( x => x.Native == false ) )
			{
				if ( ShouldSkip( c ) )
				{
					continue;
				}

				foreach ( Function f in c.Functions )
				{
					ExportFunction( c, f );
				}

				foreach ( Variable f in c.Variables )
				{
					throw new System.NotImplementedException();
				}
			}
		}
		EndBlock();
		WriteLine( "" );
	}

	private void ExportFunction( Class c, Function f )
	{
		IEnumerable<string> nativeArgs = c.SelfArg( false, f.Static ).Concat( f.Parameters ).Select( x => $"{x.GetManagedDelegateType( true )} {x.Name}" );
		string nativeArgS = string.Join( ", ", nativeArgs );

		IEnumerable<string> managedArgs = f.Parameters.Select( x => x.FromInterop( false ) );
		string managedArgsS = string.Join( ", ", managedArgs );

		string namespc = $"{c.ManagedNamespace}.{c.ManagedName}";

		// Always generate delegate type for Linux compatibility
		WriteLine( $"[UnmanagedFunctionPointer( CallingConvention.Cdecl )]" );
		WriteLine( $"internal delegate {f.Return.GetManagedDelegateType( true )} {f.MangledName}_d( {nativeArgS.Trim( ',', ' ' )} );" );
		WriteLine();

		WriteLine( "/// <summary>" );
		WriteLine( $"/// {namespc}.{f.Name}( ... )" );
		WriteLine( "/// </summary>" );

		if ( !UseLinuxCompatibleExports )
		{
			// Original UnmanagedCallersOnly approach (Windows)
			WriteLine( "[UnmanagedCallersOnly( CallConvs = new[] { typeof(CallConvCdecl) } )]" );
		}

		StartBlock( $"internal static {f.Return.GetManagedDelegateType( true )} {f.MangledName}( {nativeArgS.Trim( ',', ' ' )} )" );
			StartBlock( "try" );
			{
				string func = $"{c.ManagedNameWithNamespace}.{f.Name}( {managedArgsS} )";

				if ( !c.Native && !c.Static && !f.Static )
				{
					WriteLine( $"if ( !Sandbox.InteropSystem.TryGetObject<{c.ManagedNameWithNamespace}>( self, out var instance ) )" );
					WriteLine( $"	return{(f.HasReturn ? " default" : "")};" );
					WriteLine();

					func = $"instance.{f.Name}( {managedArgsS} )";
				}

				if ( f.HasReturn )
				{
					func = f.Return.ToInterop( false, func );
					func = f.Return.ReturnWrapCall( func, false );
				}
				else
				{
					func += ";";
				}

				func = AttributeWrapFunction( f, func );

				if ( f.ManagedCallReplacement != null )
				{
					func = f.ManagedCallReplacement.Invoke();
				}

				WriteLines( func );
			}
			EndBlock();
			StartBlock( "catch ( System.Exception ___e )" );
			{
				WriteLine( $"{definitions.ExceptionHandlerName}( \"{c.ManagedNamespace}.{c.ManagedName}\", \"{f.Name}\", ___e );" );

				if ( f.HasReturn )
				{
					WriteLine( $"return default;" );
				}
			}
			EndBlock();
		EndBlock();
		WriteLine();

		if ( UseLinuxCompatibleExports )
		{
			// Generate function pointer getter for this export
			WriteLine( $"internal static IntPtr {f.MangledName}_Ptr => GetFunctionPointer( \"{f.MangledName}\", new {f.MangledName}_d( {f.MangledName} ) );" );
			WriteLine();
		}
	}

	private string AttributeWrapFunction( Function function, string code )
	{
		if ( function.HasAttribute( "clientside" ) )
		{
			//code = $"using ( Sandbox.Realm.Client() )\n{{\n\t{code}\n}}";
		}

		if ( function.HasAttribute( "serverside" ) )
		{
			// code = $"using ( Sandbox.Realm.Server() )\n{{\n\t{code}\n}}";
		}

		return code;
	}
}




