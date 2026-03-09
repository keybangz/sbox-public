using System.Collections.Generic;
using System.Linq;

namespace Facepunch.InteropGen;

internal partial class ManagerWriter
{

	private void Exports()
	{
		StartBlock( $"internal static unsafe class Exports" );
		{
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

		WriteLine( "/// <summary>" );
		WriteLine( $"/// {namespc}.{f.Name}( ... )" );
		WriteLine( "/// </summary>" );
		WriteLine( "[UnmanagedCallersOnly]" );
		StartBlock( $"internal static {f.Return.GetManagedDelegateType( true )} {f.MangledName}( {nativeArgS.Trim( ',', ' ' )} )" );
		{
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


		}
		EndBlock();
		WriteLine();

		if ( definitions.InitFrom == "Managed" )
		{
			WriteLine( $"internal delegate {f.Return.GetManagedDelegateType( true )} {f.MangledName}_d( {nativeArgS.Trim( ',', ' ' )} );" );
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




