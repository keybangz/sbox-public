using Sandbox.Engine;
using System.Threading;

namespace Sandbox;

/// <summary>
/// Holds a collection of clothing items. Won't let you add items that aren't compatible.
/// </summary>
public partial class ClothingContainer
{
	// Debug logging helper for clothing operations
	private static void ClothingLog( string message )
	{
		var timestamp = DateTime.Now.ToString( "HH:mm:ss.fff" );
		var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
		var logLine = $"[CLOTHING {timestamp} T{threadId}] {message}";
		Log.Info( logLine );
		try
		{
			System.IO.File.AppendAllText( "/tmp/sbox_avatar.log", logLine + "\n" );
		}
		catch { }
	}

	/// <summary>
	/// Dresses a skinned model with an outfit. Will apply all the clothes it can immediately, then download any missing clothing.
	/// </summary>
	public async Task ApplyAsync( SkinnedModelRenderer body, CancellationToken token )
	{
		ClothingLog( "ApplyAsync START" );
		try
		{
			if ( !body.IsValid() )
			{
				ClothingLog( "ApplyAsync - body not valid, returning early" );
				return;
			}

			bool isMenu = GlobalContext.Current == GlobalContext.Menu;
			ClothingLog( $"ApplyAsync - isMenu={isMenu}, Clothing.Count={Clothing.Count}" );

			var scene = body.Scene;

			// apply any changes that we can, immediately
			ClothingLog( "ApplyAsync - calling Apply() (sync)" );
			Apply( body );
			ClothingLog( "ApplyAsync - Apply() complete" );

			bool hasChanges = false;

			//
			// Find any clothing that needs downloading
			// Download it, and apply it to the container.
			//
			var itemsToDownload = Clothing.Where( x => x.Clothing == null || string.IsNullOrEmpty( x.Clothing.ResourcePath ) ).ToArray();
			ClothingLog( $"ApplyAsync - items needing download: {itemsToDownload.Length}" );

			foreach ( var item in itemsToDownload )
			{
				if ( item.ItemDefinitionId == 0 ) continue;

				ClothingLog( $"ApplyAsync - processing item {item.ItemDefinitionId}" );
				var def = Sandbox.Services.Inventory.FindDefinition( item.ItemDefinitionId );
				if ( def == null )
				{
					Log.Warning( $"FindDefinition null : {item.ItemDefinitionId}" );
					continue;
				}

				Sandbox.Clothing clothing = default;


				//
				// If we're in the menu we can't just use Cloud.Load because the package and resource will be loaded
				// in the GAME resource system instead of the menu resource system.
				//
				if ( isMenu )
				{
					ClothingLog( $"ApplyAsync - menu context, trying ResourceSystem.Get for {def.Asset}" );
					clothing = GlobalContext.Menu.ResourceSystem.Get<Clothing>( def.Asset );

					if ( clothing != null )
					{
						ClothingLog( $"ApplyAsync - found in cache: {def.Asset}" );
						item.Clothing = clothing;
						hasChanges = true;
						continue;
					}

					ClothingLog( $"ApplyAsync - not in cache, installing package {def.PackageIdent}" );
					var o = new PackageLoadOptions
					{
						PackageIdent = def.PackageIdent,
						ContextTag = "menu",
						CancellationToken = token
					};

					// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
					ClothingLog( $"ApplyAsync - awaiting PackageManager.InstallAsync..." );
					var activePackage = await PackageManager.InstallAsync( o ).ConfigureAwait( false );
					ClothingLog( $"ApplyAsync - InstallAsync returned, activePackage={(activePackage != null ? "valid" : "NULL")}" );

					if ( activePackage == null )
					{
						Log.Warning( $"Error installing clothing package {def.PackageIdent}" );
						continue;
					}

					var primaryasset = activePackage.Package.PrimaryAsset;
					ClothingLog( $"ApplyAsync - mounting filesystem and loading {primaryasset}" );

					GlobalContext.Menu.FileMount.Mount( activePackage.FileSystem );

					clothing = GlobalContext.Menu.ResourceSystem.LoadGameResource<Clothing>( primaryasset, activePackage.FileSystem );
					ClothingLog( $"ApplyAsync - LoadGameResource returned: {(clothing != null ? "valid" : "NULL")}" );

					// these should match - else we wno't be able to find them later
					if ( primaryasset != def.Asset )
					{
						Log.Warning( $"Clothing primary assets don't match for {def.PackageIdent} ({primaryasset} vs {def.Asset})" );
					}
				}
				else
				{
					// Cloud.Load is always going to load them in the global context, so we need to switch to that context here
					ClothingLog( $"ApplyAsync - game context, trying ResourceSystem.Get for {def.Asset}" );
					clothing = GlobalContext.Game.ResourceSystem.Get<Clothing>( def.Asset );

					if ( clothing != null )
					{
						ClothingLog( $"ApplyAsync - found in cache: {def.Asset}" );
						item.Clothing = clothing;
						hasChanges = true;
						continue;
					}

					// ConfigureAwait(false) prevents SynchronizationContext capture deadlocks on Linux
					ClothingLog( $"ApplyAsync - awaiting Cloud.Load for {def.PackageIdent}" );
					clothing = await Cloud.Load<Clothing>( def.PackageIdent ).ConfigureAwait( false );
					ClothingLog( $"ApplyAsync - Cloud.Load returned: {(clothing != null ? "valid" : "NULL")}" );
				}

				if ( clothing is null )
				{
					Log.Warning( $"Clothing from package was null: {def.PackageIdent}" );
					continue;
				}


				token.ThrowIfCancellationRequested();

				if ( !body.IsValid() )
				{
					ClothingLog( "ApplyAsync - body became invalid during async operations" );
					return;
				}

				if ( clothing != null )
				{
					item.Clothing = clothing;
					hasChanges = true;
				}
			}

			ClothingLog( $"ApplyAsync - download loop complete, hasChanges={hasChanges}" );

			using ( scene.Push() )
			{
				//
				// If we have any changes, then re-apply all the clothing to the container
				// so that things get removed if they don't work with other items (but in the right order).
				// Then apply them to the target renderer.
				//
				if ( hasChanges )
				{
					ClothingLog( "ApplyAsync - re-applying clothing after downloads" );
					foreach ( var entry in Clothing.ToArray() )
					{
						Add( entry );
					}

					Apply( body );
					ClothingLog( "ApplyAsync - re-apply complete" );
				}
			}

			ClothingLog( "ApplyAsync END - SUCCESS" );
		}
		catch ( System.Exception e )
		{
			ClothingLog( $"ApplyAsync EXCEPTION: {e.GetType().Name}: {e.Message}" );
			ClothingLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
	}

	/// <summary>
	/// Dress a skinned model renderer with an outfit. Doesn't download missing clothing.
	/// </summary>
	public void Apply( SkinnedModelRenderer body )
	{
		ClothingLog( "Apply (sync) START" );
		try
		{
			bool isHuman = DetermineHuman( body );
			ClothingLog( $"Apply - isHuman={isHuman}, body.Model={body.Model?.Name ?? "null"}" );

			// remove out outfit
			ClothingLog( "Apply - calling Reset()" );
			Reset( body );
			Normalize();

			// apply indicentals
			ClothingLog( "Apply - setting body properties" );
			body.Set( "scale_height", Height.Remap( 0, 1, 0.8f, 1.2f, true ) );
			body.Set( "scale_heel", Clothing?.Select( x => x.Clothing?.HeelHeight ?? 0.0f ).DefaultIfEmpty( 0 ).Max() ?? 0.0f );

			// TODO - we should expose the render attributes, somehow, in a way accessible to editor inspector and serialization!
			body.Attributes.Set( "skin_age", Age );
			body.Attributes.Set( "skin_tint", Tint );

			// Clean the clothing. Remove any invalid items, any items with broken models
			// any items that can't be worn with other items.
			ClothingLog( $"Apply - filtering {Clothing?.Count ?? 0} clothing items" );
			List<ClothingContainer.ClothingEntry> set = Clothing?
															.Where( x => IsValidClothing( x, isHuman ) )
															.ToList() ?? new();
			ClothingLog( $"Apply - {set.Count} valid clothing items after filtering" );

			TagSet tags = new();

			Material skinMaterial = default;
			Material eyesMaterial = default;

			//
			// apply alternate human skin, if we have one
			//
			if ( isHuman )
			{
				skinMaterial = set.Select( x => x.Clothing.HumanSkinMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
				eyesMaterial = set.Select( x => x.Clothing.HumanEyesMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();

				tags.Add( "human" );

				var humanskin = set.Where( x => x.Clothing.HasHumanSkin ).FirstOrDefault();
				if ( humanskin is not null && Model.Load( humanskin.Clothing.HumanSkinModel ) is Model model && model.IsValid() )
				{
					body.Model = model;
					tags.Add( humanskin.Clothing.HumanSkinTags );

					body.BodyGroups = humanskin.Clothing.HumanSkinBodyGroups;
					body.MaterialGroup = humanskin.Clothing.HumanSkinMaterialGroup;
				}
				else
				{
					body.BodyGroups = body.Model.Parts.DefaultMask;
					body.MaterialGroup = "default";
				}
			}
			else
			{
				skinMaterial = set.Select( x => x.Clothing.SkinMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
				eyesMaterial = set.Select( x => x.Clothing.EyesMaterial ).Where( x => !string.IsNullOrWhiteSpace( x ) ).Select( x => Material.Load( x ) ).FirstOrDefault();
			}

			body.SetMaterialOverride( skinMaterial, "skin" );
			body.SetMaterialOverride( eyesMaterial, "eyes" );

			if ( isHuman )
			{
				EnsureHumanUnderwear( set, tags.Has( "female" ) );
			}

			//
			// Create clothes models
			//
			ClothingLog( $"Apply - creating {set.Count} clothing models" );
			int modelIndex = 0;
			foreach ( var entry in set )
			{
				var c = entry.Clothing;
				modelIndex++;

				var modelPath = c.GetModel( set.Select( x => x.Clothing ).Except( new[] { c } ), tags );
				ClothingLog( $"Apply - [{modelIndex}/{set.Count}] {c.ResourceName}: modelPath={modelPath ?? "null"}" );

				if ( string.IsNullOrEmpty( modelPath ) || !string.IsNullOrEmpty( c.SkinMaterial ) )
				{
					ClothingLog( $"Apply - [{modelIndex}] skipping (empty path or skin material)" );
					continue;
				}

				ClothingLog( $"Apply - [{modelIndex}] loading model..." );
				var model = Model.Load( modelPath );
				if ( !model.IsValid() || model.IsError )
				{
					ClothingLog( $"Apply - [{modelIndex}] model invalid/error, skipping" );
					continue;
				}

				ClothingLog( $"Apply - [{modelIndex}] creating GameObject and SkinnedModelRenderer" );
				var go = new GameObject( false, $"Clothing - {c.ResourceName}" );
				go.Parent = body.GameObject;
				go.Tags.Add( "clothing" );

				var r = go.Components.Create<SkinnedModelRenderer>();
				r.Model = model;
				r.BoneMergeTarget = body;

				// TODO - we should expose the render attributes, somehow, in a way accessible to editor inspector and serialization!
				r.Attributes.Set( "skin_age", Age );
				r.Attributes.Set( "skin_tint", Tint );

				r.SetMaterialOverride( skinMaterial, "skin" );
				r.SetMaterialOverride( eyesMaterial, "eyes" );

				if ( !string.IsNullOrEmpty( c.MaterialGroup ) )
					r.MaterialGroup = c.MaterialGroup;

				if ( c.AllowTintSelect )
				{
					var tintValue = entry.Tint?.Clamp( 0, 1 ) ?? c.TintDefault;
					var tintColor = c.TintSelection.Evaluate( tintValue );
					r.Tint = tintColor;
				}

				go.Enabled = true;
				ClothingLog( $"Apply - [{modelIndex}] model created successfully" );
			}

			//
			// Set body groups
			//
			ClothingLog( "Apply - setting body groups" );
			foreach ( var (name, value) in GetBodyGroups( set.Select( x => x.Clothing ) ) )
			{
				if ( value == 0 ) continue;

				body.SetBodyGroup( name, value );
			}

			ClothingLog( "Apply (sync) END - SUCCESS" );
		}
		catch ( System.Exception e )
		{
			ClothingLog( $"Apply (sync) EXCEPTION: {e.GetType().Name}: {e.Message}" );
			ClothingLog( $"  Stack: {e.StackTrace}" );
			throw;
		}
	}

	/// <summary>
	/// Clear the outfit from this model, make it named
	/// </summary>
	public void Reset( SkinnedModelRenderer body )
	{
		//
		// Start with defaults
		//
		body.Set( "scale_height", 1.0f );
		body.MaterialGroup = "default";
		body.MaterialOverride = null;
		body.BodyGroups = body.Model?.Parts.DefaultMask ?? 0;

		//
		// Remove old models
		//
		foreach ( var children in body.GameObject.Children )
		{
			if ( children.Tags.Has( "clothing" ) )
			{
				children.Destroy();
			}
		}
	}

	// Default underwear paths, cached to avoid repeated allocations
	const string DefaultUnderwearPath = "models/citizen_clothes/underwear/y_front_pants/y_front_pants_white.clothing";
	const string DefaultBraPath = "models/citizen_clothes/underwear/bra/bra_white.clothing";
	static readonly Lazy<Sandbox.Clothing> DefaultUnderwear = new( () => ResourceLibrary.Get<Sandbox.Clothing>( DefaultUnderwearPath ) );
	static readonly Lazy<Sandbox.Clothing> DefaultBra = new( () => ResourceLibrary.Get<Sandbox.Clothing>( DefaultBraPath ) );

	static bool DetermineHuman( SkinnedModelRenderer b, bool defaultValue = false )
	{
		if ( b?.Model is null ) return defaultValue;

		var model = b.Model.BaseModel ?? b.Model;
		return !model.Name.Contains( "citizen.vmdl", StringComparison.OrdinalIgnoreCase );
	}

	static int _isValidModelCallCount = 0;

	static bool IsValidModel( string modelName )
	{
		var callId = System.Threading.Interlocked.Increment( ref _isValidModelCallCount );
		ClothingLog( $"IsValidModel[{callId}] START: '{modelName}'" );

		try
		{
			if ( string.IsNullOrWhiteSpace( modelName ) )
			{
				ClothingLog( $"IsValidModel[{callId}] - empty model name, returning false" );
				return false;
			}

			ClothingLog( $"IsValidModel[{callId}] - calling Model.Load..." );
			var model = Model.Load( modelName );
			ClothingLog( $"IsValidModel[{callId}] - Model.Load returned: IsValid={model?.IsValid()}, IsError={model?.IsError}" );

			if ( !model.IsValid() )
			{
				ClothingLog( $"IsValidModel[{callId}] - model not valid, returning false" );
				return false;
			}
			if ( model.IsError )
			{
				ClothingLog( $"IsValidModel[{callId}] - model has error, returning false" );
				return false;
			}

			ClothingLog( $"IsValidModel[{callId}] END - returning true" );
			return true;
		}
		catch ( System.Exception ex )
		{
			ClothingLog( $"IsValidModel[{callId}] EXCEPTION: {ex.GetType().Name}: {ex.Message}" );
			return false;
		}
	}

	static void EnsureHumanUnderwear( List<ClothingEntry> set, bool isFemale )
	{
		bool hasUnderwear = set.Any( x => x.Clothing.Category is Sandbox.Clothing.ClothingCategory.Underwear or Sandbox.Clothing.ClothingCategory.Underpants );
		if ( !hasUnderwear )
			TryAddDefault( set, DefaultUnderwear.Value, isHuman: true );

		if ( isFemale && !set.Any( x => x.Clothing.Category == Sandbox.Clothing.ClothingCategory.Bra ) )
			TryAddDefault( set, DefaultBra.Value, isHuman: true );
	}

	static void TryAddDefault( List<ClothingEntry> set, Sandbox.Clothing clothing, bool isHuman )
	{
		if ( clothing is null ) return;

		var entry = new ClothingEntry( clothing );

		if ( !IsValidClothing( entry, isHuman ) ) return;
		if ( set.Any( x => !(x.Clothing?.CanBeWornWith( clothing ) ?? true) ) ) return;

		set.Add( entry );
	}

	static int _isValidClothingCallCount = 0;

	static bool IsValidClothing( ClothingContainer.ClothingEntry e, bool targetIsHuman )
	{
		var callId = System.Threading.Interlocked.Increment( ref _isValidClothingCallCount );
		var clothingName = e?.Clothing?.Title ?? e?.Clothing?.ResourceName ?? "null";
		ClothingLog( $"IsValidClothing[{callId}] START: '{clothingName}', targetIsHuman={targetIsHuman}" );

		try
		{
			if ( e is null )
			{
				ClothingLog( $"IsValidClothing[{callId}] - entry is null, returning false" );
				return false;
			}
			if ( e.Clothing is null )
			{
				ClothingLog( $"IsValidClothing[{callId}] - clothing is null, returning false" );
				return false;
			}
			if ( targetIsHuman && e.Clothing.HasHumanSkin )
			{
				ClothingLog( $"IsValidClothing[{callId}] - has human skin, returning true" );
				return true;
			}

			var model = e.Clothing.Model;
			ClothingLog( $"IsValidClothing[{callId}] - Model='{model ?? "null"}', HumanAltModel='{e.Clothing.HumanAltModel ?? "null"}'" );

			if ( targetIsHuman )
			{
				model = e.Clothing.HumanAltModel;

				// If we have a citizen model, but not a human model, make clothing invalid
				if ( string.IsNullOrEmpty( model ) && !string.IsNullOrEmpty( e.Clothing.Model ) )
				{
					ClothingLog( $"IsValidClothing[{callId}] - has citizen model but no human model, returning false" );
					return false;
				}
			}

			if ( string.IsNullOrEmpty( model ) )
			{
				ClothingLog( $"IsValidClothing[{callId}] - empty model (allowed), returning true" );
				return true;
			}

			ClothingLog( $"IsValidClothing[{callId}] - checking if model is valid..." );
			if ( !IsValidModel( model ) )
			{
				ClothingLog( $"IsValidClothing[{callId}] - model invalid, returning false" );
				Log.Warning( $"Clothing model '{model}' in {e.Clothing} is invalid, removing" );
				return false;
			}

			ClothingLog( $"IsValidClothing[{callId}] END - returning true" );
			return true;
		}
		catch ( System.Exception ex )
		{
			ClothingLog( $"IsValidClothing[{callId}] EXCEPTION: {ex.GetType().Name}: {ex.Message}" );
			return false;
		}
	}
}
