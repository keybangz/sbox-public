using System.IO;
using Facepunch;
using Facepunch.Steps;

namespace Facepunch.Pipelines;

internal class Build
{
	public static Pipeline Create( BuildConfiguration configuration = BuildConfiguration.Developer,
								 bool clean = false,
								 bool skipNative = false,
								 bool skipManaged = false,
								 bool pullArtifacts = false )
	{
		var builder = new PipelineBuilder( "Build" );
		var isPublicSource = IsPublicSourceDistribution();

		if ( pullArtifacts )
		{
			Log.Info( "Downloading public artifacts..." );
			builder.AddStep( new DownloadPublicArtifacts( "Download Public Artifacts" ) );
		}

		// Native build requires the full source tree (VPC, src/, etc.). In a public
		// source distribution the native toolchain isn't available, so skip native
		// build whenever we're not pulling pre-built artifacts. The --pull-artifacts
		// flag makes artifact download opt-in; without it we just build managed code.
		var shouldSkipNative = skipNative || isPublicSource;

		// Always add interop gen
		builder.AddStep( new Steps.InteropGen( "Interop Gen", isPublicSource ) );

		if ( !isPublicSource )
		{
			builder.AddStep( new Steps.ShaderProc( "Shader Proc" ) );
		}

		// Add native build step if not skipped
		if ( !shouldSkipNative )
		{
			builder.AddStep( new Steps.BuildVpc( "Build VPC" ) );

			builder.AddStep( new GenerateSolutions( "Generate Solutions", configuration ) );

			builder.AddStep( new BuildNative( "Build Native", configuration, clean ) );
		}

		// Add managed build step if not skipped
		if ( !skipManaged )
		{
			builder.AddStep( new BuildManaged( "Build Managed", clean ) );
		}

		return builder.Build();
	}

	private static bool IsPublicSourceDistribution()
	{
		var repoRoot = Path.TrimEndingDirectorySeparator( Path.GetFullPath( Directory.GetCurrentDirectory() ) );
		// Those are only included in the full source distribution
		var publicDir = Path.Combine( repoRoot, "public" );
		var steamworksDir = Path.Combine( repoRoot, "steamworks" );
		return !Directory.Exists( publicDir ) || !Directory.Exists( steamworksDir );
	}
}
